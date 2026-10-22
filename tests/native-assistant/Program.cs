using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PluckWindowsNative;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly Type FormType = typeof(BrowserForm);
    static object? Call(BrowserForm form, string name, params object?[] args) => FormType.GetMethod(name, Private)!.Invoke(form, args);
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }

    [STAThread]
    static void Main()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pluck-assistant-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("PLUCK_NATIVE_DATA_DIR", dir);
        ApplicationConfiguration.Initialize();
        using var form = new BrowserForm();
        form.Shown += async (_, _) =>
        {
            try
            {
                var chrome = (WebView2)FormType.GetField("chromeView", Private)!.GetValue(form)!;
                for (var i = 0; i < 200 && (chrome.CoreWebView2 == null || !(bool)FormType.GetField("chromeReady", Private)!.GetValue(form)!); i++) await Task.Delay(100);
                Check(chrome.CoreWebView2 != null, "chrome initialized");
                await Task.Delay(300);
                Call(form, "RecordAssistant", "claude", new { kind = "user", text = "Saved conversation" });
                Call(form, "PostAssistant", "claude", new { kind = "text", text = "Saved answer" });
                var chats = (IDictionary)FormType.GetField("currentConversations", Private)!.GetValue(form)!;
                var first = chats["claude"]!;
                var firstId = (string)first.GetType().GetProperty("Id")!.GetValue(first)!;
                first.GetType().GetProperty("Session")!.SetValue(first, "test-session");
                Call(form, "SelectConversation", "claude", null);
                Check(!ReferenceEquals(first, chats["claude"]), "new conversation preserves old room");
                Call(form, "SelectConversation", "claude", firstId);
                var sessions = (IDictionary)FormType.GetField("aiSessions", Private)!.GetValue(form)!;
                Check((string?)sessions["claude"] == "test-session", "select restores CLI resume session");
                await Task.Delay(200);
                var ui = await chrome.CoreWebView2!.ExecuteScriptAsync("document.querySelector('.log[data-engine=claude]').textContent");
                Check(ui.Contains("Saved conversation") && ui.Contains("Saved answer"), "history replay renders user and assistant");
                await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#aiReset').click()");
                await Task.Delay(200);
                Check(!ReferenceEquals(first, chats["claude"]), "new conversation button reaches host");
                await chrome.CoreWebView2.ExecuteScriptAsync($"document.querySelector('#aiHistory').value={JsonSerializer.Serialize(firstId)}; document.querySelector('#aiHistory').dispatchEvent(new Event('change'))");
                await Task.Delay(200);
                Check(ReferenceEquals(first, chats["claude"]), "history selector restores old room");
                Console.WriteLine("RENDER_500_MS " + await chrome.CoreWebView2.ExecuteScriptAsync("(() => { const start = performance.now(); for(let i=0;i<500;i++) renderAiChrome(); return Math.round(performance.now()-start); })()"));

                using (var restored = new BrowserForm())
                {
                    Call(form, "FlushAssistantHistory");
                    Call(restored, "LoadAssistantHistory");
                    var saved = (IList)FormType.GetField("conversations", Private)!.GetValue(restored)!;
                    Check(saved.Cast<object>().Any(c => (string?)c.GetType().GetProperty("Id")!.GetValue(c) == firstId), "rooms survive app restart");
                }
                var info = new ProcessStartInfo("test.exe");
                info.Environment["WT_SESSION"] = "other-terminal";
                info.Environment["CODEX_THREAD_ID"] = "other-thread";
                info.Environment["CODEX_HOME"] = "keep-auth-location";
                FormType.GetMethod("IsolateAssistantEnvironment", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [info]);
                Check(!info.Environment.ContainsKey("WT_SESSION") && !info.Environment.ContainsKey("CODEX_THREAD_ID") && info.Environment["CODEX_HOME"] == "keep-auth-location", "terminal session identities removed, auth location retained");

                var tab = await (Task<object?>)TaskFromCreate(form);
                var view = (WebView2)tab!.GetType().GetProperty("View")!.GetValue(tab)!;
                view.CoreWebView2.NavigateToString("<html><title>Interaction test</title><body style='padding:80px;font:20px system-ui'><h1>pluck interaction test</h1><input id='field'><button id='button' onclick=\"document.body.dataset.clicked='yes'\">Click me</button></body></html>");
                await Task.Delay(500);
                Check((bool)(await (Task<bool>)Call(form, "ActAsync", tab, "fill", "#field", "hello")!), "fill succeeds with overlay");
                Check((bool)(await (Task<bool>)Call(form, "ActAsync", tab, "click", "#button", "")!), "click succeeds with overlay");
                Check(await view.CoreWebView2.ExecuteScriptAsync("document.body.dataset.clicked === 'yes' && document.querySelector('#field').value === 'hello'") == "true", "actions reach real page elements");
                Check(await view.CoreWebView2.ExecuteScriptAsync("typeof window.__pluckActivity") == "\"function\"", "live visual overlay installed");
                using (var image = File.Create(Path.Combine(dir, "activity.png"))) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                await chrome.CoreWebView2.ExecuteScriptAsync("toggleAssist(true)");
                await Task.Delay(500);
                var viewport = (Panel)FormType.GetField("viewport", Private)!.GetValue(form)!;
                var resizes = 0;
                viewport.Resize += (_, _) => resizes++;
                await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#toggle').click()");
                await Task.Delay(650);
                Check((bool)FormType.GetField("sidebarCollapsed", Private)!.GetValue(form)! && viewport.Visible, "collapse completes and live page returns");
                Check(resizes <= 2, "sidebar motion resizes native WebView only at completion");
                Console.WriteLine("SIDEBAR_NATIVE_RESIZES " + resizes);
                await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#toggle').click(); setTimeout(() => document.querySelector('#toggle').click(), 60); setTimeout(() => document.querySelector('#toggle').click(), 100)");
                await Task.Delay(800);
                Check(!(bool)FormType.GetField("sidebarCollapsed", Private)!.GetValue(form)! && viewport.Visible, "rapid reversal settles expanded without hiding page");
                Check(await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('.brand') === null") == "true", "sidebar keeps navigation space free of duplicate branding");
                await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#tabSearch').click()");
                await Task.Delay(350);
                await chrome.CoreWebView2.ExecuteScriptAsync("query.value='interaction'; buildResults()");
                Check(await chrome.CoreWebView2.ExecuteScriptAsync("pal.mode === 'tabs' && pal.items.length === 1 && pal.items[0].kind === 'tab'") == "true", "tab search filters existing tabs without web search entries");
                await chrome.CoreWebView2.ExecuteScriptAsync("closePalette()");
                await Task.Delay(400);
                using (var image = File.Create(Path.Combine(dir, "chrome.png"))) await chrome.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                Console.WriteLine("ARTIFACTS " + dir);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }

    static async Task<object?> TaskFromCreate(BrowserForm form)
    {
        var task = (Task)Call(form, "CreateTabAsync", "about:blank", "test", true, null, "test-owner")!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
}
