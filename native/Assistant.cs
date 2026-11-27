using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OinziBrowser;

// The AI panel runs the person's own Claude Code or Codex CLI headlessly, so their subscriptions are used as-is.
// Every conversation gets its own CLI process, so several can work at once. Claude keeps its stream-json input
// open and Codex runs as an app-server, so a message sent while a run is busy steers the turn in progress.
public sealed partial class BrowserForm
{
    private static readonly string[] Engines = ["claude", "codex"];
    // acceptEdits already covers file edits inside the working folders, so only these extra tools are opened.
    private const string RestrictedClaudeTools = "Bash(node:*),PowerShell(node *),Read,WebSearch,WebFetch,TodoWrite,Skill";

    private sealed class AssistantRun(Conversation chat, Process process)
    {
        public Conversation Chat { get; } = chat;
        public Process Process { get; } = process;
        // Adds a message to the turn in progress; false once the CLI can no longer take one.
        public Func<string, AttachedImage[], bool> Steer { get; set; } = (_, _) => false;
        public bool Finished { get; set; }
        public Func<string, bool> Command { get; set; } = _ => false;
        // The CLI session was created by this run; if the run fails, the CLI may never have saved it.
        public bool NewSession { get; set; }
        public Queue<string> Errors { get; } = new();
    }

    // Keyed by conversation id.
    private readonly Dictionary<string, AssistantRun> aiRuns = [];
    private readonly string browserRuntime = Guid.NewGuid().ToString("N");
    private string AssistantDir => Path.Combine(dataDir, "assistant");

    private string PrepareAssistantWorkspace(string engine)
    {
        var workspace = Path.Combine(AssistantDir, engine);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "agent.mjs"), ReadResource("agent.mjs"));
        // Instructions now travel as a system prompt, so stale copies from older versions must not load as project files.
        foreach (var stale in new[] { "CLAUDE.md", "AGENTS.md" }) File.Delete(Path.Combine(workspace, stale));
        InstallSkills(workspace);
        return workspace;
    }

    // Bundled skills live only in Oinzi's own folders (never the person's chosen project or ~/.claude), so they
    // serve the AI panel without touching the person's other Claude setups. Rewritten when the app ships new text.
    private static void InstallSkills(string folder)
    {
        foreach (var name in typeof(BrowserForm).Assembly.GetManifestResourceNames().Where(name => name.StartsWith("skills/", StringComparison.Ordinal)))
        {
            try
            {
                var target = Path.Combine(folder, ".claude", "skills", name["skills/".Length..].Replace('\\', '/'));
                var text = ReadResource(name);
                if (File.Exists(target) && File.ReadAllText(target) == text) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string AssistantInstructions(string agent, string scripts) =>
        (prefs.CustomInstructions.Trim() is { Length: > 0 } custom ? "# The person's standing instructions\n" + custom + "\n\n" : "")
        + ReadResource("assistant.md").Replace("{{AGENT}}", agent).Replace("{{SCRIPTS}}", scripts).Replace("{{SCREENSHOTS}}", ScreenshotsDir)
            .Replace("{{SKILLS}}", Path.Combine(Path.GetDirectoryName(agent)!, ".claude", "skills"));

    private static string ReadResource(string name)
    {
        using var stream = typeof(BrowserForm).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Codex 0.16x runs through a shared background server by default. A server Oinzi cannot open (the Codex desktop app's,
    // or one started as administrator) fails with "access denied (os error 5)", so Oinzi asks for its own process.
    private static readonly Dictionary<string, bool> codexNoDaemon = [];

    private static void AddCodexDaemonFlag(ProcessStartInfo info)
    {
        bool supported;
        lock (codexNoDaemon)
        {
            if (!codexNoDaemon.TryGetValue(info.FileName, out supported))
            {
                try
                {
                    using var help = Process.Start(new ProcessStartInfo(info.FileName, "--help")
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                    });
                    var text = help?.StandardOutput.ReadToEnd() ?? "";
                    help?.WaitForExit(5000);
                    supported = text.Contains("--no-daemon", StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { supported = false; }
                codexNoDaemon[info.FileName] = supported;
            }
        }
        if (supported) info.ArgumentList.Add("--no-daemon");
    }

    private static string? FindTool(string engine)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(home, ".local", "bin"))
            .Append(Path.Combine(CodexInstallDir, "bin"))
            .Append(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"))
            .Append(Path.Combine(local, "Microsoft", "WinGet", "Links"))
            // npm installs only a codex.cmd shim on PATH; the native binary sits inside the package.
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex",
                "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin"));
        foreach (var dir in dirs)
        {
            try
            {
                var path = Path.Combine(dir.Trim().Trim('"'), engine + ".exe");
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private void PostAssistantStatus() => PostChrome(new
    {
        type = "ai.status",
        engines = Engines.ToDictionary(engine => engine, engine => new
        {
            available = FindTool(engine) != null,
            running = currentConversations.GetValueOrDefault(engine) is { } shown && aiRuns.ContainsKey(shown.Id),
            busy = aiRuns.Values.Count(run => run.Chat.Engine == engine)
        }),
        runningChats = aiRuns.Keys.ToArray()
    });

    // Every event is recorded on its conversation; only the conversation on screen streams to the panel.
    private void PostAssistant(Conversation chat, object data)
    {
        var entry = data as JsonObject ?? JsonSerializer.SerializeToNode(data)!.AsObject();
        var kind = entry["kind"]?.ToString();
        var shown = currentConversations.GetValueOrDefault(chat.Engine) == chat;
        if (kind == "usage") RecordAccountUsage(chat, entry);
        if (kind == "done") FinishScheduleRun(chat, entry["error"]?.ToString());
        // A run that failed early (for example before logging in) can leave a session id the CLI never saved. Resuming it
        // fails every time, whether the CLI reports it in its result or on stderr, so the next message uses the transcript.
        if (kind == "done" && entry["error"]?.ToString().Contains("No conversation found", StringComparison.OrdinalIgnoreCase) == true)
        { chat.Session = null; chat.NeedsContext = true; }
        if (kind == "done")
        {
            chat.Running = false;
            chat.Interrupted = entry["error"] != null;
            if (shown) PostChrome(new { type = "ai.recovery", engine = chat.Engine, interrupted = chat.Interrupted });
        }
        // Live-only events would bloat the saved history; the matching final event is recorded instead.
        if (kind is not ("thinkingDelta" or "toolOutput" or "status")) RecordAssistant(chat, entry);
        if (kind is "user" or "text" or "tool") RecordConversation(chat, entry);
        if (shown) PostChrome(new { type = "ai", engine = chat.Engine, @event = entry });
    }

    private static void IsolateAssistantEnvironment(ProcessStartInfo info)
    {
        // A headless child must not inherit another terminal/agent's IPC or session identity.
        foreach (var key in info.Environment.Keys.ToArray())
            if (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) && key != "CODEX_HOME"
                || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("ORCA_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("OINZI_SESSION", StringComparison.OrdinalIgnoreCase)
                || key.Equals("OINZI_PROFILE", StringComparison.OrdinalIgnoreCase)
                || key is "CLAUDECODE" or "WT_SESSION" or "WT_PROFILE_ID" or "TERM_SESSION_ID" or "TERM_PROGRAM" or "TERM_PROGRAM_VERSION")
                info.Environment.Remove(key);
        info.Environment["TERM"] = "dumb";
        info.Environment["NO_COLOR"] = "1";
    }

    private void StartAssistant(Conversation chat, string text, string submittedContext, AttachedImage[] attachments)
    {
        var engine = chat.Engine;
        if (aiRuns.ContainsKey(chat.Id) || (string.IsNullOrWhiteSpace(text) && attachments.Length == 0)) return;
        var exe = FindTool(engine);
        if (exe == null)
        {
            PostAssistant(chat, new { kind = "error", text = engine == "claude"
                ? "Claude Code CLI(claude.exe)를 찾을 수 없습니다. 설치 후 `claude`로 로그인하세요."
                : "Codex CLI(codex.exe)를 찾을 수 없습니다. 설치 후 `codex login`으로 로그인하세요." });
            PostAssistantStatus();
            return;
        }
        // Both engines need the slash command at the beginning of the input.
        var command = ParseSlash(text) != null;
        var prompt = command ? text.Trim() : submittedContext + (string.IsNullOrWhiteSpace(text) ? "첨부한 사진을 살펴봐 주세요." : text.Trim());
        var workspace = PrepareAssistantWorkspace(engine);
        // Scripts get a folder per conversation so concurrent conversations never overwrite each other's steps.
        var scripts = Path.Combine(workspace, "runs", chat.Id[..Math.Min(12, chat.Id.Length)]);
        Directory.CreateDirectory(scripts);
        var images = (chat.NeedsContext ? HandoffImages(chat) : []).Concat(attachments).DistinctBy(image => image.Id).TakeLast(6).ToArray();
        if (images.Any(image => !File.Exists(ImagePath(image))))
        {
            PostAssistant(chat, new { kind = "error", text = "첨부한 사진 파일을 찾을 수 없습니다. 다시 첨부해 주세요." });
            PostAssistantStatus();
            return;
        }
        var cwd = AssistantWorkingDirectory(chat, workspace);
        // Claude keeps sessions per folder, so a session started elsewhere (including the old default) continues from its transcript.
        if (chat.Engine == "claude" && chat.Session != null && !string.Equals(chat.SessionCwd ?? workspace, cwd, StringComparison.OrdinalIgnoreCase))
        { chat.Session = null; chat.NeedsContext = true; }
        chat.SessionCwd = cwd;
        // Sessions are stored in the account's config folder, so after switching accounts the conversation continues from its transcript.
        var account = ActiveAccountId(engine) is var active && active == DefaultAccount ? null : active;
        if (command && engine == "codex" && chat.Session != null && chat.SessionAccount != account)
        {
            PostAssistant(chat, new { kind = "error", text = "이 대화에서 사용한 Codex 계정으로 전환한 뒤 명령을 실행해 주세요." });
            return;
        }
        if (chat.Session != null && chat.SessionAccount != account) { chat.Session = null; chat.NeedsContext = true; }
        chat.SessionAccount = account;
        if (cwd == AiWorkDir) InstallSkills(AiWorkDir);
        if (chat.NeedsContext && !command) prompt = HandoffContext(chat, scripts) + prompt;
        var space = SpaceOf(chat);
        var browserSession = OpenSession(space, SessionTitle(chat), "panel");
        browserSession.Kind = "panel";
        chat.Profile = FindProfile(chat.Profile ?? "")?.Id ?? userProfile;
        if (browserSession.Profile != chat.Profile) { browserSession.Profile = chat.Profile; RefreshSidebar(); }
        if (chat.BrowserRuntime != browserRuntime && !command)
            prompt = "[Browser restarted or new session] Previous tab IDs and element references may be stale. Match the prior task to your session's current tabs by URL, then take a fresh snapshot. The visible tab may be unrelated: do not switch the task to it merely because it is active. If the task tab is missing, reopen its known URL from the conversation and inspect the site's saved draft before repeating edits. Unsaved page memory may be lost. Your session's tabs: "
                + JsonSerializer.Serialize(State(space)) + "\n\n" + prompt;
        if (images.Length > 0 && !command)
            prompt += "\n\n[Attached images, in order]\n" + string.Join("\n", images.Select(image => image.Name + ": " + ImagePath(image)));
        PostAssistant(chat, new { kind = "user", text, images = attachments });

        var info = AssistantProtocolProcess(exe, cwd);
        IsolateAssistantEnvironment(info);
        ApplyAccount(info, engine);
        if (connectionFile != null) info.Environment["OINZI_CONNECTION"] = connectionFile;
        info.Environment["OINZI_SESSION"] = space;
        info.Environment["OINZI_SESSION_LABEL"] = browserSession.Label;
        var instructions = AssistantInstructions(Path.Combine(workspace, "agent.mjs"), scripts);
        var started = engine == "claude"
            ? StartClaude(chat, info, prompt, images, instructions, cwd == workspace ? null : workspace)
            : StartCodex(chat, info, prompt, images, instructions, scripts);
        if (!started) { PostAssistantStatus(); return; }
        chat.Running = true;
        chat.Interrupted = false;
        chat.BrowserRuntime = browserRuntime;
        if (currentConversations.GetValueOrDefault(engine) == chat) PostChrome(new { type = "ai.recovery", engine, interrupted = false });
        FlushAssistantHistory();
        PostAssistantStatus();
    }

    private AssistantRun? Launch(Conversation chat, ProcessStartInfo info)
    {
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PostAssistant(chat, new { kind = "error", text = $"{chat.Engine} 실행 실패: {ex.Message}" });
            process.Dispose();
            return null;
        }
        var run = new AssistantRun(chat, process);
        aiRuns[chat.Id] = run;
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                lock (run.Errors) { run.Errors.Enqueue(line); if (run.Errors.Count > 12) run.Errors.Dequeue(); }
        });
        return run;
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    // Events from a run that was stopped or replaced are dropped.
    private void Emit(AssistantRun run, JsonObject data) => OnUi(() =>
    {
        if (aiRuns.GetValueOrDefault(run.Chat.Id) != run || run.Finished) return;
        var chat = run.Chat;
        switch (data["kind"]?.GetValue<string>())
        {
            case "session":
                run.NewSession |= chat.Session == null;
                chat.Session = data["id"]!.GetValue<string>();
                SaveAssistantHistory();
                return;
            case "persisted":
                run.NewSession = false;
                return;
            case "rename":
                RenameAssistantChat(chat, data["text"]!.GetValue<string>());
                return;
            case "done":
                run.Finished = true;
                if (data["error"] == null && chat.Session != null) chat.NeedsContext = false;
                else if (data["error"] != null) ForgetFailedSession(run);
                break;
        }
        PostAssistant(chat, data);
        if (data["kind"]?.GetValue<string>() == "done") { PostAssistantStatus(); NotifyDone(chat, data["error"] != null); }
    });

    private void WatchExit(AssistantRun run, Task reader) => _ = Task.Run(async () =>
    {
        await run.Process.WaitForExitAsync();
        // A grandchild that inherited the output pipe must not keep the run alive after the CLI itself exited.
        await Task.WhenAny(reader, Task.Delay(2000));
        OnUi(() => FinishRun(run));
    });

    private void FinishRun(AssistantRun run)
    {
        if (aiRuns.GetValueOrDefault(run.Chat.Id) != run) { run.Process.Dispose(); return; }
        aiRuns.Remove(run.Chat.Id);
        if (!run.Finished)
        {
            string tail;
            lock (run.Errors) tail = string.Join("\n", run.Errors.Where(line => !line.Contains(" ERROR rmcp") && !line.Contains("codex_rmcp_client")).TakeLast(6));
            var code = run.Process.ExitCode;
            if (code != 0) ForgetFailedSession(run);
            PostAssistant(run.Chat, code == 0
                ? new JsonObject { ["kind"] = "done" }
                : new JsonObject { ["kind"] = "done", ["error"] = string.IsNullOrWhiteSpace(tail) ? $"종료 코드 {code}" : tail });
        }
        PostAssistantStatus();
        run.Process.Dispose();
        if (!run.Chat.Interrupted) RunNextPrompt(run.Chat);
    }

    // A session created by a failed run (for example before logging in) may not exist; resuming it would fail on every message.
    private void ForgetFailedSession(AssistantRun run)
    {
        if (!run.NewSession || run.Chat.Session == null) return;
        run.Chat.Session = null;
        run.Chat.NeedsContext = true;
        SaveAssistantHistory();
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    private void StopAssistant(Conversation chat)
    {
        if (!aiRuns.TryGetValue(chat.Id, out var run)) return;
        Kill(run.Process);
        // Removing the run first makes its exit handler and late output no-ops.
        aiRuns.Remove(chat.Id);
        PostAssistant(chat, new JsonObject { ["kind"] = "done", ["error"] = "중지했습니다." });
        PostAssistantStatus();
    }

    private void StopAllAssistants()
    {
        foreach (var run in aiRuns.Values.ToArray())
        {
            run.Chat.Running = false;
            run.Chat.Interrupted = true;
            Kill(run.Process);
        }
        aiRuns.Clear();
        if (historyLoaded) SaveAssistantHistory();
    }

    private static string Clip(string? text, int limit) => text == null ? "" : text.Length <= limit ? text : text[..limit] + "…";

    private static string ResultText(JsonNode? content) => content switch
    {
        JsonValue value => value.ToString(),
        JsonArray items => string.Join("\n", items.Select(item => item?["text"]?.ToString()).Where(text => text != null)),
        _ => ""
    };

    // ---------- Claude Code: stream-json in and out ----------

    private bool StartClaude(Conversation chat, ProcessStartInfo info, string prompt, AttachedImage[] images, string instructions, string? workspace)
    {
        void Add(params string[] args) { foreach (var arg in args) info.ArgumentList.Add(arg); }
        Add("-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--replay-user-messages");
        switch (chat.Mode)
        {
            case "full": Add("--dangerously-skip-permissions"); break;
            case "plan": Add("--permission-mode", "plan", "--allowedTools", RestrictedClaudeTools); break;
            default: Add("--permission-mode", "acceptEdits", "--allowedTools", RestrictedClaudeTools); break;
        }
        if (chat.Model is { Length: > 0 } model) Add("--model", model);
        if (chat.Effort is { Length: > 0 } effort) Add("--effort", effort);
        Add("--append-system-prompt", instructions);
        if (workspace != null) Add("--add-dir", workspace);
        if (chat.Session != null) Add("--resume", chat.Session);
        string first;
        try { first = ClaudeInput(prompt, images); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PostAssistant(chat, new { kind = "error", text = "첨부한 사진을 읽지 못했습니다: " + ex.Message });
            return false;
        }
        if (Launch(chat, info) is not { } run) return false;
        var process = run.Process;
        var gate = new object();
        var (written, replayed, closed) = (0, 0, false);
        bool Write(string line)
        {
            lock (gate)
            {
                if (closed) return false;
                try { process.StandardInput.WriteLine(line); process.StandardInput.Flush(); written++; return true; }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { return false; }
            }
        }
        run.Steer = (text, attached) => Write(ClaudeInput(text, attached));
        var slash = ParseSlash(prompt) != null;
        _ = Task.Run(() =>
        {
            if (Write(first)) return;
            Emit(run, new JsonObject { ["kind"] = "error", ["text"] = "메시지를 Claude에 전달하지 못했습니다." });
            Kill(process);
        });
        var reader = Task.Run(async () =>
        {
            var receivedText = false;
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (message == null) continue;
                if (message["type"]?.ToString() == "user" && message["isReplay"]?.GetValue<bool>() == true) { lock (gate) replayed++; continue; }
                foreach (var item in ParseClaude(message))
                {
                    if (item["kind"]?.ToString() == "text") receivedText = true;
                    if (item["kind"]?.ToString() == "done")
                    {
                        // A message sent mid-run that Claude has not taken in yet starts another turn in this process.
                        lock (gate)
                        {
                            if (replayed < written) continue;
                            closed = true;
                            try { process.StandardInput.Close(); } catch (IOException) { }
                        }
                        // Local commands can return only a result, with no assistant message to render.
                        if (slash && !receivedText && item["error"] == null &&
                            message["result"]?.ToString() is { Length: > 0 } result)
                            Emit(run, new JsonObject { ["kind"] = "note", ["text"] =
                                result.Contains("isn't available in this environment", StringComparison.Ordinal)
                                    ? result + "\n/terminal로 같은 Claude 세션을 열 수 있습니다." : result });
                    }
                    Emit(run, item);
                }
            }
        });
        WatchExit(run, reader);
        return true;
    }

    private static string ClaudeStatus(string? status) => status switch
    {
        "requesting" => "응답을 기다리는 중",
        "compacting" => "대화를 압축하는 중",
        null or "" => "",
        _ => status
    };

    private static IEnumerable<JsonObject> ParseClaude(JsonObject message)
    {
        switch (message["type"]?.ToString())
        {
            case "system" when message["subtype"]?.ToString() == "init" && message["session_id"] is { } session:
                yield return new JsonObject { ["kind"] = "session", ["id"] = session.ToString() };
                break;
            case "system" when message["subtype"]?.ToString() == "status":
                yield return new JsonObject { ["kind"] = "status", ["text"] = ClaudeStatus(message["status"]?.ToString()) };
                break;
            case "system" when message["subtype"]?.ToString() == "task_started":
                yield return new JsonObject { ["kind"] = "status", ["text"] = "백그라운드 작업: " + Clip(message["description"]?.ToString(), 120) };
                break;
            case "system" when message["subtype"]?.ToString() == "compact_boundary":
                yield return new JsonObject { ["kind"] = "note", ["text"] = "대화를 압축했습니다." };
                break;
            case "stream_event":
                var streamed = message["event"];
                var deltaType = streamed?["delta"]?["type"]?.ToString();
                if (streamed?["type"]?.ToString() == "content_block_delta" && deltaType == "text_delta")
                    yield return new JsonObject { ["kind"] = "delta", ["text"] = streamed["delta"]!["text"]?.ToString() };
                else if (streamed?["type"]?.ToString() == "content_block_delta" && deltaType == "thinking_delta")
                    yield return new JsonObject { ["kind"] = "thinkingDelta", ["text"] = streamed["delta"]!["thinking"]?.ToString() };
                break;
            case "assistant":
                foreach (var block in message["message"]?["content"]?.AsArray() ?? [])
                {
                    var blockType = block?["type"]?.ToString();
                    if (blockType == "text")
                        yield return new JsonObject { ["kind"] = "text", ["text"] = block!["text"]?.ToString() };
                    else if (blockType == "thinking" && !string.IsNullOrWhiteSpace(block!["thinking"]?.ToString()))
                        yield return new JsonObject { ["kind"] = "thinking", ["text"] = block["thinking"]?.ToString() };
                    else if (blockType == "tool_use" && block!["name"]?.ToString() == "TodoWrite")
                        yield return new JsonObject
                        {
                            ["kind"] = "plan", ["id"] = block["id"]?.ToString(),
                            ["steps"] = new JsonArray((block["input"]?["todos"]?.AsArray() ?? []).Select(todo => (JsonNode)new JsonObject
                            {
                                ["step"] = (todo?["status"]?.ToString() == "in_progress" ? todo["activeForm"] : null)?.ToString() ?? todo?["content"]?.ToString(),
                                ["status"] = todo?["status"]?.ToString() switch { "in_progress" => "inProgress", "completed" => "completed", _ => "pending" }
                            }).ToArray())
                        };
                    else if (blockType == "tool_use")
                    {
                        var input = block!["input"];
                        var detail = input?["command"] ?? input?["file_path"] ?? input?["url"] ?? input?["query"] ?? input?["pattern"] ?? input?["description"];
                        yield return new JsonObject
                        {
                            ["kind"] = "tool", ["id"] = block["id"]?.ToString(), ["name"] = block["name"]?.ToString(),
                            ["detail"] = Clip(detail?.ToString(), 600), ["body"] = Clip(input?["content"]?.ToString() ?? input?["new_string"]?.ToString(), 4000)
                        };
                    }
                }
                break;
            case "user":
                foreach (var block in message["message"]?["content"]?.AsArray() ?? [])
                    if (block?["type"]?.ToString() == "tool_result")
                        yield return new JsonObject
                        {
                            ["kind"] = "toolDone", ["id"] = block["tool_use_id"]?.ToString(),
                            ["ok"] = block["is_error"]?.GetValue<bool>() != true, ["output"] = Clip(ResultText(block["content"]), 4000)
                        };
                break;
            case "rate_limit_event":
                var windows = message["rate_limit_info"]?["unifiedWindows"];
                if (windows != null)
                    yield return new JsonObject
                    {
                        ["kind"] = "usage",
                        ["five"] = windows["five_hour"]?["utilization"]?.DeepClone(),
                        ["week"] = windows["seven_day"]?["utilization"]?.DeepClone()
                    };
                break;
            case "result":
                var summary = new JsonObject { ["kind"] = "done", ["seconds"] = Math.Round((message["duration_ms"]?.GetValue<double>() ?? 0) / 1000) };
                if (message["is_error"]?.GetValue<bool>() == true)
                    summary["error"] = Clip(message["result"]?.ToString() ?? message["subtype"]?.ToString(), 1000);
                yield return summary;
                break;
        }
    }

    // ---------- Codex: app-server JSON-RPC over stdio ----------

    private static readonly Regex PowerShellWrapper = new("^\"?[^\"]*powershell(\\.exe)?\"?\\s+-Command\\s+(?<body>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static string ShellCommand(string? command)
    {
        var text = command ?? "";
        var match = PowerShellWrapper.Match(text);
        return match.Success ? match.Groups["body"].Value.Trim().Trim('\'', '"') : text;
    }

    private static JsonArray CodexInput(string text, AttachedImage[] images, Func<AttachedImage, string> path)
    {
        var input = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["text_elements"] = new JsonArray() });
        foreach (var image in images) input.Add(new JsonObject { ["type"] = "localImage", ["path"] = path(image) });
        return input;
    }

    private JsonObject CodexSandbox(Conversation chat, string scripts, string workingDirectory) => chat.Mode switch
    {
        "full" => new JsonObject { ["type"] = "dangerFullAccess" },
        "readonly" => new JsonObject { ["type"] = "readOnly", ["networkAccess"] = true },
        // agent.mjs talks to the browser over localhost, so even the restricted mode needs network access.
        _ => new JsonObject
        {
            ["type"] = "workspaceWrite", ["writableRoots"] = new JsonArray(scripts, workingDirectory), ["networkAccess"] = true,
            ["excludeTmpdirEnvVar"] = false, ["excludeSlashTmp"] = false
        }
    };

    private bool StartCodex(Conversation chat, ProcessStartInfo info, string prompt, AttachedImage[] images, string instructions, string scripts)
    {
        AddCodexDaemonFlag(info);
        var sandbox = CodexSandbox(chat, scripts, info.WorkingDirectory);
        if (sandbox["type"]?.ToString() == "workspaceWrite")
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("sandbox_workspace_write.network_access=true");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("sandbox_workspace_write.writable_roots=" + sandbox["writableRoots"]!.ToJsonString());
        }
        info.ArgumentList.Add("app-server");
        if (Launch(chat, info) is not { } run) return false;
        var process = run.Process;
        var gate = new object();
        var nextId = 0;
        var replies = new Dictionary<int, TaskCompletionSource<JsonNode?>>();
        string? threadId = null, turnId = null;
        var (model, effort, session, planMode) = (chat.Model, chat.Effort, chat.Session, chat.PlanMode);
        var slash = ParseSlash(prompt);
        string? goalStatus = null;
        var initialized = false;
        var ready = false;
        var closing = false;
        var commandsInFlight = 0;
        var activity = 0;
        var goalRevision = 0;
        JsonObject? completion = null;

        void Send(JsonObject message)
        {
            lock (gate)
            {
                try { process.StandardInput.WriteLine(message.ToJsonString()); process.StandardInput.Flush(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
            }
        }
        async Task<JsonNode> Request(string method, JsonObject parameters)
        {
            var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (gate) { id = ++nextId; replies[id] = reply; }
            Send(CodexRequest(id, method, parameters));
            try
            {
                var response = await reply.Task.WaitAsync(TimeSpan.FromSeconds(30));
                if (response == null) throw new InvalidOperationException("Codex 연결이 종료됐습니다.");
                if (response["error"] is { } error)
                    throw new InvalidOperationException(error["message"]?.ToString() ?? error.ToJsonString());
                return response["result"] ?? new JsonObject();
            }
            finally { lock (gate) replies.Remove(id); }
        }

        void Note(string text, bool error = false) =>
            Emit(run, new JsonObject { ["kind"] = error ? "error" : "note", ["text"] = text });

        static bool ProtocolError(Exception ex) => ex is IOException or InvalidOperationException or JsonException or TimeoutException;

        void End(JsonObject done)
        {
            Emit(run, done);
            try { process.StandardInput.Close(); } catch (Exception ex) when (ProtocolError(ex)) { }
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                Kill(process);
            });
        }

        void FinishWhenIdle(int delay = 400)
        {
            int stamp;
            lock (gate)
            {
                if (closing || !ready || commandsInFlight != 0 || turnId != null || completion == null) return;
                stamp = ++activity;
            }
            _ = Task.Run(async () =>
            {
                // Codex can start a continuation after reporting that the previous turn finished.
                await Task.Delay(delay);
                JsonObject done;
                lock (gate)
                {
                    if (closing || stamp != activity || commandsInFlight != 0 || turnId != null || completion == null) return;
                    if (goalStatus == "active")
                    {
                        // Only the app-server decides when the goal can start another turn.
                        Emit(run, new JsonObject { ["kind"] = "status", ["text"] = "GOAL 활성 · Codex의 다음 턴 대기 중" });
                        return;
                    }
                    closing = true;
                    done = completion;
                }
                End(done);
            });
        }

        JsonObject ThreadParameters() => new() { ["threadId"] = threadId ?? session };

        void RememberGoal(JsonNode? goal, int revision)
        {
            lock (gate)
                if (goalRevision == revision) goalStatus = goal?["status"]?.ToString();
        }

        async Task<JsonNode?> GoalCommand(string arguments)
        {
            if (threadId == null && session == null)
            {
                Note(CodexGoalSummary(null));
                return null;
            }
            int revision;
            lock (gate) revision = goalRevision;
            var goal = (await Request("thread/goal/get", ThreadParameters()))["goal"];
            // An unloaded conversation can be inspected without starting its active goal.
            if (threadId != null) RememberGoal(goal, revision);
            var action = arguments.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            var verb = action.FirstOrDefault()?.ToLowerInvariant();
            if (verb == null) { Note(CodexGoalSummary(goal)); return goal; }
            if (verb is "clear" or "pause" or "resume" && action.Length > 1)
            {
                Note($"사용법 /goal {verb}");
                return goal;
            }
            if (verb == "clear" && action.Length == 1)
            {
                var reply = await Request("thread/goal/clear", ThreadParameters());
                RememberGoal(null, revision);
                Note(reply["cleared"]?.GetValue<bool>() == true ? "GOAL을 지웠습니다." : CodexGoalSummary(null));
                return null;
            }
            var parameters = ThreadParameters();
            parameters["origin"] = "user";
            if (verb is "pause" or "resume" && action.Length == 1)
            {
                if (goal == null) { Note(CodexGoalSummary(null)); return null; }
                parameters["status"] = verb == "pause" ? "paused" : "active";
            }
            else
            {
                if (verb == "edit" && action.Length == 1) { Note(CodexGoalSummary(goal) + "\n/goal edit <새 목표>"); return goal; }
                if (verb != "edit" && goal?["status"]?.ToString() is { } status && status != "complete")
                {
                    Note(CodexGoalSummary(goal) + "\n목표를 바꾸려면 /goal edit <새 목표>를 사용하세요.");
                    return goal;
                }
                parameters["objective"] = verb == "edit" ? action[1] : arguments;
                if (verb != "edit") parameters["status"] = "active";
            }
            lock (gate) revision = goalRevision;
            var result = (await Request("thread/goal/set", parameters))["goal"];
            if (threadId != null) RememberGoal(result, revision);
            if (result != null) Emit(run, new JsonObject { ["kind"] = "persisted" });
            Note(CodexGoalSummary(result));
            return result;
        }

        async Task ShowUsage()
        {
            var result = await Request("account/rateLimits/read", new JsonObject());
            foreach (var item in ParseCodex("account/rateLimits/updated", result.AsObject())) Emit(run, item);
            Note(CodexUsageSummary(result));
        }

        run.Command = text =>
        {
            if (ParseSlash(text) is not { } command) return false;
            var name = command.Name.ToLowerInvariant();
            if (name is not ("goal" or "usage")) return false;
            lock (gate)
            {
                if (!initialized || !ready || closing || (name == "goal" && threadId == null)) return false;
                if (commandsInFlight != 0)
                {
                    Note("이전 명령이 끝난 뒤 다시 실행해 주세요.");
                    return true;
                }
                commandsInFlight++;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (name == "goal") await GoalCommand(command.Arguments);
                    else await ShowUsage();
                }
                catch (Exception ex) when (ProtocolError(ex)) { Note("명령을 실행하지 못했습니다. " + Clip(ex.Message, 600), true); }
                finally
                {
                    lock (gate) commandsInFlight--;
                    FinishWhenIdle();
                }
            });
            return true;
        };

        run.Steer = (text, attached) =>
        {
            string? target;
            lock (gate) target = closing ? null : turnId;
            if (threadId == null || target == null || run.Finished) return false;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Request("turn/steer", new JsonObject
                    { ["threadId"] = threadId, ["expectedTurnId"] = target, ["input"] = CodexInput(text, attached, ImagePath) });
                }
                catch (Exception ex) when (ProtocolError(ex)) { Note("추가 지시를 전달하지 못했습니다. " + Clip(ex.Message, 300), true); }
            });
            return true;
        };

        var reader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (message == null) continue;
                var method = message["method"]?.ToString();
                if (message["id"] is JsonValue id && method == null && id.TryGetValue<int>(out var number))
                {
                    TaskCompletionSource<JsonNode?>? reply;
                    lock (gate) replies.Remove(number, out reply);
                    reply?.TrySetResult(message);
                    continue;
                }
                if (message["id"] is { } requestId)
                {
                    // Approval requests should not arrive with approvalPolicy "never"; decline anything that does.
                    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Oinzi does not answer " + method } });
                    continue;
                }
                var parameters = message["params"] as JsonObject ?? new JsonObject();
                if (parameters["threadId"]?.ToString() is { } eventThread && threadId != null && eventThread != threadId) continue;
                if (method == "turn/started")
                {
                    lock (gate)
                    {
                        turnId = parameters["turn"]?["id"]?.ToString();
                        completion = null;
                        activity++;
                    }
                    Emit(run, new JsonObject { ["kind"] = "status", ["text"] = "작업 중" });
                }
                if (method == "thread/goal/updated" && parameters["goal"] is { } newGoal)
                {
                    Emit(run, new JsonObject { ["kind"] = "persisted" });
                    var status = newGoal["status"]?.ToString();
                    string? previous;
                    lock (gate) { previous = goalStatus; goalStatus = status; goalRevision++; }
                    if (previous != status && status is "paused" or "blocked" or "budgetLimited" or "usageLimited" or "complete")
                        Note(CodexGoalSummary(newGoal));
                    if (threadId != null) FinishWhenIdle();
                }
                if (method == "thread/goal/cleared")
                {
                    lock (gate) { goalStatus = null; goalRevision++; }
                    FinishWhenIdle();
                }
                foreach (var item in ParseCodex(method, parameters))
                {
                    if (item["kind"]?.ToString() != "done") { Emit(run, item); continue; }
                    if (item["error"] == null) Emit(run, new JsonObject { ["kind"] = "persisted" });
                    lock (gate)
                    {
                        turnId = null;
                        completion = item;
                    }
                    FinishWhenIdle();
                }
            }
            lock (gate) foreach (var reply in replies.Values) reply.TrySetResult(null);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Request("initialize", CodexInitialize());
                Send(JsonNode.Parse(CodexInitialized)!.AsObject());
                lock (gate) initialized = true;
                var name = slash?.Name.ToLowerInvariant();
                var arguments = slash?.Arguments ?? "";
                if (name == "usage") { await ShowUsage(); CompleteCommand(); return; }
                if (name == "skills")
                {
                    var skills = CodexSkills(await Request("skills/list", new JsonObject { ["cwds"] = new JsonArray(info.WorkingDirectory) }));
                    Note(skills.Length == 0 ? "이 작업 폴더에서 사용할 수 있는 Codex 스킬이 없습니다." :
                        string.Join("\n", skills.Select(item => "/" + item.Name + "  " + item.Hint)));
                    CompleteCommand();
                    return;
                }
                if (name == "goal" && (arguments.Length == 0 || arguments.ToLowerInvariant() is "pause" or "clear"))
                { await GoalCommand(arguments); CompleteCommand(); return; }
                if (name == "goal" && arguments.Equals("resume", StringComparison.OrdinalIgnoreCase) && session == null)
                { Note(CodexGoalSummary(null)); CompleteCommand(); return; }
                if (session == null && name is "compact" or "status" or "rename")
                {
                    if (name == "rename" && arguments.Length > 0)
                        Emit(run, new JsonObject { ["kind"] = "rename", ["text"] = arguments });
                    else Note(name switch
                    {
                        "status" => $"아직 Codex 세션이 시작되지 않았습니다.\n작업 폴더 {info.WorkingDirectory}",
                        "rename" => "사용법 /rename <대화 이름>",
                        _ => "대화를 시작한 뒤 /compact를 실행해 주세요."
                    });
                    CompleteCommand();
                    return;
                }
                SlashCommand? skill = null;
                if (name is not (null or "goal" or "compact" or "review" or "rename" or "status" or "plan"))
                {
                    var skills = CodexSkills(await Request("skills/list", new JsonObject { ["cwds"] = new JsonArray(info.WorkingDirectory) }));
                    var skillName = slash!.Value.Name;
                    skill = skills.FirstOrDefault(item => item.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
                    if (skill == null)
                    {
                        Note($"/{skillName}은 Codex 터미널에서 확인해 주세요. /terminal로 같은 세션을 열 수 있습니다.");
                        CompleteCommand();
                        return;
                    }
                }
                var thread = new JsonObject
                {
                    ["cwd"] = info.WorkingDirectory, ["approvalPolicy"] = "never", ["developerInstructions"] = instructions,
                    ["sandbox"] = sandbox["type"]?.ToString() switch
                    { "dangerFullAccess" => "danger-full-access", "readOnly" => "read-only", _ => "workspace-write" }
                };
                if (model is { Length: > 0 }) thread["model"] = model;
                JsonNode? opened = null;
                if (session != null)
                {
                    var resume = (JsonObject)thread.DeepClone();
                    resume["threadId"] = session;
                    resume["excludeTurns"] = true;
                    try { opened = await Request("thread/resume", resume); }
                    catch (InvalidOperationException) when (slash == null) { }
                }
                opened ??= await Request("thread/start", thread);
                threadId = opened["thread"]?["id"]?.ToString() ?? throw new InvalidOperationException("Codex did not return a thread");
                Emit(run, new JsonObject { ["kind"] = "session", ["id"] = threadId });
                switch (name)
                {
                    case "goal":
                        await GoalCommand(arguments);
                        CompleteCommand(400);
                        return;
                    case "compact":
                        if (arguments.Length > 0) throw new InvalidOperationException("Codex의 /compact는 인수 없이 실행합니다.");
                        await Request("thread/compact/start", ThreadParameters());
                        StartWaiting();
                        return;
                    case "review":
                        var review = ThreadParameters();
                        review["target"] = arguments.Length == 0 ? new JsonObject { ["type"] = "uncommittedChanges" } :
                            new JsonObject { ["type"] = "custom", ["instructions"] = arguments };
                        await Request("review/start", review);
                        StartWaiting();
                        return;
                    case "rename":
                        if (arguments.Length == 0) { Note("사용법 /rename <대화 이름>"); CompleteCommand(); return; }
                        var rename = ThreadParameters();
                        rename["name"] = arguments;
                        await Request("thread/name/set", rename);
                        Emit(run, new JsonObject { ["kind"] = "rename", ["text"] = arguments });
                        CompleteCommand();
                        return;
                    case "status":
                        Note($"Codex 세션 {threadId}\n모델 {opened["model"]}\n작업 폴더 {opened["cwd"]}\n권한 {thread["sandbox"]}");
                        CompleteCommand();
                        return;
                }
                var input = CodexInput(skill == null ? name == "plan" ? arguments : prompt
                    : "$" + skill.Name + (arguments.Length == 0 ? "" : " " + arguments), images, ImagePath);
                if (skill != null) input.Add(new JsonObject { ["type"] = "skill", ["name"] = skill.Name, ["path"] = skill.Path });
                var turn = new JsonObject
                {
                    ["threadId"] = threadId, ["input"] = input, ["sandboxPolicy"] = sandbox, ["summary"] = "auto"
                };
                if (model is { Length: > 0 }) turn["model"] = model;
                if (effort is { Length: > 0 }) turn["effort"] = effort;
                if (planMode || name == "plan" || opened["collaborationMode"]?["mode"]?.ToString() == "plan")
                    turn["collaborationMode"] = new JsonObject
                    {
                        ["mode"] = planMode || name == "plan" ? "plan" : "default",
                        ["settings"] = new JsonObject { ["model"] = model ?? opened["model"]?.ToString(),
                            ["reasoning_effort"] = effort ?? opened["reasoningEffort"]?.ToString(), ["developer_instructions"] = null }
                    };
                await Request("turn/start", turn);
                StartWaiting();
            }
            catch (Exception ex) when (ProtocolError(ex))
            {
                lock (gate) closing = true;
                End(new JsonObject { ["kind"] = "done", ["error"] = "Codex 요청 실패 · " + Clip(ex.Message, 600) });
            }
        });
        WatchExit(run, reader);
        return true;

        void StartWaiting()
        {
            lock (gate) ready = true;
            FinishWhenIdle();
        }

        void CompleteCommand(int delay = 0)
        {
            lock (gate)
            {
                ready = true;
                if (turnId == null) completion ??= new JsonObject { ["kind"] = "done" };
            }
            FinishWhenIdle(delay);
        }
    }

    private static IEnumerable<JsonObject> ParseCodex(string? method, JsonObject p)
    {
        var item = p["item"];
        var itemType = item?["type"]?.ToString();
        switch (method)
        {
            case "item/agentMessage/delta":
                yield return new JsonObject { ["kind"] = "delta", ["text"] = p["delta"]?.ToString() };
                break;
            case "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta":
                yield return new JsonObject { ["kind"] = "thinkingDelta", ["text"] = p["delta"]?.ToString() };
                break;
            case "item/commandExecution/outputDelta":
                yield return new JsonObject { ["kind"] = "toolOutput", ["id"] = p["itemId"]?.ToString(), ["text"] = p["delta"]?.ToString() };
                break;
            case "turn/plan/updated":
                yield return new JsonObject
                {
                    ["kind"] = "plan", ["explanation"] = p["explanation"]?.DeepClone(),
                    ["steps"] = new JsonArray((p["plan"]?.AsArray() ?? []).Select(step => (JsonNode)new JsonObject
                    { ["step"] = step?["step"]?.ToString(), ["status"] = step?["status"]?.ToString() }).ToArray())
                };
                break;
            case "item/started" when itemType == "commandExecution":
                yield return new JsonObject { ["kind"] = "tool", ["id"] = item!["id"]?.ToString(), ["name"] = "Shell", ["detail"] = Clip(ShellCommand(item["command"]?.ToString()), 600) };
                break;
            case "item/started" when itemType == "mcpToolCall":
                yield return new JsonObject { ["kind"] = "tool", ["id"] = item!["id"]?.ToString(), ["name"] = $"{item["server"]}.{item["tool"]}", ["detail"] = Clip(item["arguments"]?.ToJsonString(), 600) };
                break;
            case "item/started" when itemType == "webSearch":
                yield return new JsonObject { ["kind"] = "tool", ["id"] = item!["id"]?.ToString(), ["name"] = "WebSearch", ["detail"] = item["query"]?.ToString() ?? "" };
                break;
            case "item/started" when itemType == "reasoning":
                yield return new JsonObject { ["kind"] = "status", ["text"] = "생각하는 중" };
                break;
            case "item/completed":
                switch (itemType)
                {
                    case "agentMessage" or "plan":
                        yield return new JsonObject { ["kind"] = "text", ["text"] = item!["text"]?.ToString() };
                        break;
                    case "exitedReviewMode":
                        yield return new JsonObject { ["kind"] = "text", ["text"] = item!["review"]?.ToString() };
                        break;
                    case "reasoning":
                        var summary = string.Join("\n", (item!["summary"]?.AsArray() ?? []).Select(part => part?.ToString()));
                        if (!string.IsNullOrWhiteSpace(summary)) yield return new JsonObject { ["kind"] = "thinking", ["text"] = summary };
                        break;
                    case "commandExecution":
                        yield return new JsonObject
                        {
                            ["kind"] = "toolDone", ["id"] = item!["id"]?.ToString(),
                            ["ok"] = item["exitCode"]?.GetValue<int>() == 0 && item["status"]?.ToString() != "failed",
                            ["output"] = Clip(item["aggregatedOutput"]?.ToString(), 4000)
                        };
                        break;
                    case "fileChange":
                        var paths = string.Join(", ", item!["changes"]?.AsArray().Select(change => Path.GetFileName(change?["path"]?.ToString() ?? "")) ?? []);
                        yield return new JsonObject { ["kind"] = "tool", ["id"] = item["id"]?.ToString(), ["name"] = "Write", ["detail"] = paths };
                        yield return new JsonObject { ["kind"] = "toolDone", ["id"] = item["id"]?.ToString(), ["ok"] = item["status"]?.ToString() != "failed" };
                        break;
                    case "mcpToolCall" or "webSearch":
                        yield return new JsonObject { ["kind"] = "toolDone", ["id"] = item!["id"]?.ToString(), ["ok"] = item["status"]?.ToString() != "failed", ["output"] = Clip(item["result"]?.ToJsonString(), 4000) };
                        break;
                    case "contextCompaction":
                        yield return new JsonObject { ["kind"] = "note", ["text"] = "대화를 압축했습니다." };
                        break;
                }
                break;
            case "account/rateLimits/updated":
                var limits = p["rateLimits"];
                if (limits?["primary"] != null || limits?["secondary"] != null)
                    yield return new JsonObject
                    {
                        ["kind"] = "usage",
                        ["five"] = limits!["primary"]?["usedPercent"] is { } five ? five.GetValue<double>() / 100 : null,
                        ["week"] = limits["secondary"]?["usedPercent"] is { } week ? week.GetValue<double>() / 100 : null
                    };
                break;
            case "error":
                yield return new JsonObject { ["kind"] = "note", ["text"] = Clip(p["error"]?["message"]?.ToString() ?? p["message"]?.ToString(), 600) };
                break;
            case "turn/completed":
                var status = p["turn"]?["status"]?.ToString();
                var done = new JsonObject { ["kind"] = "done" };
                if (status == "failed") done["error"] = Clip(p["turn"]?["error"]?["message"]?.ToString() ?? "작업이 실패했습니다.", 1000);
                else if (status == "interrupted") done["error"] = "중단됐습니다.";
                yield return done;
                break;
        }
    }
}
