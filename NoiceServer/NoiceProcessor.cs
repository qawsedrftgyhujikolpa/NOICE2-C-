using OpenCvSharp;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace NoiceServer;

/// <summary>
/// 動画から背景を消去し、動きをノイズで可視化する処理
/// ── 極限爆速化版 ──
/// </summary>
public static class NoiceProcessor
{
    // ノイズプール枚数: 500→30 に大幅削減。見た目にはほぼ差なし、起動が爆速になる。
    private const int NoisePoolSize = 30;

    // MOG2 のパラメータ（通常モード用）
    private const int Mog2History = 300;
    private const int Mog2VarThreshold = 60;

    // ストリーミング時の JPEG 品質。95(デフォルト)→70 に落としてエンコード速度と転送速度を稼ぐ。
    private static readonly int[] JpegStreamParams = { (int)ImwriteFlags.JpegQuality, 70 };
    // ダウンロード用は少し品質を上げる（どうせ再エンコードするが）。
    private static readonly int[] JpegSaveParams = { (int)ImwriteFlags.JpegQuality, 85 };

    /// <summary>
    /// ノイズプール生成（並列+ブラー廃止で爆速）
    /// 30枚あれば十分。ランダムノイズにブラーかけても誰も気づかないから廃止。
    /// </summary>
    public static Mat[] CreateNoisePool(int w, int h, bool isColor, ILogger? log = null)
    {
        log?.LogInformation("🌀 ノイズプール生成: {Size}枚 ({W}x{H})", NoisePoolSize, w, h);
        var pool = new Mat[NoisePoolSize];

        // 全コアで並列生成
        Parallel.For(0, NoisePoolSize, i =>
        {
            if (isColor)
            {
                var noise = new Mat(h, w, MatType.CV_8UC3);
                Cv2.Randu(noise, new Scalar(0, 0, 0), new Scalar(256, 256, 256));
                // GaussianBlur は廃止。ノイズにブラーをかけても見た目変わらん。
                pool[i] = noise;
            }
            else
            {
                var gray = new Mat(h, w, MatType.CV_8UC1);
                Cv2.Randu(gray, new Scalar(0), new Scalar(256));
                var noise = new Mat();
                Cv2.CvtColor(gray, noise, ColorConversionCodes.GRAY2BGR);
                gray.Dispose();
                pool[i] = noise;
            }
        });

        log?.LogInformation("✅ Pool生成完了");
        return pool;
    }

    /// <summary>
    /// 1フレーム処理（極限最適化版）
    /// mask と maskDilate はバッファとして外から渡してもらい、毎フレーム new しない。
    /// </summary>
    public static void ProcessFrame(
        Mat frame,
        Mat[] pool,
        Mat staticNoise,
        object? detector,
        int poolIndex,
        Mat result,       // 出力バッファ（外部で確保済み）
        Mat mask,         // マスクバッファ（外部で確保済み）
        Mat maskDilate,   // 膨張マスクバッファ（外部で確保済み）
        bool nitroMode,
        Mat? prevGray,
        Mat? grayBuf)     // グレースケール変換用バッファ
    {
        bool hasMask = false;

        if (nitroMode && prevGray != null && grayBuf != null)
        {
            // --- Nitro Mode: フレーム間差分（最速） ---
            Cv2.CvtColor(frame, grayBuf, ColorConversionCodes.BGR2GRAY);
            Cv2.Absdiff(grayBuf, prevGray, mask);
            Cv2.Threshold(mask, mask, 25, 255, ThresholdTypes.Binary);
            grayBuf.CopyTo(prevGray);
            hasMask = true;
        }
        else if (detector is BackgroundSubtractorMOG2 backSub)
        {
            // --- Normal Mode: 背景差分法 ---
            // ブラーのカーネルを 9→5 にさらに小さく。精度は少し落ちるが速度優先。
            Cv2.GaussianBlur(frame, mask, new OpenCvSharp.Size(5, 5), 0);
            backSub.Apply(mask, mask);
            hasMask = true;
        }

        // ノイズ合成
        staticNoise.CopyTo(result);
        if (hasMask)
        {
            // Nitro時は膨張（Dilate）もスキップ可。見た目よりスピード優先。
            if (nitroMode)
            {
                // Dilate なしで直接合成 → さらに高速
                pool[poolIndex % NoisePoolSize].CopyTo(result, mask);
            }
            else
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
                Cv2.Dilate(mask, maskDilate, kernel, iterations: 1);
                pool[poolIndex % NoisePoolSize].CopyTo(result, maskDilate);
            }
        }
    }

    /// <summary>
    /// ストリーミング用（極限爆速版）
    /// フレームスキップ + 低品質JPEGで帯域と処理時間を大幅カット。
    /// </summary>
    public static async IAsyncEnumerable<byte[]> ProcessVoidStreamAsync(
        string tempPath,
        double scale,
        bool isColor,
        double speed,
        bool nitroMode,
        ILogger? log,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var cap = new VideoCapture(tempPath);
        if (!cap.IsOpened()) yield break;

        double fps = cap.Get(VideoCaptureProperties.Fps);
        if (fps <= 0) fps = 30.0;
        int w = (int)(cap.Get(VideoCaptureProperties.FrameWidth) * scale);
        int h = (int)(cap.Get(VideoCaptureProperties.FrameHeight) * scale);

        // speed > 1 の場合、フレームを飛ばして物理的な処理量を減らす
        // 例: speed=2.0 → 1フレームおきにスキップ
        int skipEvery = speed > 1.0 ? (int)Math.Round(speed) : 1;

        var pool = CreateNoisePool(w, h, isColor, log);
        try
        {
            using var staticNoise = pool[0].Clone();
            using var backSub = nitroMode ? null : BackgroundSubtractorMOG2.Create(Mog2History, Mog2VarThreshold, false);
            using var prevGray = nitroMode ? new Mat(h, w, MatType.CV_8UC1, new Scalar(0)) : null;
            using var grayBuf = nitroMode ? new Mat() : null;

            double frameDelay = 1.0 / (fps * speed);
            int pIdx = 0;
            int frameCount = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 全バッファを外で確保して使い回す（GCを極限まで減らす）
            using var frame = new Mat();
            using var resized = new Mat();
            using var result = new Mat();
            using var mask = new Mat();
            using var maskDilate = new Mat();

            while (cap.Read(frame) && !frame.Empty() && !cancellationToken.IsCancellationRequested)
            {
                frameCount++;

                // フレームスキップ: speed > 1 なら間引く
                if (skipEvery > 1 && (frameCount % skipEvery) != 0)
                    continue;

                sw.Restart();

                // Nearest 補間 = 最速のリサイズ方式
                Cv2.Resize(frame, resized, new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Nearest);

                ProcessFrame(resized, pool, staticNoise, (object?)backSub ?? prevGray!, pIdx, result, mask, maskDilate, nitroMode, prevGray, grayBuf);

                // JPEG品質70でエンコード（ストリーミングだし許せ）
                Cv2.ImEncode(".jpg", result, out byte[] jpegBytes, JpegStreamParams);
                yield return jpegBytes;

                pIdx++;

                double wait = frameDelay - sw.Elapsed.TotalSeconds;
                if (wait > 0)
                    await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var m in pool) m.Dispose();
        }
    }

    /// <summary>
    /// ダウンロード用（極限爆速版）
    /// </summary>
    public static void SaveProcessedVideo(string tempPath, string outputPath, double scale, bool isColor, string audioMode, bool nitroMode, ILogger log, Action<double>? onProgress = null)
    {
        using var cap = new VideoCapture(tempPath);
        if (!cap.IsOpened()) throw new InvalidOperationException("Failed to open video");

        double fps = cap.Get(VideoCaptureProperties.Fps);
        if (fps <= 0) fps = 30.0;
        int w = (int)(cap.Get(VideoCaptureProperties.FrameWidth) * scale);
        int h = (int)(cap.Get(VideoCaptureProperties.FrameHeight) * scale);
        int totalFrames = (int)cap.Get(VideoCaptureProperties.FrameCount);

        string tempSilent = outputPath + ".silent.mp4";
        var pool = CreateNoisePool(w, h, isColor, log);
        try
        {
            using var writer = new VideoWriter(tempSilent, FourCC.MP4V, fps, new OpenCvSharp.Size(w, h));
            using var staticNoise = pool[0].Clone();
            using var backSub = nitroMode ? null : BackgroundSubtractorMOG2.Create(Mog2History, Mog2VarThreshold, false);
            using var prevGray = nitroMode ? new Mat(h, w, MatType.CV_8UC1, new Scalar(0)) : null;
            using var grayBuf = nitroMode ? new Mat() : null;

            // 全バッファ外部確保
            using var frame = new Mat();
            using var resized = new Mat();
            using var result = new Mat();
            using var mask = new Mat();
            using var maskDilate = new Mat();

            int pIdx = 0;
            while (cap.Read(frame) && !frame.Empty())
            {
                // ダウンロードは全フレーム処理（品質優先）、ただし Nearest で速度稼ぐ
                Cv2.Resize(frame, resized, new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Nearest);
                ProcessFrame(resized, pool, staticNoise, (object?)backSub ?? prevGray!, pIdx, result, mask, maskDilate, nitroMode, prevGray, grayBuf);

                writer.Write(result);
                pIdx++;
                if (pIdx % 30 == 0)
                {
                    double pct = (double)pIdx / totalFrames * 100;
                    log.LogInformation(" rendering... {PIdx}/{Total} ({Pct:F1}%)", pIdx, totalFrames, pct);
                    onProgress?.Invoke(pct);
                }
            }
            onProgress?.Invoke(100);
        }
        finally
        {
            foreach (var m in pool) m.Dispose();
        }

        cap.Release();

        // 音声ミックス（FFmpeg）
        try
        {
            MuxAudio(tempPath, tempSilent, outputPath, audioMode, fps, log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Audio mixing failed");
            if (File.Exists(tempSilent) && !File.Exists(outputPath))
                File.Move(tempSilent, outputPath);
        }

        try { if (File.Exists(tempSilent)) File.Delete(tempSilent); } catch { }
        log.LogInformation("✨ Rendering complete.");
    }

    private static void MuxAudio(string originalPath, string silentVideoPath, string outputPath, string audioMode, double fps, ILogger log)
    {
        static int RunFfmpeg(string args)
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                }
            };
            p.Start();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }

        if (audioMode == "mute")
        {
            if (RunFfmpeg($"-y -i \"{silentVideoPath}\" -c:v libx264 \"{outputPath}\"") != 0)
            {
                if (File.Exists(silentVideoPath))
                    File.Move(silentVideoPath, outputPath);
            }
            return;
        }

        if (audioMode == "original")
        {
            if (RunFfmpeg($"-y -i \"{silentVideoPath}\" -i \"{originalPath}\" -c:v libx264 -map 0:v -map 1:a? -c:a aac -shortest \"{outputPath}\"") != 0)
            {
                if (File.Exists(silentVideoPath))
                    File.Move(silentVideoPath, outputPath);
            }
            return;
        }

        if (audioMode is "white" or "brown")
        {
            double duration = 0;
            using (var pp = new System.Diagnostics.Process())
            {
                pp.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{silentVideoPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                pp.Start();
                var outStr = pp.StandardOutput.ReadToEnd().Trim();
                pp.WaitForExit();
                double.TryParse(outStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out duration);
            }
            if (duration <= 0) duration = 60;
            string noiseType = audioMode == "white" ? "white" : "pink";
            string filter = $"-filter_complex \"[0:v]copy[v];[1:a]apad[a]\" -map \"[v]\" -map \"[a]\" -shortest -c:a aac";
            if (RunFfmpeg($"-y -i \"{silentVideoPath}\" -f lavfi -i anoisesrc=c={noiseType}:d={duration}:r=44100 {filter} \"{outputPath}\"") != 0)
            {
                if (File.Exists(silentVideoPath))
                    File.Move(silentVideoPath, outputPath);
            }
            return;
        }

        if (File.Exists(silentVideoPath))
            File.Move(silentVideoPath, outputPath);
    }
}
