using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PluckWindowsNative;

// The overview shows every session as a card: the person's tabs, each AI panel conversation and each terminal agent.
// It shares the palette's frozen-viewport mechanism, so `paletteOpen` covers both overlays.
public sealed partial class BrowserForm
{
    private const string StoppedMessage = "The person stopped this session from the overview. Do not retry; tell them the task was stopped.";
    private static readonly TimeSpan CaptureWait = TimeSpan.FromMilliseconds(700);
    private bool overviewBusy;

    private async Task OpenOverviewAsync()
    {
        if (!chromeReady) return;
        while (sidebarPreparing || sidebarMotion) await Task.Delay(16);
        string? shot = null;
        if (!paletteOpen && viewport.Visible && Active()?.View.CoreWebView2 is { } core)
        {
            var capture = CaptureAsync(core, CoreWebView2CapturePreviewImageFormat.Jpeg);
            if (await Task.WhenAny(capture, Task.Delay(CaptureWait)) == capture && await capture is { } bytes)
                shot = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        object[] cards;
        try { cards = await OverviewCardsAsync(); }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dataDir, "overview-error.log"), ex.ToString());
            return;
        }
        paletteOpen = true;
        PostChrome(new { type = "overview", shot, cards });
    }

    private async Task RefreshOverviewAsync()
    {
        if (overviewBusy || !paletteOpen) return;
        overviewBusy = true;
        try { PostChrome(new { type = "overview.cards", cards = await OverviewCardsAsync() }); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(dataDir, "overview-error.log"), ex.ToString()); }
        finally { overviewBusy = false; }
    }

    private Conversation? RunningConversation(string space, out string? engine)
    {
        foreach (var candidate in Engines)
            if (aiRuns.ContainsKey(candidate) && currentConversations.GetValueOrDefault(candidate) is { } chat && SpaceOf(chat) == space)
            {
                engine = candidate;
                return chat;
            }
        engine = null;
        return null;
    }

    private async Task<object[]> OverviewCardsAsync()
    {
        var entries = new List<(BrowserTab? Shown, Func<string?, object> Card)>();
        var mine = Active() is { Agent: false } viewed ? viewed : OrderedTabs().FirstOrDefault(tab => !tab.Agent);
        if (mine != null)
            entries.Add((mine, thumb => new
            {
                space = "user", label = "내 탭", kind = "user", engine = "", tabs = browserTabs.Values.Count(tab => !tab.Agent),
                title = TabTitle(mine), current = Active()?.Agent == false, running = false, status = "", interrupted = false, stopped = false, thumb
            }));
        var recent = DateTime.UtcNow.AddSeconds(-45);
        foreach (var session in sessions.Values.OrderBy(session => session.Order).ToArray())
        {
            var own = browserTabs.Values.Where(tab => tab.Space == session.Id).ToArray();
            var chat = conversations.LastOrDefault(c => c.Space == session.Id || (c.Space == null && c.Id == session.Id));
            var run = RunningConversation(session.Id, out var engine);
            var busy = own.Any(tab => tab.PendingCommands > 0);
            var running = !session.Stopped && (run != null || busy || (session.Kind != "panel" && session.LastSeen > recent));
            if (own.Length == 0 && !running) continue;
            // The tab the agent is working in, else its latest tab, else a tab of the person's it was allowed to drive.
            var shown = own.FirstOrDefault(tab => tab.PendingCommands > 0) ?? own.MaxBy(tab => tab.LastUsed)
                ?? session.UserTabs.Select(id => browserTabs.GetValueOrDefault(id)).LastOrDefault(tab => tab != null);
            entries.Add((shown, thumb => new
            {
                space = session.Id, label = session.Label, kind = session.Kind == "panel" ? "panel" : "terminal",
                engine = engine ?? chat?.Engine ?? "", tabs = own.Length,
                title = shown == null ? "" : TabTitle(shown), current = Active() is { } active && own.Contains(active),
                running, status = session.Stopped ? "중지됨" : busy ? session.Activity : running ? (run != null ? "AI가 생각하고 있어요" : "터미널에서 작업 중") : "",
                interrupted = run == null && chat?.Interrupted == true, stopped = session.Stopped, thumb
            }));
        }
        // Captures run side by side and the overview waits a bounded time; slower ones land in the cache for the next refresh.
        var captures = entries.Where(entry => entry.Shown != null).Select(entry => StartThumbnail(entry.Shown!)).ToArray();
        await Task.WhenAny(Task.WhenAll(captures), Task.Delay(CaptureWait));
        return entries.Select(entry => entry.Card(entry.Shown?.Thumb)).ToArray();
    }

    private static string TabTitle(BrowserTab tab) =>
        tab.View.CoreWebView2?.DocumentTitle is { Length: > 0 } title ? title : tab.View.Source?.Host ?? "새 탭";

    // Keeps a recent thumbnail of each tab after it loads or an agent acts on it, so the overview opens with pictures.
    private async void ScheduleThumbnail(BrowserTab tab)
    {
        if (tab.ThumbDue) return;
        tab.ThumbDue = true;
        await Task.Delay(1500);
        tab.ThumbDue = false;
        if (!IsDisposed && browserTabs.ContainsKey(tab.Id)) await StartThumbnail(tab);
    }

    private static Task StartThumbnail(BrowserTab tab)
    {
        // A capture requested before a hidden tab's first frame can wait indefinitely, so a stale one is abandoned.
        if (tab.ThumbCapture is { IsCompleted: false } running && DateTime.UtcNow - tab.ThumbStarted < TimeSpan.FromSeconds(3)) return running;
        tab.ThumbStarted = DateTime.UtcNow;
        return tab.ThumbCapture = CaptureThumbnailAsync(tab);
    }

    private static async Task<byte[]?> CaptureAsync(CoreWebView2 core, CoreWebView2CapturePreviewImageFormat format)
    {
        try
        {
            using var image = new MemoryStream();
            await core.CapturePreviewAsync(format, image);
            return image.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException) { return null; }
    }

    // CapturePreviewAsync can stall for many seconds on a tab that has been hidden a while; the DevTools capture does not.
    private static async Task<byte[]?> CaptureHiddenAsync(CoreWebView2 core)
    {
        try
        {
            var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", """{"format":"jpeg","quality":85}""");
            using var result = JsonDocument.Parse(json);
            return Convert.FromBase64String(result.RootElement.GetProperty("data").GetString() ?? "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException
            or JsonException or KeyNotFoundException or FormatException) { return null; }
    }

    // Thumbnails are shrunk to keep the messages to the chrome page small.
    private static async Task<string?> CaptureThumbnailAsync(BrowserTab tab)
    {
        if (tab.View.CoreWebView2 is not { } core || await CaptureHiddenAsync(core) is not { } shot) return tab.Thumb;
        try
        {
            using var capture = new MemoryStream(shot);
            using var full = new Bitmap(capture);
            var width = Math.Min(640, full.Width);
            var height = Math.Max(1, (int)Math.Round(full.Height * (width / (double)full.Width)));
            using var small = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(small))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(full, 0, 0, width, height);
            }
            using var output = new MemoryStream();
            var jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var quality = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L) } };
            small.Save(output, jpeg, quality);
            return tab.Thumb = "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException) { return tab.Thumb; }
    }

    private void OpenSessionTab(string space)
    {
        var tab = space == "user"
            ? Active() is { Agent: false } viewed ? viewed : OrderedTabs().FirstOrDefault(other => !other.Agent)
            : browserTabs.Values.Where(other => other.Space == space).OrderByDescending(other => other.PendingCommands).ThenByDescending(other => other.LastUsed).FirstOrDefault()
                ?? sessions.GetValueOrDefault(space)?.UserTabs.Select(id => browserTabs.GetValueOrDefault(id)).LastOrDefault(other => other != null);
        if (tab != null) tabs.SelectedTab = tab.Page;
    }

    private void StopSession(string space)
    {
        if (RunningConversation(space, out var engine) != null) StopAssistant(engine!);
        else if (sessions.GetValueOrDefault(space) is { Kind: not "panel" } session) session.Stopped = true;
        _ = RefreshOverviewAsync();
    }
}
