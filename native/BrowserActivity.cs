using System.Text.Json;

namespace OinziBrowser;

public sealed partial class BrowserForm
{
    private bool followAssistant = true;
    // The engine whose conversation is showing in the open AI panel; null while the panel is closed.
    private string? aiViewEngine;

    private string? FollowedSpace() => aiViewEngine is { } engine && currentConversations.TryGetValue(engine, out var chat) ? SpaceOf(chat) : null;
    private static string ActivityLabel(string command) => command switch
    {
        "click" => "AI가 클릭하고 있어요",
        "fill" => "AI가 입력하고 있어요",
        "scroll" => "AI가 스크롤하고 있어요",
        "goto" => "AI가 페이지를 열고 있어요",
        "snapshot" => "AI가 페이지를 읽고 있어요",
        "press" => "AI가 키를 누르고 있어요",
        "wait" => "AI가 페이지를 기다리고 있어요",
        "hover" => "AI가 메뉴를 살펴보고 있어요",
        "select" => "AI가 항목을 고르고 있어요",
        "type" => "AI가 입력하고 있어요",
        "clickAt" => "AI가 클릭하고 있어요",
        "back" or "forward" => "AI가 페이지를 옮기고 있어요",
        _ => "AI가 브라우저를 조작하고 있어요"
    };

    private async Task ShowActivityAsync(BrowserTab tab, string command, string target = "", string frameId = "f0", bool done = false)
    {
        // The done call still runs so an overlay shown before the setting was turned off gets removed.
        if (!prefs.AgentOverlay && !done) return;
        var label = done ? "브라우저 조작 완료" : ActivityLabel(command);
        var script = ReadResource("activity.js") + "\nwindow.__oinziActivity(" + JsonSerializer.Serialize(label) + ","
            + JsonSerializer.Serialize(target) + "," + (done ? "true" : "false") + "," + JsonSerializer.Serialize(command) + ")";
        try
        {
            if (frameId == "f0") await tab.View.CoreWebView2!.ExecuteScriptAsync(script);
            else if (tab.Frames.TryGetValue(frameId, out var frame)) await frame.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
    }
}
