using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PluckWindowsNative;

// Smaller agent commands: a status line for the overview, screenshot comparison and running several commands at once.
public sealed partial class BrowserForm
{
    private static readonly HashSet<string> BatchableCommands =
    [
        "list", "new", "show", "goto", "snapshot", "click", "fill", "hover", "select", "clickAt", "type", "press", "scroll",
        "back", "forward", "wait", "js", "screenshot", "close", "status", "login", "compare"
    ];

    // Steps run in order inside the caller's session; with bail (the default) the first failure stops the rest.
    private async Task<object> BatchAsync(JsonElement body, string session, string owner)
    {
        if (!body.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("steps must be an array of { command, ...arguments }");
        var bail = !body.TryGetProperty("bail", out var bailValue) || bailValue.ValueKind != JsonValueKind.False;
        var results = new List<object?>();
        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (index >= 50) throw new InvalidOperationException("A batch can hold at most 50 steps.");
            var node = JsonNode.Parse(step.GetRawText()) as JsonObject ?? throw new InvalidOperationException($"Step {index} is not an object");
            var command = node["command"]?.ToString() ?? "";
            if (!BatchableCommands.Contains(command)) throw new InvalidOperationException($"Step {index}: {command} cannot run in a batch");
            node["session"] = session;
            node["owner"] = owner;
            using var document = JsonDocument.Parse(node.ToJsonString());
            try { results.Add(await CommandAsync(document.RootElement)); }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or TaskCanceledException)
            {
                results.Add(new { error = ex.Message });
                if (bail) return new { ok = false, failedAt = index, results };
            }
            index++;
        }
        return new { ok = true, results };
    }

    // Compares the tab's current screen with a baseline PNG the agent sends. Images travel as base64, so pluck never
    // reads or writes files on an agent's behalf.
    private static async Task<object> CompareAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, string baseline, double threshold, bool wantDiff)
    {
        if (baseline.Length == 0) throw new InvalidOperationException("baseline (base64 PNG) required");
        using var before = LoadPng(baseline);
        using var after = LoadPng(await CaptureAsync(core, false));
        var width = Math.Min(before.Width, after.Width);
        var height = Math.Min(before.Height, after.Height);
        var limit = (int)Math.Round(Math.Clamp(threshold, 0, 1) * 255);
        using var diff = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var a = Pixels(before, width, height);
        var b = Pixels(after, width, height);
        var marks = new int[width * height];
        var changed = 0;
        for (var i = 0; i < marks.Length; i++)
        {
            int pa = a[i], pb = b[i];
            var delta = Math.Max(Math.Abs((pa >> 16 & 255) - (pb >> 16 & 255)), Math.Max(Math.Abs((pa >> 8 & 255) - (pb >> 8 & 255)), Math.Abs((pa & 255) - (pb & 255))));
            if (delta > limit) { changed++; marks[i] = unchecked((int)0xFFE5484D); }
            else
            {
                // Unchanged pixels are kept faint so the red changes stand out against the page.
                var grey = ((pb >> 16 & 255) + (pb >> 8 & 255) + (pb & 255)) / 3;
                var faint = 255 - (255 - grey) / 4;
                marks[i] = unchecked((int)0xFF000000) | faint << 16 | faint << 8 | faint;
            }
        }
        string? diffPng = null;
        if (wantDiff)
        {
            var data = diff.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(marks, 0, data.Scan0, marks.Length);
            diff.UnlockBits(data);
            using var stream = new MemoryStream();
            diff.Save(stream, ImageFormat.Png);
            diffPng = Convert.ToBase64String(stream.ToArray());
        }
        return new
        {
            changed = Math.Round((double)changed / Math.Max(1, marks.Length), 5), pixels = changed, width, height,
            sizeMatch = before.Width == after.Width && before.Height == after.Height, diff = diffPng
        };
    }

    private static Bitmap LoadPng(string base64)
    {
        // GDI+ reads a stream-backed bitmap lazily, so the pixels are copied before the stream closes.
        try { using var stream = new MemoryStream(Convert.FromBase64String(base64)); using var decoded = new Bitmap(stream); return new Bitmap(decoded); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { throw new InvalidOperationException("Not a PNG image"); }
    }

    private static int[] Pixels(Bitmap image, int width, int height)
    {
        using var copy = image.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppArgb);
        var data = copy.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new int[width * height];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        copy.UnlockBits(data);
        return pixels;
    }
}
