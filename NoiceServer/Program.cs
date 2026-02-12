using System.Buffers;
using NoiceServer;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Concurrent; // 追加

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:8000");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// アップロードサイズ上限の設定（一箇所だけじゃ足りないんだよな）
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 512 * 1024 * 1024; // 512MB
});

// Kestrel（サーバー本体）のリクエストサイズ制限も広げてやる
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 512 * 1024 * 1024; // 512MB
});

// .NET 8 では IFormFile を使うと自動的にアンチフォージェリ（不正送信防止）が有効になります。
// ローカルツールでトークンとか面倒なので、サービスだけ登録して後で無効化します。
builder.Services.AddAntiforgery();

// ログをファイルにも出力（ビルド前に登録）
// index.html があるフォルダを contentRoot に（dotnet run 時はカレントが NoiceServer になるので親を探す）
static string FindContentRoot()
{
    var cur = Directory.GetCurrentDirectory();
    if (File.Exists(Path.Combine(cur, "index.html"))) return Path.GetFullPath(cur);
    var parent = Path.GetFullPath(Path.Combine(cur, ".."));
    if (File.Exists(Path.Combine(parent, "index.html"))) return parent;
    // bin/Release/net8.0 などから 4 段上 = NOICE_speed_up
    var fromBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    if (File.Exists(Path.Combine(fromBin, "index.html"))) return fromBin;
    return Path.GetFullPath(cur);
}
var contentRoot = FindContentRoot();
var logPath = Path.Combine(contentRoot, "server.log");
var logWriter = new StreamWriter(logPath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
builder.Logging.AddProvider(new FileLoggerProvider(logWriter));

var app = builder.Build();

// 静的ファイルはカレントディレクトリから提供
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(contentRoot),
    RequestPath = ""
});

// アンチフォージェリミドルウェアを追加（これがないとエラーが出る）
app.UseAntiforgery();

var uploadDir = Path.Combine(contentRoot, "uploads");
var outputDir = Path.Combine(contentRoot, "processed_videos");
foreach (var d in new[] { uploadDir, outputDir })
{
    if (!Directory.Exists(d))
        Directory.CreateDirectory(d);
}

// 【起動時クリーンアップ】前回の残骸を消す。サーバー再起動のたびにゴミが消える。
static void CleanDir(string dir)
{
    if (!Directory.Exists(dir)) return;
    foreach (var f in Directory.GetFiles(dir))
    {
        try { File.Delete(f); } catch { /* ロック中なら無視 */ }
    }
}
CleanDir(uploadDir);
CleanDir(outputDir);

// レンダリング進捗管理用
var progressStore = new ConcurrentDictionary<string, double>();

// --- API（Python FastAPI と互換）---

app.MapGet("/", () =>
{
    var indexPath = Path.Combine(contentRoot, "index.html");
    if (!File.Exists(indexPath))
        return Results.NotFound();
    return Results.Content(File.ReadAllText(indexPath), "text/html; charset=utf-8");
});

app.MapGet("/style.css", () =>
{
    var path = Path.Combine(contentRoot, "style.css");
    return File.Exists(path) ? Results.File(path, "text/css") : Results.NotFound();
});

app.MapGet("/main.js", () =>
{
    var path = Path.Combine(contentRoot, "main.js");
    return File.Exists(path) ? Results.File(path, "application/javascript") : Results.NotFound();
});

app.MapGet("/logs", () =>
{
    if (!File.Exists(logPath))
        return Results.Json(new { logs = "System Initialized." });

    try
    {
        // 【修正】File.ReadAllLines だと書き込み中のファイルを開けなくて怒られることがあります。
        // FileShare.ReadWrite を指定することで、ロガーが書き込み中でも読み取れるようにします。
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var content = sr.ReadToEnd();
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        var last = string.Join("\n", lines.Length <= 10 ? lines : lines.TakeLast(10));
        return Results.Json(new { logs = last });
    }
    catch (Exception ex)
    {
        return Results.Json(new { logs = $"Error reading logs: {ex.Message}" });
    }
});

app.MapPost("/upload", async (IFormFile file) =>
{
    if (file == null)
        return Results.BadRequest();
    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var safeName = string.Join("_", file.FileName.Split(Path.GetInvalidFileNameChars()));
    var fileName = $"void_{ts}_{safeName}";
    var path = Path.Combine(uploadDir, fileName);
    await using (var stream = File.Create(path))
        await file.CopyToAsync(stream);
    return Results.Json(new { temp_name = fileName, output_name = $"noice_void_{ts}.mp4" });
}).DisableAntiforgery(); // 【修正】ローカルで使うだけなので、ブラウザのセキュリティチェックを無効化します。

app.MapGet("/stream/{tempName}/{outputName}", async (
    string tempName,
    string outputName,
    ILogger<object> log,
    HttpContext ctx,
    double scale = 0.5,
    bool is_color = true,
    double speed = 1.0,
    bool nitro = false) => // 【高速化】Nitroモードのスイッチを追加
{
    var tempPath = Path.Combine(uploadDir, tempName);
    if (!File.Exists(tempPath))
        return Results.NotFound();

    ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
    await ctx.Response.StartAsync();

    try
    {
        await foreach (var jpegBytes in NoiceProcessor.ProcessVoidStreamAsync(tempPath, scale, is_color, speed, nitro, log, ctx.RequestAborted))
        {
            var boundary = "\r\n--frame\r\nContent-Type: image/jpeg\r\n\r\n";
            var preamble = System.Text.Encoding.ASCII.GetBytes(boundary);
            await ctx.Response.Body.WriteAsync(preamble);
            await ctx.Response.Body.WriteAsync(jpegBytes);
            await ctx.Response.Body.WriteAsync(System.Text.Encoding.ASCII.GetBytes("\r\n"));
            await ctx.Response.Body.FlushAsync();
        }
    }
    finally
    {
        // 配信が終わったら一時ファイルを消す（ストレージがパンパンになるのを防ぐ）
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
    }

    return Results.Empty;
});

app.MapGet("/process_download/{tempName}/{outputName}", (
    string tempName,
    string outputName,
    ILogger<object> log,
    double scale = 1.0,
    bool is_color = true,
    string audio_mode = "mute",
    bool nitro = false) =>
{
    var tempPath = Path.Combine(uploadDir, tempName);
    var outPath = Path.Combine(outputDir, outputName);
    if (!File.Exists(tempPath))
        return Results.NotFound();

    progressStore[outputName] = 0; // 進捗リセット

    try
    {
        NoiceProcessor.SaveProcessedVideo(tempPath, outPath, scale, is_color, audio_mode, nitro, log, p => 
        {
            progressStore[outputName] = p; // 進捗更新
        });
        return Results.Json(new { status = "completed", url = $"/download/{outputName}" });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Rendering failed");
        return Results.Json(new { status = "error", message = ex.Message });
    }
    finally
    {
        progressStore.TryRemove(outputName, out _); // 終わったら消す
        // 【修正】レンダリング完了後、アップロードされた元動画を消す
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
    }
});

app.MapGet("/progress/{outputName}", (string outputName) =>
{
    if (progressStore.TryGetValue(outputName, out var p))
        return Results.Json(new { progress = p });
    return Results.Json(new { progress = -1 }); // 存在しない
});

app.MapGet("/download/{filename}", async (string filename, HttpContext ctx) =>
{
    var path = Path.Combine(outputDir, filename);
    if (!File.Exists(path))
        return Results.Json(new { error = "File not found." });

    // ファイルを返した後、少し待ってから削除する（転送完了を待つ）
    var result = Results.File(path, "video/mp4", filename);
    // バックグラウンドで遅延削除
    _ = Task.Run(async () =>
    {
        await Task.Delay(10000); // 10秒待ってから消す
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    });
    return result;
});

// 【新規】手動クリーンアップ: RESET ボタンから呼べるゴミ掃除エンドポイント
app.MapDelete("/cleanup", () =>
{
    int deleted = 0;
    foreach (var dir in new[] { uploadDir, outputDir })
    {
        if (!Directory.Exists(dir)) continue;
        foreach (var f in Directory.GetFiles(dir))
        {
            try { File.Delete(f); deleted++; } catch { }
        }
    }
    return Results.Json(new { status = "ok", deleted });
});

app.Run();

/// <summary>
/// ログを server.log に追記するプロバイダ
/// </summary>
file class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;

    public FileLoggerProvider(StreamWriter writer) => _writer = writer;

    public ILogger CreateLogger(string categoryName) => new FileLogger(_writer, categoryName);

    public void Dispose() => _writer?.Dispose();
}

file class FileLogger : ILogger
{
    private readonly StreamWriter _writer;
    private readonly string _category;

    public FileLogger(StreamWriter writer, string category)
    {
        _writer = writer;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {msg}";
        try
        {
            _writer.WriteLine(line);
        }
        catch { /* ignore */ }
    }
}
