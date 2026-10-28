using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using OinziBrowser;
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
        var dir = Path.Combine(Path.GetTempPath(), "Oinzi-assistant-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("OINZI_DATA_DIR", dir);
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
                    first.GetType().GetProperty("Running")!.SetValue(first, true);
                    Call(form, "FlushAssistantHistory");
                    Call(restored, "LoadAssistantHistory");
                    var saved = (IList)FormType.GetField("conversations", Private)!.GetValue(restored)!;
                    Check(saved.Cast<object>().Any(c => (string?)c.GetType().GetProperty("Id")!.GetValue(c) == firstId), "rooms survive app restart");
                    var selected = ((IDictionary)FormType.GetField("currentConversations", Private)!.GetValue(restored)!)["claude"]!;
                    Check((string?)selected.GetType().GetProperty("Id")!.GetValue(selected) == firstId, "restart restores selected older room");
                    Check((bool)selected.GetType().GetProperty("Interrupted")!.GetValue(selected)! && !(bool)selected.GetType().GetProperty("Running")!.GetValue(selected)!, "interrupted run becomes recoverable after restart");
                    Check((string?)selected.GetType().GetProperty("Session")!.GetValue(selected) == "test-session", "interrupted run retains CLI session");
                    first.GetType().GetProperty("Running")!.SetValue(first, false);
                }
                Call(form, "PostAssistant", "claude", new { kind = "done", error = "Interrupted test" });
                await Task.Delay(100);
                Check(await chrome.CoreWebView2.ExecuteScriptAsync("!document.querySelector('#resumeWork').hidden") == "true", "interrupted conversation offers resume button");
                Call(form, "PostAssistant", "claude", new { kind = "done" });
                await Task.Delay(100);
                Check(await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#resumeWork').hidden") == "true", "successful completion clears resume button");
                var runs = (IDictionary)FormType.GetField("aiRuns", Private)!.GetValue(form)!;
                using (var placeholder = new Process())
                using (var shutdown = JsonDocument.Parse("{\"command\":\"shutdown\"}"))
                {
                    runs["claude"] = placeholder;
                    var rejected = false;
                    try { await (Task<object>)Call(form, "CommandAsync", shutdown.RootElement)!; }
                    catch (InvalidOperationException) { rejected = true; }
                    finally { runs.Remove("claude"); }
                    Check(rejected && !form.IsDisposed, "automatic shutdown refuses to interrupt active AI");
                }
                var info = new ProcessStartInfo("test.exe");
                info.Environment["WT_SESSION"] = "other-terminal";
                info.Environment["CODEX_THREAD_ID"] = "other-thread";
                info.Environment["CODEX_HOME"] = "keep-auth-location";
                FormType.GetMethod("IsolateAssistantEnvironment", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [info]);
                Check(!info.Environment.ContainsKey("WT_SESSION") && !info.Environment.ContainsKey("CODEX_THREAD_ID") && info.Environment["CODEX_HOME"] == "keep-auth-location", "terminal session identities removed, auth location retained");

                var tab = await (Task<object?>)TaskFromCreate(form);
                var view = (WebView2)tab!.GetType().GetProperty("View")!.GetValue(tab)!;
                view.CoreWebView2.NavigateToString("<html><title>Interaction test</title><body style='padding:80px;font:20px system-ui'><h1>Oinzi interaction test</h1><input id='field'><button id='button' onclick=\"document.body.dataset.clicked='yes'\">Click me</button></body></html>");
                await Task.Delay(500);
                Check((bool)(await (Task<bool>)Call(form, "ActAsync", tab, "fill", "#field", "hello")!), "fill succeeds with overlay");
                Check((bool)(await (Task<bool>)Call(form, "ActAsync", tab, "click", "#button", "")!), "click succeeds with overlay");
                Check(await view.CoreWebView2.ExecuteScriptAsync("document.body.dataset.clicked === 'yes' && document.querySelector('#field').value === 'hello'") == "true", "actions reach real page elements");
                Check(await view.CoreWebView2.ExecuteScriptAsync("typeof window.__oinziActivity") == "\"function\"", "live visual overlay installed");
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
                await chrome.CoreWebView2.ExecuteScriptAsync("(() => { const c=document.createElement('canvas'); c.width=c.height=32; const ctx=c.getContext('2d');ctx.fillStyle='red';ctx.fillRect(0,0,32,32);c.toBlob(blob=>{ window.fixtureImage=new File([blob],'red.png',{type:'image/png'});attachImages([fixtureImage]); }); })()");
                await Task.Delay(350);
                var pending = (IDictionary)FormType.GetField("pendingImages", Private)!.GetValue(form)!;
                var attachments = (IList)pending["claude"]!;
                Check(attachments.Count == 1, "file selection flow saves image attachment");
                Check(await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('#imageDrafts img').length === 1 && !aiSend.disabled") == "true", "preview appears and image-only send is enabled");
                var attached = attachments[0]!;
                var attachmentArray = Array.CreateInstance(attached.GetType(), 1); attachmentArray.SetValue(attached, 0);
                var payload = (string)Call(form, "ClaudeInput", "Inspect image", attachmentArray)!;
                using var payloadJson = JsonDocument.Parse(payload);
                Check(payloadJson.RootElement.GetProperty("message").GetProperty("content")[1].GetProperty("source").GetProperty("media_type").GetString() == "image/png", "Claude receives structured image input, not a file-name-only prompt");
                Check(File.Exists((string)Call(form, "ImagePath", attached)!), "original image exists for Codex image argument");
                Call(form, "AddImage", "claude", "bad.txt", "data:text/plain;base64,aW52YWxpZA==", "");
                Check(attachments.Count == 1, "invalid attachment rejected without damaging draft");
                await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#imageDrafts button').click()");
                await Task.Delay(100);
                Check(attachments.Count == 0, "remove button detaches pending image");
                await chrome.CoreWebView2.ExecuteScriptAsync("(() => { const data=new DataTransfer(); data.items.add(fixtureImage); document.querySelector('.composer').dispatchEvent(new ClipboardEvent('paste',{clipboardData:data,bubbles:true,cancelable:true})); })()");
                await Task.Delay(250);
                Check(attachments.Count == 1, "clipboard paste adds image through composer");
                await chrome.CoreWebView2.ExecuteScriptAsync("(() => { const data=new DataTransfer(); data.items.add(fixtureImage); document.querySelector('.composer').dispatchEvent(new DragEvent('drop',{dataTransfer:data,bubbles:true,cancelable:true})); })()");
                await Task.Delay(250);
                Check(attachments.Count == 2, "drag and drop adds image through composer");
                using (var queuedRun = new Process())
                {
                    runs["claude"] = queuedRun;
                    try
                    {
                        Call(form, "PostAssistantStatus");
                        await Task.Delay(100);
                        await chrome.CoreWebView2.ExecuteScriptAsync("aiInput.value='First follow-up'; aiInput.dispatchEvent(new Event('input')); aiInput.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}))");
                        await Task.Delay(150);
                        await chrome.CoreWebView2.ExecuteScriptAsync("aiInput.value='Second follow-up'; aiInput.dispatchEvent(new Event('input')); aiSend.click()");
                        await Task.Delay(150);
                        var queue = (IList)first.GetType().GetProperty("Pending")!.GetValue(first)!;
                        Check(queue.Count == 2 && ReferenceEquals(runs["claude"], queuedRun), "Enter and send queue follow-ups without stopping current run");
                        Check(((Array)queue[0]!.GetType().GetProperty("Images")!.GetValue(queue[0])!).Length == 2 && attachments.Count == 0,
                            "queued images stay attached to their own request");
                        Check(await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#aiQueue').textContent.includes('Second follow-up') && !document.querySelector('#aiStop').hidden && aiInput.value === ''") == "true",
                            "pending requests visible while composer stays available");
                        Call(form, "FlushAssistantHistory");
                        using (var queueRestart = new BrowserForm())
                        {
                            Call(queueRestart, "LoadAssistantHistory");
                            var restoredChat = ((IDictionary)FormType.GetField("currentConversations", Private)!.GetValue(queueRestart)!)["claude"]!;
                            Check(((IList)restoredChat.GetType().GetProperty("Pending")!.GetValue(restoredChat)!).Count == 2,
                                "pending requests survive restart without automatically executing");
                        }
                        await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#aiQueue button').click()");
                        await Task.Delay(100);
                        Check(queue.Count == 1 && (string?)queue[0]!.GetType().GetProperty("Text")!.GetValue(queue[0]) == "Second follow-up", "cancel removes only selected queued request");
                        queue.Clear(); Call(form, "PostQueue", "claude");
                    }
                    finally { runs.Remove("claude"); Call(form, "PostAssistantStatus"); }
                }
                attachments.Clear(); Call(form, "PostImages", "claude");
                Call(form, "PostAssistant", "claude", new { kind = "user", text = "Describe this image", images = attachmentArray });
                Call(form, "PostAssistant", "claude", new { kind = "delta", text = "Unfinished answer about the red image" });
                Call(form, "RecordAssistant", "codex", new { kind = "user", text = "Existing Codex conversation" });
                var oldTarget = chats["codex"]!;
                await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('.seg button[data-engine=codex]').click()");
                Check(await chrome.CoreWebView2.ExecuteScriptAsync("!document.querySelector('#handoff').hidden") == "true", "switching engines offers conversation handoff");
                await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#handoffGo').click()");
                await Task.Delay(250);
                var transferred = chats["codex"]!;
                var allChats = (IList)FormType.GetField("conversations", Private)!.GetValue(form)!;
                Check(!ReferenceEquals(oldTarget, transferred) && allChats.Contains(oldTarget) && ReferenceEquals(first, chats["claude"]), "handoff preserves both original conversation rooms");
                Check((bool)transferred.GetType().GetProperty("NeedsContext")!.GetValue(transferred)! && !sessions.Contains("codex"), "handoff creates a fresh target session with pending context");
                var context = (string)Call(form, "HandoffContext", transferred, dir)!;
                Check(context.Contains("Saved conversation") && context.Contains("Unfinished answer") && context.Contains("Image:"), "handoff retains earlier messages, partial answer and image paths");
                Check(((Array)Call(form, "HandoffImages", transferred)!).Length == 1, "image accompanies the first target-engine prompt");
                Check(await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('.log[data-engine=codex] img') !== null") == "true", "transferred conversation displays prior photo");
                Call(form, "FlushAssistantHistory");
                using (var reopened = new BrowserForm())
                {
                    Call(reopened, "LoadAssistantHistory");
                    var restoredChats = (IDictionary)FormType.GetField("currentConversations", Private)!.GetValue(reopened)!;
                    var restoredChat = restoredChats["codex"]!;
                    Check((bool)restoredChat.GetType().GetProperty("NeedsContext")!.GetValue(restoredChat)! && ((Array)Call(reopened, "HandoffImages", restoredChat)!).Length == 1, "handoff context and photos survive restart before next prompt");
                }
                await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('.seg button[data-engine=claude]').click(); document.querySelector('#handoffGo').click()");
                await Task.Delay(200);
                Check(!ReferenceEquals(first, chats["claude"]) && allChats.Contains(first) && ((Array)Call(form, "HandoffImages", chats["claude"])!).Length == 1, "Codex to Claude handoff also preserves photos and original room");
                await chrome.CoreWebView2.ExecuteScriptAsync("document.querySelector('#imageError').textContent=''");
                using (var image = File.Create(Path.Combine(dir, "chrome.png"))) await chrome.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                var localCard = Path.Combine(dir, "local card.html");
                File.WriteAllText(localCard, "<html><body>Local preview fixture</body></html>");
                var localUrl = (string)FormType.GetMethod("NormalizeUrl", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [localCard])!;
                view.CoreWebView2.Navigate(localUrl);
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    if (await view.CoreWebView2.ExecuteScriptAsync("document.body?.textContent.includes('Local preview fixture')") == "true") break;
                    await Task.Delay(100);
                }
                Check(view.Source.IsFile && await view.CoreWebView2.ExecuteScriptAsync("document.body.textContent.includes('Local preview fixture')") == "true",
                    "local HTML path opens as file preview without DNS lookup");
                view.CoreWebView2.AddWebResourceRequestedFilter("https://oinzi-test.invalid/*", CoreWebView2WebResourceContext.All);
                view.CoreWebView2.WebResourceRequested += (_, request) => request.Response = view.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<html><title>Saved draft</title></html>")), 200, "OK", "Content-Type: text/html");
                view.CoreWebView2.Navigate("https://oinzi-test.invalid/saved-blog-draft");
                var tabControl = (TabControl)FormType.GetField("tabs", Private)!.GetValue(form)!;
                tabControl.SelectedTab = (TabPage)tab.GetType().GetProperty("Page")!.GetValue(tab)!;
                for (var attempt = 0; attempt < 50 && view.Source?.AbsolutePath != "/saved-blog-draft"; attempt++) await Task.Delay(100);
                Call(form, "SaveTabs");
                var persisted = (Array)Call(form, "ReadSavedTabs")!;
                Check(persisted.Cast<object>().Any(t => (string?)t.GetType().GetProperty("Space")!.GetValue(t) == "test"
                    && (bool)t.GetType().GetProperty("Active")!.GetValue(t)!), "AI task URL, space and selection survive save/read");
                using (var restarted = new BrowserForm())
                {
                    restarted.Show();
                    for (var attempt = 0; attempt < 150; attempt++)
                    {
                        if (FormType.GetField("listener", Private)!.GetValue(restarted) != null) break;
                        await Task.Delay(100);
                    }
                    var restoredActive = Call(restarted, "Active")!;
                    Check((string?)restoredActive.GetType().GetProperty("Space")!.GetValue(restoredActive) == "test",
                        "real browser restart selects prior AI task instead of first user tab");
                    var restoredView = (WebView2)restoredActive.GetType().GetProperty("View")!.GetValue(restoredActive)!;
                    for (var attempt = 0; attempt < 100 && restoredView.Source?.AbsolutePath != "/saved-blog-draft"; attempt++) await Task.Delay(100);
                    Check(restoredView.Source?.AbsolutePath == "/saved-blog-draft",
                        "real browser restart reopens saved task URL");
                    restarted.Close();
                }
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
