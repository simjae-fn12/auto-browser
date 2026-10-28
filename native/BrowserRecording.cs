using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace OinziBrowser;

// Records what a session does so the person can replay it later: frames of the tab the session is working in,
// plus a timeline of the agent's actions and, for AI panel conversations, its messages.
// Frames are captured with DevTools, which also works while the tab is hidden behind the one the person is viewing.
public sealed partial class BrowserForm
{
    private sealed class Recording
    {
        public required string Id { get; init; }
        public required string Space { get; init; }
        public required string Title { get; init; }
        public required string Dir { get; init; }
        public DateTime Started { get; } = DateTime.UtcNow;
        public DateTime? Ended { get; set; }
        public List<JsonObject> Frames { get; } = [];
        public List<JsonObject> Events { get; } = [];
        public System.Windows.Forms.Timer Timer { get; } = new() { Interval = 250 };
        public bool Capturing { get; set; }
        public string? LastHash { get; set; }
        public long Bytes { get; set; }
        public int Ticks { get; set; }
        public long Elapsed => (long)(DateTime.UtcNow - Started).TotalMilliseconds;
    }

    private const string RecordingHost = "https://recordings.oinzi/";
    // Active recordings, by session space.
    private readonly Dictionary<string, Recording> recordings = new(StringComparer.Ordinal);
    private FileStream? exportStream;
    private string RecordingsDir => Path.Combine(dataDir, "recordings");

    // Frames are served to the chrome page with CORS so the player can draw them on a canvas for video export.
    private void ServeRecordings(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter(RecordingHost + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!e.Request.Uri.StartsWith(RecordingHost, StringComparison.Ordinal) || environment == null) return;
            var relative = Uri.UnescapeDataString(e.Request.Uri[RecordingHost.Length..].Split('?')[0]).Replace('/', Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(RecordingsDir) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Access-Control-Allow-Origin: *");
                return;
            }
            e.Response = environment.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(path)), 200, "OK",
                "Content-Type: image/jpeg\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: max-age=31536000, immutable");
        };
    }

    private bool IsRecording(string space) => recordings.ContainsKey(space);

    private void ToggleRecording(string space)
    {
        if (string.IsNullOrEmpty(space)) return;
        if (recordings.TryGetValue(space, out var active)) StopRecording(active);
        else StartRecording(space);
        RefreshSidebar();
        foreach (var engine in Engines) PostSettings(engine);
        _ = RefreshOverviewAsync();
    }

    private void StartRecording(string space)
    {
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
        var recording = new Recording
        {
            Id = id, Space = space, Title = space == "user" ? "내 탭" : SessionLabel(space), Dir = Path.Combine(RecordingsDir, id)
        };
        Directory.CreateDirectory(recording.Dir);
        recording.Timer.Tick += (_, _) =>
        {
            _ = CaptureRecordingFrameAsync(recording);
            // Keep the saved timeline current, so a crash loses at most a few seconds.
            if (++recording.Ticks % 20 == 0) SaveRecording(recording);
        };
        recordings[space] = recording;
        recording.Events.Add(new JsonObject { ["t"] = 0, ["kind"] = "note", ["text"] = "녹화를 시작했습니다." });
        recording.Timer.Start();
        _ = CaptureRecordingFrameAsync(recording);
    }

    // Stopping turns the frames into an MP4 right away; on shutdown that is skipped and the player can make it later.
    private void StopRecording(Recording recording, bool encode = true)
    {
        recording.Timer.Stop();
        recording.Timer.Dispose();
        recording.Ended = DateTime.UtcNow;
        recordings.Remove(recording.Space);
        SaveRecording(recording);
        if (encode && prefs.AutoMp4 && recording.Frames.Count > 0) _ = EncodeAndPostAsync(recording.Id, reveal: false);
    }

    private void StopAllRecordings()
    {
        foreach (var recording in recordings.Values.ToArray()) StopRecording(recording, encode: false);
    }

    private void SaveRecording(Recording recording)
    {
        try
        {
            var meta = new JsonObject
            {
                ["id"] = recording.Id, ["title"] = recording.Title, ["space"] = recording.Space,
                ["started"] = recording.Started.ToString("o"), ["ended"] = recording.Ended?.ToString("o"),
                ["duration"] = recording.Elapsed, ["bytes"] = recording.Bytes,
                ["frames"] = new JsonArray(recording.Frames.Select(frame => frame.DeepClone()).ToArray()),
                ["events"] = new JsonArray(recording.Events.Select(item => item.DeepClone()).ToArray())
            };
            File.WriteAllText(Path.Combine(recording.Dir, "meta.json.tmp"), meta.ToJsonString());
            File.Move(Path.Combine(recording.Dir, "meta.json.tmp"), Path.Combine(recording.Dir, "meta.json"), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // The tab the session is working in: a busy one, else the one it used last, including person's tabs it was allowed to drive.
    private BrowserTab? RecordingTab(string space)
    {
        if (space == "user") return Active() is { Agent: false } viewed ? viewed : null;
        var granted = sessions.GetValueOrDefault(space)?.UserTabs.Select(id => browserTabs.GetValueOrDefault(id)).OfType<BrowserTab>() ?? [];
        var candidates = browserTabs.Values.Where(tab => tab.Space == space).Concat(granted).ToArray();
        return candidates.FirstOrDefault(tab => tab.PendingCommands > 0) ?? candidates.MaxBy(tab => tab.LastUsed);
    }

    private async Task CaptureRecordingFrameAsync(Recording recording)
    {
        if (recording.Capturing || recording.Ended != null || RecordingTab(recording.Space) is not { } tab || tab.View.CoreWebView2 is not { } core) return;
        recording.Capturing = true;
        try
        {
            var capture = CaptureHiddenAsync(core, 72);
            if (await Task.WhenAny(capture, Task.Delay(3000)) != capture || await capture is not { } bytes) return;
            // Unchanged screens are not stored again, so idle stretches cost almost nothing.
            var hash = Convert.ToHexString(SHA1.HashData(bytes));
            if (hash == recording.LastHash || recording.Ended != null) return;
            recording.LastHash = hash;
            var file = $"{recording.Frames.Count:D6}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(recording.Dir, file), bytes);
            recording.Bytes += bytes.Length;
            recording.Frames.Add(new JsonObject
            {
                ["t"] = recording.Elapsed, ["file"] = file, ["tab"] = tab.Id,
                ["url"] = tab.View.Source?.ToString() ?? "", ["title"] = tab.View.CoreWebView2?.DocumentTitle ?? ""
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { recording.Capturing = false; }
    }

    // Names the element an action targets by what the person sees on it, so a replay reads "클릭: 결제하기", not "#b".
    private static async Task<string> DescribeTargetAsync(BrowserTab tab, string target)
    {
        var match = System.Text.RegularExpressions.Regex.Match(target, "^@(?<frame>f[0-9]+):(?<ref>[0-9]+)$");
        if (target.Length == 0 || (match.Success && match.Groups["frame"].Value != "f0") || tab.View.CoreWebView2 is not { } core) return target;
        var selector = match.Success ? $"[data-oinzi-ref=\"{match.Groups["ref"].Value}\"]" : target;
        var script = $$"""
            (() => {
              try {
                const el = document.querySelector({{JsonSerializer.Serialize(selector)}});
                if (!el) return '';
                const label = el.getAttribute('aria-label') || el.innerText || el.value || el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('name') || '';
                return label.trim().replace(/\s+/g, ' ').slice(0, 60);
              } catch { return ''; }
            })()
            """;
        try
        {
            var describe = core.ExecuteScriptAsync(script);
            if (await Task.WhenAny(describe, Task.Delay(800)) == describe && JsonSerializer.Deserialize<string>(await describe) is { Length: > 0 } label)
                return label;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException) { }
        return target;
    }

    private async Task RecordActionAsync(string space, string command, BrowserTab tab, string target, string value)
    {
        if (!recordings.ContainsKey(space)) return;
        if (command is "click" or "fill") target = await DescribeTargetAsync(tab, target);
        if (!recordings.TryGetValue(space, out var recording)) return;
        var label = command switch
        {
            "goto" => "페이지 이동: " + value,
            "click" => "클릭: " + target,
            "fill" => $"입력: {target} ← {Clip(value, 80)}",
            "press" => "키 입력: " + value,
            "scroll" => "스크롤 " + (value == "up" ? "위로" : "아래로"),
            "snapshot" => "페이지 읽기",
            "wait" => "기다리기: " + target,
            "js" => "스크립트 실행",
            "show" => "탭 보여주기",
            _ => command
        };
        recording.Events.Add(new JsonObject
        {
            ["t"] = recording.Elapsed, ["kind"] = "action", ["command"] = command, ["text"] = label,
            ["url"] = tab.View.Source?.ToString() ?? "", ["title"] = tab.View.CoreWebView2?.DocumentTitle ?? ""
        });
    }

    private void RecordConversation(Conversation chat, JsonObject entry)
    {
        if (!recordings.TryGetValue(SpaceOf(chat), out var recording)) return;
        var kind = entry["kind"]?.ToString();
        var text = kind switch
        {
            "user" => entry["text"]?.ToString(),
            "text" => entry["text"]?.ToString(),
            "tool" => $"{entry["name"]}: {entry["detail"]}",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(text)) return;
        recording.Events.Add(new JsonObject
        {
            ["t"] = recording.Elapsed, ["kind"] = kind == "user" ? "person" : kind == "tool" ? "tool" : "ai", ["text"] = Clip(text, 2000)
        });
    }

    private void PostRecordingList()
    {
        var items = new List<JsonObject>();
        try
        {
            if (Directory.Exists(RecordingsDir))
                foreach (var meta in Directory.EnumerateFiles(RecordingsDir, "meta.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (JsonNode.Parse(File.ReadAllText(meta)) is not JsonObject data) continue;
                        var frames = data["frames"]?.AsArray() ?? [];
                        items.Add(new JsonObject
                        {
                            ["id"] = data["id"]?.ToString(), ["title"] = data["title"]?.ToString(), ["started"] = data["started"]?.ToString(),
                            ["duration"] = data["duration"]?.DeepClone(), ["bytes"] = data["bytes"]?.DeepClone(),
                            ["frames"] = frames.Count, ["steps"] = (data["events"]?.AsArray() ?? []).Count(item => item?["kind"]?.ToString() == "action"),
                            ["cover"] = frames.Count > 0 ? frames[frames.Count / 2]?["file"]?.ToString() : null,
                            ["live"] = recordings.Values.Any(active => active.Id == data["id"]?.ToString()),
                            ["video"] = data["video"]?.ToString() is { } video && File.Exists(video) ? video : null,
                            ["encoding"] = encodingRecordings.Contains(data["id"]?.ToString() ?? "")
                        });
                    }
                    catch (Exception ex) when (ex is IOException or JsonException) { }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        PostChrome(new { type = "rec.list", items = items.OrderByDescending(item => item["started"]?.ToString()).ToArray() });
    }

    private void PostRecording(string id)
    {
        if (recordings.Values.FirstOrDefault(active => active.Id == id) is { } live) SaveRecording(live);
        var path = Path.Combine(RecordingsDir, Path.GetFileName(id), "meta.json");
        try { PostChrome(new { type = "rec.data", id, meta = JsonNode.Parse(File.ReadAllText(path)) }); }
        catch (Exception ex) when (ex is IOException or JsonException) { PostChrome(new { type = "rec.error", text = "녹화를 열지 못했습니다." }); }
    }

    private void DeleteRecording(string id)
    {
        if (recordings.Values.Any(active => active.Id == id)) return;
        try { Directory.Delete(Path.Combine(RecordingsDir, Path.GetFileName(id)), true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        PostRecordingList();
    }

    // The chrome page encodes the video with MediaRecorder and streams it here in chunks.
    private void BeginExport(string title)
    {
        exportStream?.Dispose();
        exportStream = null;
        using var dialog = new SaveFileDialog
        {
            Filter = "WebM 동영상|*.webm", InitialDirectory = RecordingVideosDir, FileName = string.Concat((title.Length == 0 ? "Oinzi 녹화" : title).Split(Path.GetInvalidFileNameChars())) + ".webm"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try { exportStream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        PostChrome(new { type = "rec.export.ready", ok = exportStream != null, path = exportStream?.Name ?? "" });
    }

    private void ExportChunk(string data)
    {
        if (exportStream == null) return;
        try { exportStream.Write(Convert.FromBase64String(data)); }
        catch (Exception ex) when (ex is IOException or FormatException) { exportStream.Dispose(); exportStream = null; }
    }

    private void EndExport()
    {
        var path = exportStream?.Name;
        exportStream?.Dispose();
        exportStream = null;
        PostChrome(new { type = "rec.export.done", ok = path != null, path = path ?? "" });
    }
}
