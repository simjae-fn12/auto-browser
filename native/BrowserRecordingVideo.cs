using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace OinziBrowser;

// Turns a recording's JPEG frames into an MP4 in Documents\Oinzi\녹화 with ffmpeg, captions burned in.
// Idle stretches (the AI thinking, an unchanged page) are shortened so the video shows the work, not the waiting.
public sealed partial class BrowserForm
{
    private const double MaxFrameSeconds = 2.0;
    private readonly HashSet<string> encodingRecordings = new(StringComparer.Ordinal);

    // Returns the saved path, or null with a reason the person can act on.
    private async Task<(string? Path, string? Error)> EncodeRecordingAsync(string id)
    {
        id = Path.GetFileName(id);
        if (FindTool("ffmpeg") is not { } ffmpeg) return (null, "ffmpeg가 없어 MP4를 만들 수 없습니다.");
        if (!encodingRecordings.Add(id)) return (null, "이미 MP4로 만드는 중입니다.");
        var dir = Path.Combine(RecordingsDir, id);
        try
        {
            if (JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "meta.json"))) is not JsonObject meta) return (null, "녹화 정보를 읽지 못했습니다.");
            var frames = (meta["frames"]?.AsArray() ?? [])
                .Select(frame => (T: frame?["t"]?.GetValue<long>() ?? 0, File: frame?["file"]?.ToString() ?? ""))
                .Where(frame => File.Exists(Path.Combine(dir, frame.File))).ToArray();
            if (frames.Length == 0) return (null, "녹화된 화면이 없습니다.");
            var duration = meta["duration"]?.GetValue<long>() ?? frames[^1].T;

            // Each frame stays until the next one, capped, and the timeline of captions is squeezed the same way.
            var shown = new double[frames.Length];
            var starts = new double[frames.Length];
            for (var i = 0; i < frames.Length; i++)
            {
                var next = i + 1 < frames.Length ? frames[i + 1].T : Math.Max(duration, frames[i].T + 1500);
                shown[i] = Math.Clamp((next - frames[i].T) / 1000.0, 0.04, MaxFrameSeconds);
                starts[i] = i == 0 ? 0 : starts[i - 1] + shown[i - 1];
            }
            double Squeeze(long t)
            {
                var i = Array.FindLastIndex(frames, frame => frame.T <= t);
                return i < 0 ? 0 : starts[i] + Math.Min((t - frames[i].T) / 1000.0, shown[i]);
            }

            var list = new StringBuilder();
            for (var i = 0; i < frames.Length; i++) list.Append($"file '{frames[i].File}'\nduration {shown[i]:0.###}\n");
            list.Append($"file '{frames[^1].File}'\n"); // the concat demuxer ignores the last duration without this
            await File.WriteAllTextAsync(Path.Combine(dir, "video.txt"), list.ToString());
            await File.WriteAllTextAsync(Path.Combine(dir, "captions.srt"), Captions(meta, Squeeze, starts[^1] + shown[^1]), new UTF8Encoding(false));

            var (width, height) = FrameSize(Path.Combine(dir, frames[0].File));
            var name = string.Concat($"{DateTime.Parse(meta["started"]?.ToString() ?? DateTime.Now.ToString("o")).ToLocalTime():yyyy-MM-dd HH.mm} {meta["title"]}"
                .Split(Path.GetInvalidFileNameChars())).Trim();
            Directory.CreateDirectory(RecordingVideosDir);
            var output = Path.Combine(RecordingVideosDir, name + ".mp4");
            var partial = Path.Combine(dir, "video.part.mp4");
            var info = new ProcessStartInfo(ffmpeg)
            {
                // Relative names keep Windows drive colons out of the subtitles filter, which would need escaping.
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (var arg in new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", "video.txt",
                "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=white," +
                       "subtitles=captions.srt:force_style='FontName=Malgun Gothic,FontSize=13,Outline=1,Shadow=0,MarginV=18'",
                "-r", "15", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "video.part.mp4"
            }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg를 실행하지 못했습니다.");
            var errors = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(partial))
                return (null, "MP4를 만들지 못했습니다: " + (await errors).Trim().Split('\n').LastOrDefault());
            File.Move(partial, output, true);
            meta["video"] = output;
            await File.WriteAllTextAsync(Path.Combine(dir, "meta.json"), meta.ToJsonString());
            return (output, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception)
        {
            return (null, "MP4를 만들지 못했습니다: " + ex.Message);
        }
        finally
        {
            encodingRecordings.Remove(id);
            foreach (var scratch in new[] { "video.txt", "captions.srt", "video.part.mp4" })
                try { File.Delete(Path.Combine(dir, scratch)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // x264 needs even dimensions; very large captures are brought down to 1920 wide.
    private static (int Width, int Height) FrameSize(string file)
    {
        using var image = Image.FromFile(file);
        var scale = Math.Min(1.0, 1920.0 / image.Width);
        return ((int)(image.Width * scale) / 2 * 2, (int)(image.Height * scale) / 2 * 2);
    }

    // What happened at each moment: the person's requests, the agent's actions and short AI replies.
    private static string Captions(JsonObject meta, Func<long, double> squeeze, double end)
    {
        var items = (meta["events"]?.AsArray() ?? [])
            .Where(item => item?["kind"]?.ToString() is "action" or "person" or "ai")
            .Select(item => (At: squeeze(item!["t"]?.GetValue<long>() ?? 0), Text: Clip(
                (item["kind"]?.ToString() == "person" ? "요청: " : "") + (item["text"]?.ToString() ?? "").ReplaceLineEndings(" "), 90)))
            .Where(item => item.Text.Length > 0).ToArray();
        static string Stamp(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\,fff");
        var srt = new StringBuilder();
        for (var i = 0; i < items.Length; i++)
        {
            var until = Math.Min(i + 1 < items.Length ? items[i + 1].At : end, items[i].At + 4);
            if (until - items[i].At < 0.3) until = items[i].At + 0.3;
            srt.Append($"{i + 1}\n{Stamp(items[i].At)} --> {Stamp(until)}\n{items[i].Text}\n\n");
        }
        return srt.ToString();
    }

    private async Task EncodeAndPostAsync(string id, bool reveal)
    {
        PostChrome(new { type = "rec.video.started", id });
        var (path, error) = await EncodeRecordingAsync(id);
        if (IsDisposed) return;
        PostChrome(new { type = "rec.video.done", id, ok = path != null, path = path ?? "", error = error ?? "" });
        PostRecordingList();
        if (reveal && path != null) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
