using System.Collections;
using System.Reflection;
using System.Text.Json;
using PluckWindowsNative;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var form = new BrowserForm();
        var type = typeof(BrowserForm);
        var tabType = type.GetNestedType("BrowserTab", BindingFlags.NonPublic)!;
        var tabs = (IDictionary)type.GetField("browserTabs", Private)!.GetValue(form)!;
        var control = (TabControl)type.GetField("tabs", Private)!.GetValue(form)!;
        object Add(int id, string space, string owner, int minutes = 0, int pending = 0)
        {
            var page = new TabPage();
            var view = new WebView2();
            page.Controls.Add(view);
            var tab = Activator.CreateInstance(tabType, id, space, page, view, owner)!;
            tabType.GetProperty("LastUsed")!.SetValue(tab, DateTime.UtcNow.AddMinutes(-minutes));
            tabType.GetProperty("PendingCommands")!.SetValue(tab, pending);
            tabs.Add(id, tab);
            control.TabPages.Add(page);
            return tab;
        }
        void Command(object body)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(body));
            ((Task<object?>)type.GetMethod("CommandAsync", Private)!.Invoke(form, [json.RootElement])!).GetAwaiter().GetResult();
        }
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        Add(1, "user", "run-a", 60);
        Add(2, "work", "run-a");
        Add(3, "work", "run-b");
        Command(new { command = "cleanup-run", owner = "run-a" });
        Check(tabs.Contains(1) && !tabs.Contains(2) && tabs.Contains(3), "cleanup preserves user and other run in same space");
        Command(new { command = "close", id = 3, space = "work" });
        Check(!tabs.Contains(3), "uninitialized WebView can be closed");
        Add(4, "stale", "old", 16);
        Add(5, "busy", "busy", 16, 1);
        Add(6, "recent", "recent", 1);
        Add(7, "heartbeat", "live", 16);
        Command(new { command = "heartbeat", owner = "live" });
        type.GetMethod("CleanupIdleTabs", Private)!.Invoke(form, null);
        Check(!tabs.Contains(4), "stale agent space is reclaimed");
        Check(tabs.Contains(1) && tabs.Contains(5) && tabs.Contains(6) && tabs.Contains(7), "user, busy, recent, and heartbeat tabs survive");
        Console.WriteLine("Native cleanup checks passed.");

        void Chrome(object message) => type.GetMethod("HandleChromeMessage", Private)!.Invoke(form, [JsonSerializer.Serialize(message)]);
        TabPage Page(int id) => (TabPage)tabType.GetProperty("Page")!.GetValue(tabs[id])!;
        Chrome(new { type = "select", id = 5 });
        Check(control.SelectedTab == Page(5), "sidebar selects the associated WebView tab");
        Chrome(new { type = "close", id = 5 });
        Check(!tabs.Contains(5), "sidebar close removes only the selected tab");
        Check(tabs.Contains(1) && tabs.Contains(6) && tabs.Contains(7), "sidebar close preserves other tabs");

        Add(8, "user", "");
        Add(9, "user", "");
        Chrome(new { type = "select", id = 8 });
        Chrome(new { type = "reorder", ids = new[] { 9, 8, 1 } });
        int[] Order() => ((IEnumerable)type.GetMethod("OrderedTabs", Private)!.Invoke(form, null)!).Cast<object>()
            .Select(tab => (int)tabType.GetProperty("Id")!.GetValue(tab)!).ToArray();
        Check(Order().Take(3).SequenceEqual([9, 8, 1]), "dragging in the sidebar reorders user tabs");
        Check(Order().Skip(3).All(id => id is 6 or 7), "agent spaces stay below user tabs");
        Chrome(new { type = "close", id = 8 });
        Check(control.SelectedTab == Page(1), "closing the active tab selects its sidebar neighbour");

        var state = JsonSerializer.Serialize(type.GetMethod("ChromeState", Private)!.Invoke(form, null));
        using var stateJson = JsonDocument.Parse(state);
        Check(stateJson.RootElement.GetProperty("active").GetInt32() == 1, "chrome state reports the active tab");
        Check(stateJson.RootElement.GetProperty("tabs").GetArrayLength() == tabs.Count, "chrome state lists every tab");
        using var html = typeof(BrowserForm).Assembly.GetManifestResourceStream("chrome.html");
        Check(html is { Length: > 1000 }, "chrome UI is embedded in the executable");

        string[] Kinds(string parser, string line) => ((IEnumerable<System.Text.Json.Nodes.JsonObject>)type.GetMethod(parser, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [System.Text.Json.Nodes.JsonNode.Parse(line)!.AsObject()])!).Select(item => item["kind"]!.ToString()).ToArray();
        Check(Kinds("ParseClaude", """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"node agent.mjs x.mjs"}}]}}""")
            .SequenceEqual(["text", "tool"]), "Claude stream events map to panel text and tool rows");
        Check(Kinds("ParseClaude", """{"type":"system","subtype":"init","session_id":"abc"}""").SequenceEqual(["session"]), "Claude session id is captured for resume");
        Check(Kinds("ParseCodex", """{"type":"item.completed","item":{"id":"i","type":"command_execution","exit_code":0,"aggregated_output":"ok"}}""")
            .SequenceEqual(["toolDone"]), "Codex command results map to tool completion");
        Check(Kinds("ParseCodex", """{"type":"item.completed","item":{"type":"error","message":"Codex is ignoring 1 unrecognized configuration setting."}}""").Length == 0,
            "Codex config warnings are not shown in the panel");
    }
}
