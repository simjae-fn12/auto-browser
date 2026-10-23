using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PluckWindowsNative;

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

    // Bundled skills live only in pluck's own folders (never the person's chosen project or ~/.claude), so they
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
        ReadResource("assistant.md").Replace("{{AGENT}}", agent).Replace("{{SCRIPTS}}", scripts).Replace("{{SCREENSHOTS}}", ScreenshotsDir)
            .Replace("{{SKILLS}}", Path.Combine(Path.GetDirectoryName(agent)!, ".claude", "skills"));

    private static string ReadResource(string name)
    {
        using var stream = typeof(BrowserForm).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? FindTool(string engine)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(home, ".local", "bin"))
            .Append(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"))
            .Append(Path.Combine(local, "Microsoft", "WinGet", "Links"));
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
                || key.StartsWith("PLUCK_SESSION", StringComparison.OrdinalIgnoreCase)
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
        // A slash command such as /compact goes to the CLI exactly as typed, without context in front of it.
        var command = text.TrimStart().StartsWith('/');
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
        var cwd = chat.Cwd is { Length: > 0 } folder && Directory.Exists(folder) ? folder
            : Directory.Exists(AiWorkDir) ? AiWorkDir : workspace;
        // Claude keeps sessions per folder, so a session started elsewhere (including the old default) continues from its transcript.
        if (chat.Engine == "claude" && chat.Session != null && !string.Equals(chat.SessionCwd ?? workspace, cwd, StringComparison.OrdinalIgnoreCase))
        { chat.Session = null; chat.NeedsContext = true; }
        chat.SessionCwd = cwd;
        if (cwd == AiWorkDir) InstallSkills(AiWorkDir);
        if (chat.NeedsContext && !command) prompt = HandoffContext(chat, scripts) + prompt;
        var space = SpaceOf(chat);
        var browserSession = OpenSession(space, SessionTitle(chat), "panel");
        browserSession.Kind = "panel";
        if (chat.BrowserRuntime != browserRuntime && !command)
            prompt = "[Browser restarted or new session] Previous tab IDs and element references may be stale. Match the prior task to your session's current tabs by URL, then take a fresh snapshot. The visible tab may be unrelated: do not switch the task to it merely because it is active. If the task tab is missing, reopen its known URL from the conversation and inspect the site's saved draft before repeating edits. Unsaved page memory may be lost. Your session's tabs: "
                + JsonSerializer.Serialize(State(space)) + "\n\n" + prompt;
        if (images.Length > 0 && !command)
            prompt += "\n\n[Attached images, in order]\n" + string.Join("\n", images.Select(image => image.Name + ": " + ImagePath(image)));
        PostAssistant(chat, new { kind = "user", text, images = attachments });

        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        IsolateAssistantEnvironment(info);
        if (connectionFile != null) info.Environment["PLUCK_NATIVE_CONNECTION"] = connectionFile;
        info.Environment["PLUCK_SESSION"] = space;
        info.Environment["PLUCK_SESSION_LABEL"] = browserSession.Label;
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
                chat.Session = data["id"]!.GetValue<string>();
                SaveAssistantHistory();
                return;
            case "done":
                run.Finished = true;
                if (data["error"] == null && chat.Session != null) chat.NeedsContext = false;
                break;
        }
        PostAssistant(chat, data);
        if (data["kind"]?.GetValue<string>() == "done") PostAssistantStatus();
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
            PostAssistant(run.Chat, code == 0
                ? new JsonObject { ["kind"] = "done" }
                : new JsonObject { ["kind"] = "done", ["error"] = string.IsNullOrWhiteSpace(tail) ? $"종료 코드 {code}" : tail });
        }
        PostAssistantStatus();
        run.Process.Dispose();
        if (!run.Chat.Interrupted) RunNextPrompt(run.Chat);
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
        _ = Task.Run(() =>
        {
            if (Write(first)) return;
            Emit(run, new JsonObject { ["kind"] = "error", ["text"] = "메시지를 Claude에 전달하지 못했습니다." });
            Kill(process);
        });
        var reader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (message == null) continue;
                if (message["type"]?.ToString() == "user" && message["isReplay"]?.GetValue<bool>() == true) { lock (gate) replayed++; continue; }
                foreach (var item in ParseClaude(message))
                {
                    if (item["kind"]?.ToString() == "done")
                    {
                        // A message sent mid-run that Claude has not taken in yet starts another turn in this process.
                        lock (gate)
                        {
                            if (replayed < written) continue;
                            closed = true;
                            try { process.StandardInput.Close(); } catch (IOException) { }
                        }
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

    private JsonObject CodexSandbox(Conversation chat, string scripts) => chat.Mode switch
    {
        "full" => new JsonObject { ["type"] = "dangerFullAccess" },
        "readonly" => new JsonObject { ["type"] = "readOnly", ["networkAccess"] = true },
        // agent.mjs talks to the browser over localhost, so even the restricted mode needs network access.
        _ => new JsonObject
        {
            ["type"] = "workspaceWrite", ["writableRoots"] = new JsonArray(scripts), ["networkAccess"] = true,
            ["excludeTmpdirEnvVar"] = false, ["excludeSlashTmp"] = false
        }
    };

    private bool StartCodex(Conversation chat, ProcessStartInfo info, string prompt, AttachedImage[] images, string instructions, string scripts)
    {
        info.ArgumentList.Add("app-server");
        if (Launch(chat, info) is not { } run) return false;
        var process = run.Process;
        var gate = new object();
        var nextId = 0;
        var replies = new Dictionary<int, TaskCompletionSource<JsonNode?>>();
        string? threadId = null, turnId = null;
        var sandbox = CodexSandbox(chat, scripts);
        var (model, effort, session) = (chat.Model, chat.Effort, chat.Session);

        void Send(JsonObject message)
        {
            lock (gate)
            {
                try { process.StandardInput.WriteLine(message.ToJsonString()); process.StandardInput.Flush(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
            }
        }
        Task<JsonNode?> Request(string method, JsonObject parameters)
        {
            var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (gate) { id = ++nextId; replies[id] = reply; }
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            return reply.Task;
        }
        static string? Failure(JsonNode? reply) => reply?["error"]?["message"]?.ToString() ?? (reply?["error"] != null ? reply["error"]!.ToJsonString() : null);

        run.Steer = (text, attached) =>
        {
            if (threadId == null || turnId == null || run.Finished) return false;
            _ = Request("turn/steer", new JsonObject
            {
                ["threadId"] = threadId, ["expectedTurnId"] = turnId, ["input"] = CodexInput(text, attached, ImagePath)
            }).ContinueWith(task =>
            {
                if (Failure(task.Result) is { } error) Emit(run, new JsonObject { ["kind"] = "note", ["text"] = "추가 지시를 전달하지 못했습니다: " + Clip(error, 300) });
            }, TaskScheduler.Default);
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
                if (message["id"] is { } id && method == null)
                {
                    TaskCompletionSource<JsonNode?>? reply;
                    lock (gate) replies.Remove(id.GetValue<int>(), out reply);
                    reply?.TrySetResult(message);
                    continue;
                }
                if (message["id"] is { } requestId)
                {
                    // Approval requests should not arrive with approvalPolicy "never"; decline anything that does.
                    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "pluck does not answer " + method } });
                    continue;
                }
                var parameters = message["params"] as JsonObject ?? new JsonObject();
                if (method == "turn/started") turnId = parameters["turn"]?["id"]?.ToString() ?? turnId;
                foreach (var item in ParseCodex(method, parameters))
                {
                    Emit(run, item);
                    if (item["kind"]?.ToString() == "done") Kill(process);
                }
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                if (Failure(await Request("initialize", new JsonObject
                    { ["clientInfo"] = new JsonObject { ["name"] = "pluck", ["title"] = "pluck", ["version"] = "1" }, ["capabilities"] = null })) is { } initError)
                    throw new InvalidOperationException(initError);
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" });
                var thread = new JsonObject { ["cwd"] = info.WorkingDirectory, ["approvalPolicy"] = "never", ["developerInstructions"] = instructions };
                if (model is { Length: > 0 }) thread["model"] = model;
                JsonNode? opened = null;
                if (session != null)
                {
                    var resume = (JsonObject)thread.DeepClone();
                    resume["threadId"] = session;
                    opened = await Request("thread/resume", resume);
                    if (Failure(opened) != null) opened = null;
                }
                opened ??= await Request("thread/start", thread);
                if (Failure(opened) is { } threadError) throw new InvalidOperationException(threadError);
                threadId = opened!["result"]?["thread"]?["id"]?.ToString() ?? throw new InvalidOperationException("Codex did not return a thread");
                Emit(run, new JsonObject { ["kind"] = "session", ["id"] = threadId });
                if (prompt.Trim() == "/compact")
                {
                    if (Failure(await Request("thread/compact/start", new JsonObject { ["threadId"] = threadId })) is { } compactError)
                        throw new InvalidOperationException(compactError);
                    return;
                }
                var turn = new JsonObject
                {
                    ["threadId"] = threadId, ["input"] = CodexInput(prompt, images, ImagePath), ["sandboxPolicy"] = sandbox, ["summary"] = "auto"
                };
                if (model is { Length: > 0 }) turn["model"] = model;
                if (effort is { Length: > 0 }) turn["effort"] = effort;
                var started = await Request("turn/start", turn);
                if (Failure(started) is { } turnError) throw new InvalidOperationException(turnError);
                turnId ??= started!["result"]?["turn"]?["id"]?.ToString();
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                Emit(run, new JsonObject { ["kind"] = "done", ["error"] = "Codex를 시작하지 못했습니다: " + Clip(ex.Message, 600) });
                Kill(process);
            }
        });
        WatchExit(run, reader);
        return true;
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
                    case "agentMessage":
                        yield return new JsonObject { ["kind"] = "text", ["text"] = item!["text"]?.ToString() };
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
            case "thread/compacted":
                yield return new JsonObject { ["kind"] = "done" };
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
