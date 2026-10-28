using System.Security.Cryptography;
using System.Text.Json;

namespace OinziBrowser;

// Scheduled AI tasks: a prompt that runs daily, weekly or every few hours in a new conversation of its own.
// Runs only while Oinzi is open; a run missed while it was closed happens once at the next start.
public sealed partial class BrowserForm
{
    private sealed class Schedule
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string Engine { get; set; } = "claude";
        // daily | weekly | interval
        public string Kind { get; set; } = "daily";
        public string Time { get; set; } = "09:00";
        public int[] Days { get; set; } = [1, 2, 3, 4, 5];
        public int IntervalHours { get; set; } = 6;
        public bool Enabled { get; set; } = true;
        public bool Record { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime? LastRun { get; set; }
        public List<ScheduleRun> History { get; set; } = [];
    }

    private sealed class ScheduleRun
    {
        public DateTime At { get; set; }
        public string Chat { get; set; } = "";
        public string Status { get; set; } = "running";
        public string Error { get; set; } = "";
    }

    private List<Schedule> schedules = [];
    // Conversation id → schedule it was started for, while the run is going.
    private readonly Dictionary<string, Schedule> scheduleRuns = [];
    private readonly System.Windows.Forms.Timer scheduleTimer = new() { Interval = 30000 };
    private string SchedulesFile => Path.Combine(dataDir, "schedules.json");

    private void LoadSchedules()
    {
        try { if (File.Exists(SchedulesFile)) schedules = JsonSerializer.Deserialize<List<Schedule>>(File.ReadAllText(SchedulesFile)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { schedules = []; }
        // A run that was going when Oinzi closed is reported as interrupted rather than running forever.
        foreach (var run in schedules.SelectMany(s => s.History).Where(run => run.Status == "running")) { run.Status = "error"; run.Error = "Oinzi가 종료되어 중단됐습니다."; }
        scheduleTimer.Tick += (_, _) => RunDueSchedules();
        scheduleTimer.Start();
        FormClosed += (_, _) => scheduleTimer.Dispose();
        // Give the conversation history a moment to load before catching up on missed runs.
        BeginInvoke(async () => { await Task.Delay(5000); RunDueSchedules(); });
    }

    private void SaveSchedules()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(SchedulesFile + ".tmp", JsonSerializer.Serialize(schedules));
            File.Move(SchedulesFile + ".tmp", SchedulesFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // The first due time after `from`.
    private static DateTime NextRun(Schedule schedule, DateTime from)
    {
        if (schedule.Kind == "interval") return from.AddHours(Math.Clamp(schedule.IntervalHours, 1, 168));
        var time = TimeSpan.TryParse(schedule.Time, out var parsed) ? parsed : new TimeSpan(9, 0, 0);
        for (var day = 0; day <= 8; day++)
        {
            var at = from.Date.AddDays(day) + time;
            if (at <= from) continue;
            if (schedule.Kind == "weekly" && !schedule.Days.Contains((int)at.DayOfWeek)) continue;
            return at;
        }
        return DateTime.MaxValue;
    }

    private DateTime DueAt(Schedule schedule) => NextRun(schedule, schedule.LastRun ?? schedule.Created);

    private void RunDueSchedules()
    {
        if (!historyLoaded || IsDisposed) return;
        foreach (var schedule in schedules.Where(s => s.Enabled && DueAt(s) <= DateTime.Now).ToArray()) RunSchedule(schedule);
    }

    private void RunSchedule(Schedule schedule)
    {
        // The previous run is still going; this one is skipped rather than piled up.
        if (scheduleRuns.ContainsValue(schedule)) { schedule.LastRun = DateTime.Now; SaveSchedules(); return; }
        schedule.LastRun = DateTime.Now;
        if (!Engines.Contains(schedule.Engine) || schedule.Prompt.Trim().Length == 0) { SaveSchedules(); return; }
        // A conversation of its own that is not selected, so the panel stays on whatever the person is reading.
        var chat = new Conversation { Engine = schedule.Engine, Title = $"[예약] {schedule.Name} · {DateTime.Now:MM/dd HH:mm}" };
        conversations.Add(chat);
        scheduleRuns[chat.Id] = schedule;
        schedule.History.Add(new ScheduleRun { At = DateTime.Now, Chat = chat.Id });
        if (schedule.History.Count > 20) schedule.History.RemoveRange(0, schedule.History.Count - 20);
        SaveSchedules();
        if (schedule.Record)
        {
            OpenSession(SpaceOf(chat), SessionTitle(chat), "panel").Kind = "panel";
            if (!IsRecording(SpaceOf(chat))) ToggleRecording(SpaceOf(chat));
        }
        StartAssistant(chat, schedule.Prompt, "[Oinzi] 예약 작업으로 자동 실행되었습니다. 사용자가 지켜보고 있지 않으니 결제·게시·전송처럼 되돌릴 수 없는 일은 하지 말고, 결과를 정리해 답하세요.\n\n", []);
        if (!aiRuns.ContainsKey(chat.Id)) FinishScheduleRun(chat, "CLI를 시작하지 못했습니다.");
        PostConversationList(schedule.Engine);
        PostSchedules();
    }

    // Called when a scheduled conversation's run ends.
    private void FinishScheduleRun(Conversation chat, string? error)
    {
        if (!scheduleRuns.Remove(chat.Id, out var schedule)) return;
        if (schedule.History.LastOrDefault(run => run.Chat == chat.Id) is { } run) { run.Status = error == null ? "ok" : "error"; run.Error = Clip(error ?? "", 200); }
        if (IsRecording(SpaceOf(chat))) ToggleRecording(SpaceOf(chat));
        SaveSchedules();
        PostSchedules();
        ShowNotification(error == null ? $"예약 작업 '{schedule.Name}' 완료" : $"예약 작업 '{schedule.Name}' 실패", error ?? chat.Title, chat);
    }

    private object SchedulesMessage() => new
    {
        type = "schedules", startup = StartsWithWindows(),
        items = schedules.Select(s => new
        {
            id = s.Id, name = s.Name, prompt = s.Prompt, engine = s.Engine, kind = s.Kind, time = s.Time, days = s.Days,
            intervalHours = s.IntervalHours, enabled = s.Enabled, record = s.Record, running = scheduleRuns.ContainsValue(s),
            next = s.Enabled ? DueAt(s) : (DateTime?)null,
            history = s.History.AsEnumerable().Reverse().Take(5).Select(run => new { at = run.At, chat = run.Chat, status = run.Status, error = run.Error }).ToArray()
        }).ToArray()
    };

    private void PostSchedules() => PostChrome(SchedulesMessage());

    private void HandleScheduleMessage(string type, JsonElement message)
    {
        var id = Read(message, "id");
        var schedule = schedules.FirstOrDefault(s => s.Id == id);
        switch (type)
        {
            case "sched.list": break;
            case "sched.save":
                var name = Clip(Read(message, "name").Trim(), 60);
                var prompt = Read(message, "prompt").Trim();
                if (name.Length == 0 || prompt.Length == 0) { PostChrome(new { type = "settings.note", text = "이름과 요청 내용을 입력하세요." }); return; }
                if (schedule == null)
                {
                    schedule = new Schedule { Id = "s" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), Created = DateTime.Now };
                    schedules.Add(schedule);
                }
                schedule.Name = name;
                schedule.Prompt = prompt.Length > 8000 ? prompt[..8000] : prompt;
                schedule.Engine = Engines.Contains(Read(message, "engine")) ? Read(message, "engine") : "claude";
                schedule.Kind = Read(message, "kind") is "daily" or "weekly" or "interval" ? Read(message, "kind") : "daily";
                schedule.Time = TimeSpan.TryParse(Read(message, "time"), out var time) ? time.ToString(@"hh\:mm") : "09:00";
                schedule.Days = Read(message, "days").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(day => int.TryParse(day, out var d) ? d : -1).Where(d => d is >= 0 and <= 6).Distinct().ToArray();
                if (schedule.Kind == "weekly" && schedule.Days.Length == 0) schedule.Days = [1];
                schedule.IntervalHours = int.TryParse(Read(message, "intervalHours"), out var hours) ? Math.Clamp(hours, 1, 168) : 6;
                schedule.Record = Read(message, "record") == "True";
                // Editing starts the timetable afresh, so a changed time does not fire at once for a "missed" slot.
                schedule.LastRun = DateTime.Now;
                break;
            case "sched.toggle" when schedule != null:
                schedule.Enabled = Read(message, "enabled") == "True";
                if (schedule.Enabled) schedule.LastRun = DateTime.Now;
                break;
            case "sched.run" when schedule != null: RunSchedule(schedule); return;
            case "sched.remove" when schedule != null: schedules.Remove(schedule); break;
            case "sched.open" when conversations.FirstOrDefault(c => c.Id == Read(message, "chat")) is { } chat:
                PostChrome(new { type = "settings.close" });
                RevealConversation(chat);
                return;
            case "sched.startup": SetStartsWithWindows(Read(message, "enabled") == "True"); break;
            default: return;
        }
        SaveSchedules();
        PostSchedules();
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static bool StartsWithWindows()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Oinzi") is string;
    }

    private static void SetStartsWithWindows(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("Oinzi", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("Oinzi", false);
    }
}
