using OpenCvSharp;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace NoiceServer;

/// <summary>
/// 動画から背景を消去し、動きをノイズで可視化する処理（Python server.py と同等ロジック）
/// </summary>
public static class NoiceProcessor
{
    private const int NoisePoolSize = 500;
    private const int Mog2History = 300;
    private const int Mog2VarThreshold = 60;
    private const int BlurKernel = 15;
    private const int MedianKernel = 5;
    private const int DilateIterations = 2;

    /// <summary>
    /// 高密度ノイズプールを生成（並列処理で爆速化）
    /// </summary>
    public static List<Mat> CreateNoisePool(int w, int h, bool isColor, ILogger? log = null)
    {
        log?.LogInformation("🌀 RAM極限しばきモード: {Size}個の巨大ノイズテクスチャを生成中...", NoisePoolSize);
        var pool = new Mat[NoisePoolSize];
        
        // Parallel.For を使って CPU の全コアでノイズを生成する（C#の本気）
        Parallel.For(0, NoisePoolSize, i =>
        {
            if (isColor)
            {
                var noise = new Mat(h, w, MatType.CV_8UC3);
                Cv2.Randu(noise, new Scalar(0, 0, 0), new Scalar(256, 256, 256));
                Cv2.GaussianBlur(noise, noise, new OpenCvSharp.Size(3, 3), 0);
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

        log?.LogInformation("✅ Pool generation complete.");
        return pool.ToList();
    }

    /// <summary>
    /// 1フレームを処理（最適化版）
    /// </summary>
    public static void ProcessFrame(
        Mat frame, 
        List<Mat> pool, 
        Mat staticNoise, 
        object detector, // BackgroundSubtractorMOG2 または 前のフレーム(Mat)
        int poolIndex, 
        Mat result,      // 前もって確保された出力用バッファ
        bool nitroMode,
        Mat? prevGray = null)
    {
        using var mask = new Mat();
        
        if (nitroMode && prevGray != null)
        {
            // --- Nitro Mode: 単純なフレーム間差分（爆速） ---
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Absdiff(gray, prevGray, mask); // D が小文字の可能性がある
            Cv2.Threshold(mask, mask, 25, 255, ThresholdTypes.Binary);
            gray.CopyTo(prevGray); // 次のフレームのために保存
        }
        else if (detector is BackgroundSubtractorMOG2 backSub)
        {
            // --- Normal Mode: 背景差分法 (MOG2) ---
            using var blurred = new Mat();
            // カーネルサイズを少し小さくして高速化(15->9)
            Cv2.GaussianBlur(frame, blurred, new OpenCvSharp.Size(9, 9), 0);
            backSub.Apply(blurred, mask);
        }

        // ノイズ合成
        staticNoise.CopyTo(result);
        if (!mask.Empty())
        {
            using var maskDilate = new Mat();
            Cv2.Dilate(mask, maskDilate, null, null, 1); // 膨張処理を1回に減らして高速化
            var noiseFrame = pool[poolIndex % NoisePoolSize];
            noiseFrame.CopyTo(result, maskDilate);
        }
    }

    /// <summary>
    /// ストリーミング用: フレームを順次 yield（JPEG バイト + MJPEG 境界）
    /// </summary>
        public static async IAsyncEnumerable<byte[]> ProcessVoidStreamAsync(
        string tempPath,
        double scale,
        bool isColor,
        double speed,
        bool nitroMode, // 追加
        ILogger? log,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var cap = new VideoCapture(tempPath);
        if (!cap.IsOpened()) yield break;

        double fps = cap.Get(VideoCaptureProperties.Fps);
        if (fps <= 0) fps = 30.0;
        int w = (int)(cap.Get(VideoCaptureProperties.FrameWidth) * scale);
        int h = (int)(cap.Get(VideoCaptureProperties.FrameHeight) * scale);

        var pool = CreateNoisePool(w, h, isColor, log);
        try
        {
            using var staticNoise = pool[0].Clone();
            using var backSub = nitroMode ? null : BackgroundSubtractorMOG2.Create(Mog2History, Mog2VarThreshold, false);
            using var prevGray = nitroMode ? new Mat(h, w, MatType.CV_8UC1, new Scalar(0)) : null;

            double frameDelay = 1.0 / (fps * speed);
            int pIdx = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var frame = new Mat();
            using var resized = new Mat();
            using var result = new Mat(); // バッファ再利用

            while (cap.Read(frame) && !frame.Empty() && !cancellationToken.IsCancellationRequested)
            {
                sw.Restart();
                Cv2.Resize(frame, resized, new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Area);
                
                ProcessFrame(resized, pool, staticNoise, (object?)backSub ?? prevGray!, pIdx, result, nitroMode, prevGray);
                
                Cv2.ImEncode(".jpg", result, out byte[] jpegBytes);
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
    /// ダウンロード用: 無音 MP4 を書き出し（最適化版）
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
            
            using var frame = new Mat();
            using var resized = new Mat();
            using var result = new Mat();

            int pIdx = 0;
            while (cap.Read(frame) && !frame.Empty())
            {
                Cv2.Resize(frame, resized, new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Area);
                ProcessFrame(resized, pool, staticNoise, (object?)backSub ?? prevGray!, pIdx, result, nitroMode, prevGray);
                
                writer.Write(result);
                pIdx++;
                if (pIdx % 30 == 0)
                {
                    log.LogInformation(" rendering... {PIdx}/{Total}", pIdx, totalFrames);
                    onProgress?.Invoke((double)pIdx / totalFrames * 100);
                }
            }
            onProgress?.Invoke(100);
        }
        finally
        {
            foreach (var m in pool) m.Dispose();
        }

        cap.Release();

        // 音声ミックス（FFMpegCore）
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
            // 無音動画を libx264 で再エンコード
            if (RunFfmpeg($"-y -i \"{silentVideoPath}\" -c:v libx264 \"{outputPath}\"") != 0)
            {
                if (File.Exists(silentVideoPath))
                    File.Move(silentVideoPath, outputPath);
            }
            return;
        }

        if (audioMode == "original")
        {
            // 動画1 + 音声2 → 出力
            if (RunFfmpeg($"-y -i \"{silentVideoPath}\" -i \"{originalPath}\" -c:v libx264 -map 0:v -map 1:a? -c:a aac -shortest \"{outputPath}\"") != 0)
            {
                if (File.Exists(silentVideoPath))
                    File.Move(silentVideoPath, outputPath);
            }
            return;
        }

        if (audioMode is "white" or "brown")
        {
            // 動画の長さを ffprobe で取得
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
