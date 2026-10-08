using System.Collections;
using System.Reflection;
using System.Text.Json;
using EgoWindowsNative;
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
    }
}
