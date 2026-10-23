using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PluckWindowsNative;

// The AI panel runs the person's own Claude Code or Codex CLI headlessly, so their subscriptions are used as-is.
// Each engine drives the browser through agent.mjs in a private workspace folder and streams JSON events back.
public sealed partial class BrowserForm
{
    private static readonly string[] Engines = ["claude", "codex"];
    private readonly Dictionary<string, Process> aiRuns = [];
    private readonly Dictionary<string, string> aiSessions = [];
    private readonly string browserRuntime = Guid.NewGuid().ToString("N");
    private string AssistantDir => Path.Combine(dataDir, "assistant");

    private string PrepareAssistantWorkspace(string engine)
    {
        var workspace = Path.Combine(AssistantDir, engine);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "agent.mjs"), ReadResource("agent.mjs"));
        var instructions = ReadResource("assistant.md");
        File.WriteAllText(Path.Combine(workspace, "CLAUDE.md"), instructions);
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), instructions);
        return workspace;
    }

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
            .Append(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"));
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
            running = aiRuns.ContainsKey(engine)
        })
    });

    private void PostAssistant(string engine, object data)
    {
        var entry = JsonSerializer.SerializeToNode(data);
        if (entry?["kind"]?.ToString() == "done" && currentConversations.TryGetValue(engine, out var chat))
        {
            chat.Running = false;
            chat.Interrupted = entry["error"] != null;
            PostChrome(new { type = "ai.recovery", engine, interrupted = chat.Interrupted });
        }
        RecordAssistant(engine, data);
        PostChrome(new { type = "ai", engine, @event = data });
    }

    private static void IsolateAssistantEnvironment(ProcessStartInfo info)
    {
        // A headless child must not inherit another terminal/agent's IPC or session identity.
        foreach (var key in info.Environment.Keys.ToArray())
            if (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) && key != "CODEX_HOME"
                || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("ORCA_", StringComparison.OrdinalIgnoreCase)
                || key is "CLAUDECODE" or "WT_SESSION" or "WT_PROFILE_ID" or "TERM_SESSION_ID" or "TERM_PROGRAM" or "TERM_PROGRAM_VERSION")
                info.Environment.Remove(key);
        info.Environment["TERM"] = "dumb";
        info.Environment["NO_COLOR"] = "1";
    }

    private void StartAssistant(string engine, string text, bool withContext, string submittedContext = "")
    {
        if (!Engines.Contains(engine) || aiRuns.ContainsKey(engine) || (string.IsNullOrWhiteSpace(text) && pendingImages[engine].Count == 0)) return;
        LoadAssistantHistory();
        var conversation = currentConversations[engine];
        var exe = FindTool(engine);
        if (exe == null)
        {
            PostAssistant(engine, new { kind = "error", text = engine == "claude"
                ? "Claude Code CLI(claude.exe)를 찾을 수 없습니다. 설치 후 `claude`로 로그인하세요."
                : "Codex CLI(codex.exe)를 찾을 수 없습니다. 설치 후 `codex login`으로 로그인하세요." });
            PostAssistantStatus();
            return;
        }
        var prompt = string.IsNullOrWhiteSpace(text) ? "첨부한 사진을 살펴봐 주세요." : text.Trim();
        prompt = submittedContext + prompt;
        if (withContext && Active() is { } tab && tab.View.Source is { } source)
            prompt = $"[pluck] 사용자가 보고 있는 탭: \"{tab.View.CoreWebView2?.DocumentTitle}\" — {source} (tab id {tab.Id})\n\n{prompt}";

        var workspace = PrepareAssistantWorkspace(engine);
        var attachments = pendingImages[engine].ToArray();
        var images = (conversation.NeedsContext ? HandoffImages(conversation) : [])
            .Concat(attachments).DistinctBy(image => image.Id).TakeLast(6).ToArray();
        if (images.Any(image => !File.Exists(ImagePath(image))))
        {
            PostAssistant(engine, new { kind = "error", text = "첨부한 사진 파일을 찾을 수 없습니다. 다시 첨부해 주세요." });
            PostAssistantStatus();
            return;
        }
        if (conversation.NeedsContext) prompt = HandoffContext(conversation, workspace) + prompt;
        var space = SpaceOf(conversation);
        var browserSession = OpenSession(space, SessionTitle(conversation), "panel");
        browserSession.Kind = "panel";
        if (conversation.BrowserRuntime != browserRuntime)
            prompt = "[Browser restarted or new session] Previous tab IDs and element references may be stale. Match the prior task to your session's current tabs by URL, then take a fresh snapshot. The visible tab may be unrelated: do not switch the task to it merely because it is active. If the task tab is missing, reopen its known URL from the conversation and inspect the site's saved draft before repeating edits. Unsaved page memory may be lost. Your session's tabs: "
                + JsonSerializer.Serialize(State(space)) + "\n\n" + prompt;
        if (images.Length > 0)
            prompt += "\n\n[Attached images, in order]\n" + string.Join("\n", images.Select(image => image.Name + ": " + ImagePath(image)));
        PostAssistant(engine, new { kind = "user", text, images = attachments });
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workspace,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        IsolateAssistantEnvironment(info);
        if (connectionFile != null) info.Environment["PLUCK_NATIVE_CONNECTION"] = connectionFile;
        info.Environment["PLUCK_SESSION"] = space;
        info.Environment["PLUCK_SESSION_LABEL"] = browserSession.Label;
        var session = aiSessions.GetValueOrDefault(engine);
        if (engine == "claude")
        {
            foreach (var arg in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--permission-mode", "acceptEdits", "--allowedTools", "Bash(node:*),PowerShell(node *),Read,WebSearch,WebFetch" })
                info.ArgumentList.Add(arg);
            if (session != null) { info.ArgumentList.Add("--resume"); info.ArgumentList.Add(session); }
        }
        else
        {
            info.ArgumentList.Add("exec");
            if (session != null) { info.ArgumentList.Add("resume"); info.ArgumentList.Add(session); }
            foreach (var arg in new[] { "--json", "--skip-git-repo-check",
                "-c", "sandbox_mode=\"workspace-write\"", "-c", "sandbox_workspace_write.network_access=true" })
                info.ArgumentList.Add(arg);
            foreach (var image in images) { info.ArgumentList.Add("--image"); info.ArgumentList.Add(ImagePath(image)); }
            info.ArgumentList.Add("--");
            info.ArgumentList.Add("-");
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PostAssistant(engine, new { kind = "error", text = $"{engine} 실행 실패: {ex.Message}" });
            process.Dispose();
            PostAssistantStatus();
            return;
        }
        aiRuns[engine] = process;
        conversation.Running = true;
        conversation.Interrupted = false;
        conversation.BrowserRuntime = browserRuntime;
        PostChrome(new { type = "ai.recovery", engine, interrupted = false });
        FlushAssistantHistory();
        pendingImages[engine].Clear();
        PostImages(engine);
        PostAssistantStatus();

        var errors = new Queue<string>();
        var finished = false;
        // A stopped run is no longer in aiRuns; anything it prints afterwards is dropped.
        bool Current() => aiRuns.GetValueOrDefault(engine) == process;
        void Emit(JsonObject data) => BeginInvoke(() =>
        {
            if (!Current()) return;
            if (data["kind"]?.GetValue<string>() == "done")
            {
                finished = true;
                if (data["error"] == null && conversation.Session != null) conversation.NeedsContext = false;
            }
            PostAssistant(engine, data);
        });
        var input = Task.Run(async () =>
        {
            try
            {
                await process.StandardInput.WriteLineAsync(engine == "claude" ? ClaudeInput(prompt, images) : prompt);
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
            {
                Emit(new JsonObject { ["kind"] = "error", ["text"] = "메시지 또는 사진을 전달하지 못했습니다: " + ex.Message });
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        });
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                lock (errors) { errors.Enqueue(line); if (errors.Count > 12) errors.Dequeue(); }
        });
        _ = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                if (node is not JsonObject message) continue;
                var parsed = engine == "claude" ? ParseClaude(message) : ParseCodex(message);
                foreach (var item in parsed)
                {
                    if (item["kind"]?.GetValue<string>() == "session")
                    {
                        var id = item["id"]!.GetValue<string>();
                        BeginInvoke(() =>
                        {
                            if (!Current()) return;
                            aiSessions[engine] = id;
                            conversation.Session = id;
                            SaveAssistantHistory();
                        });
                        continue;
                    }
                    Emit(item);
                }
            }
            await process.WaitForExitAsync();
            await stderr;
            await input;
            BeginInvoke(() =>
            {
                if (!Current()) { process.Dispose(); return; }
                aiRuns.Remove(engine);
                if (!finished)
                {
                    string tail;
                    lock (errors) tail = string.Join("\n", errors.Where(line => !line.Contains(" ERROR rmcp") && !line.Contains("codex_rmcp_client")).TakeLast(6));
                    PostAssistant(engine, process.ExitCode == 0
                        ? new JsonObject { ["kind"] = "done" }
                        : new JsonObject { ["kind"] = "done", ["error"] = string.IsNullOrWhiteSpace(tail) ? $"종료 코드 {process.ExitCode}" : tail });
                }
                PostAssistantStatus();
                process.Dispose();
                if (!conversation.Interrupted) RunNextPrompt(engine);
            });
        });
    }

    private void StopAssistant(string engine)
    {
        if (!aiRuns.TryGetValue(engine, out var process)) return;
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        // Removing the run first makes its exit handler and late output no-ops.
        aiRuns.Remove(engine);
        PostAssistant(engine, new JsonObject { ["kind"] = "done", ["error"] = "중지했습니다." });
        PostAssistantStatus();
    }

    private void StopAllAssistants()
    {
        foreach (var engine in aiRuns.Keys)
            if (currentConversations.TryGetValue(engine, out var chat)) { chat.Running = false; chat.Interrupted = true; }
        foreach (var process in aiRuns.Values.ToArray())
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
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

    private static IEnumerable<JsonObject> ParseClaude(JsonObject message)
    {
        switch (message["type"]?.ToString())
        {
            case "system" when message["subtype"]?.ToString() == "init" && message["session_id"] is { } session:
                yield return new JsonObject { ["kind"] = "session", ["id"] = session.ToString() };
                break;
            case "stream_event":
                var streamed = message["event"];
                if (streamed?["type"]?.ToString() == "content_block_delta" && streamed["delta"]?["type"]?.ToString() == "text_delta")
                    yield return new JsonObject { ["kind"] = "delta", ["text"] = streamed["delta"]!["text"]?.ToString() };
                break;
            case "assistant":
                foreach (var block in message["message"]?["content"]?.AsArray() ?? [])
                {
                    if (block?["type"]?.ToString() == "text")
                        yield return new JsonObject { ["kind"] = "text", ["text"] = block["text"]?.ToString() };
                    else if (block?["type"]?.ToString() == "tool_use")
                    {
                        var input = block["input"];
                        var detail = input?["command"] ?? input?["file_path"] ?? input?["url"] ?? input?["query"] ?? input?["pattern"];
                        yield return new JsonObject
                        {
                            ["kind"] = "tool", ["id"] = block["id"]?.ToString(), ["name"] = block["name"]?.ToString(),
                            ["detail"] = Clip(detail?.ToString(), 600), ["body"] = Clip(input?["content"]?.ToString(), 4000)
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
                yield return message["is_error"]?.GetValue<bool>() == true
                    ? new JsonObject { ["kind"] = "done", ["error"] = Clip(message["result"]?.ToString() ?? message["subtype"]?.ToString(), 1000) }
                    : new JsonObject { ["kind"] = "done" };
                break;
        }
    }

    private static readonly Regex PowerShellWrapper = new("^\"?[^\"]*powershell(\\.exe)?\"?\\s+-Command\\s+(?<body>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static IEnumerable<JsonObject> ParseCodex(JsonObject message)
    {
        var item = message["item"];
        var kind = item?["type"]?.ToString();
        switch (message["type"]?.ToString())
        {
            case "thread.started":
                yield return new JsonObject { ["kind"] = "session", ["id"] = message["thread_id"]?.ToString() };
                break;
            case "item.started" when kind == "command_execution":
                var command = item!["command"]?.ToString() ?? "";
                var match = PowerShellWrapper.Match(command);
                if (match.Success) command = match.Groups["body"].Value.Trim().Trim('\'', '"');
                yield return new JsonObject { ["kind"] = "tool", ["id"] = item["id"]?.ToString(), ["name"] = "Shell", ["detail"] = Clip(command, 600) };
                break;
            case "item.completed":
                switch (kind)
                {
                    case "agent_message":
                        yield return new JsonObject { ["kind"] = "text", ["text"] = item!["text"]?.ToString() };
                        break;
                    case "reasoning":
                        yield return new JsonObject { ["kind"] = "thinking", ["text"] = item!["text"]?.ToString() };
                        break;
                    case "command_execution":
                        yield return new JsonObject
                        {
                            ["kind"] = "toolDone", ["id"] = item!["id"]?.ToString(),
                            ["ok"] = item["exit_code"]?.GetValue<int>() == 0, ["output"] = Clip(item["aggregated_output"]?.ToString(), 4000)
                        };
                        break;
                    case "file_change":
                        var paths = string.Join(", ", item!["changes"]?.AsArray().Select(change => Path.GetFileName(change?["path"]?.ToString() ?? "")) ?? []);
                        yield return new JsonObject { ["kind"] = "tool", ["id"] = item["id"]?.ToString(), ["name"] = "Write", ["detail"] = paths };
                        yield return new JsonObject { ["kind"] = "toolDone", ["id"] = item["id"]?.ToString(), ["ok"] = item["status"]?.ToString() != "failed" };
                        break;
                    case "web_search":
                        yield return new JsonObject { ["kind"] = "tool", ["id"] = item!["id"]?.ToString(), ["name"] = "WebSearch", ["detail"] = item["query"]?.ToString() };
                        yield return new JsonObject { ["kind"] = "toolDone", ["id"] = item["id"]?.ToString(), ["ok"] = true };
                        break;
                    case "error" when item!["message"]?.ToString() is { } error && !error.StartsWith("Codex is ignoring", StringComparison.Ordinal):
                        yield return new JsonObject { ["kind"] = "note", ["text"] = Clip(error, 600) };
                        break;
                }
                break;
            case "turn.completed":
                yield return new JsonObject { ["kind"] = "done" };
                break;
            case "turn.failed":
                yield return new JsonObject { ["kind"] = "done", ["error"] = Clip(message["error"]?["message"]?.ToString(), 1000) };
                break;
            case "error":
                yield return new JsonObject { ["kind"] = "note", ["text"] = Clip(message["message"]?.ToString(), 600) };
                break;
        }
    }
}
