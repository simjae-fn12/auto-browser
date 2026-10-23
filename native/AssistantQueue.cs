namespace PluckWindowsNative;

public sealed partial class BrowserForm
{
    private sealed record QueuedPrompt(string Id, string Text, string Context, AttachedImage[] Images);

    private void PostQueue(string engine) => PostChrome(new
    {
        type = "ai.queue", engine,
        items = currentConversations[engine].Pending.Select(item => new { id = item.Id, text = item.Text, images = item.Images.Length }).ToArray()
    });

    // A message for a busy conversation joins the turn in progress, as in the terminal; it waits in the queue only
    // when the CLI can no longer take it (for example while the run is finishing).
    private void SubmitAssistant(string engine, string text, bool withContext)
    {
        if (!Engines.Contains(engine) || (string.IsNullOrWhiteSpace(text) && pendingImages[engine].Count == 0)) return;
        LoadAssistantHistory();
        var chat = currentConversations[engine];
        var context = withContext && Active() is { } tab
            ? $"[pluck] 사용자가 보고 있는 탭: \"{tab.View.CoreWebView2?.DocumentTitle}\" — {tab.View.Source} (tab id {tab.Id})\n\n" : "";
        var images = pendingImages[engine].ToArray();
        pendingImages[engine].Clear();
        PostImages(engine);
        if (aiRuns.TryGetValue(chat.Id, out var run))
        {
            var steered = false;
            try { steered = run.Steer(context + (string.IsNullOrWhiteSpace(text) ? "첨부한 사진을 살펴봐 주세요." : text.Trim()), images); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (steered)
            {
                PostAssistant(chat, new { kind = "user", text, images, steer = true });
                return;
            }
        }
        chat.Pending.Add(new(Guid.NewGuid().ToString("N"), text, context, images));
        FlushAssistantHistory();
        PostQueue(engine);
        RunNextPrompt(chat);
    }

    private void RemoveQueuedPrompt(string engine, string id)
    {
        if (!currentConversations.TryGetValue(engine, out var chat)) return;
        chat.Pending.RemoveAll(item => item.Id == id);
        SaveAssistantHistory();
        PostQueue(engine);
    }

    private void RunNextPrompt(Conversation chat)
    {
        if (aiRuns.ContainsKey(chat.Id) || chat.Pending.Count == 0) return;
        var next = chat.Pending[0];
        // Remove before starting so the durable run checkpoint cannot replay this request twice.
        chat.Pending.RemoveAt(0);
        StartAssistant(chat, next.Text, next.Context, next.Images);
        if (!aiRuns.ContainsKey(chat.Id)) chat.Pending.Insert(0, next);
        SaveAssistantHistory();
        if (currentConversations.GetValueOrDefault(chat.Engine) == chat) PostQueue(chat.Engine);
    }
}
