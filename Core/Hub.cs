using System.Text.Json;
using System.Text.RegularExpressions;
using DoomGuy.Doom;

namespace DoomGuy.Core;

public sealed class HudMessage
{
    public string Text = "";
    public Ink Ink;
    public int Tics;
}

/// <summary>A finished task or a notification, announced by the head in face-only mode until clicked away.</summary>
public sealed class Announcement
{
    public string Headline = "";
    public string Line2 = "";
    public string Line3 = "";
    public Ink Ink = Ink.Gold;
}

/// <summary>
/// Everything DoomGuy knows: tasks, reminders, the log, and the marine's health and face. Lives on the UI
/// thread; the API server and the windows only ever talk to it there. Ticks at Doom's 35 Hz.
/// </summary>
public sealed class Hub
{
    public const int TicRate = 35;
    private const int MessageTics = 4 * TicRate;

    public Settings Settings { get; }
    public SavedState State { get; }
    public List<TaskItem> Tasks => State.Tasks;
    public List<Reminder> Reminders => State.Reminders;
    public List<LogLine> Log => State.Log;
    public readonly List<HudMessage> Hud = [];
    /// <summary>Announcements waiting their turn in the head's bubble (only drained in face-only mode).</summary>
    public readonly List<Announcement> Announcements = [];
    public Sound? Sound;
    public long Tic;
    private long _lastSoundTic;
    /// <summary>Health when its last sound played, and how far it must move before the next.</summary>
    private int _soundHealth = 100;
    private const int HealthSoundStep = 5;

    /// <summary>A reminder just went off (the alert window shows it).</summary>
    public event Action? ReminderFired;

    // Face and flash state, after st_stuff.c.
    public int FaceIndex, DamageCount, BonusCount;
    private int _faceCount, _facePriority, _oldHealth = 100, _hurt, _rampageTics;
    private bool _grin;
    private readonly Queue<DateTime> _toolTimes = new();
    private readonly Random _rng = new();
    private bool _dirty;
    private DateTime _lastSave = DateTime.Now;

    public Hub(Settings settings, SavedState state)
    {
        Settings = settings;
        State = state;
        Gauges = new Gauges(this);
        RollDay();
        foreach (var r in Reminders) r.NextNag = DateTime.MinValue;
        _oldHealth = _soundHealth = Health;
    }

    // ---------------------------------------------------------------- derived numbers

    public int NeedsYouCount => Tasks.Count(t => t.State == TaskState.NeedsYou);
    public int WorkingCount => Tasks.Count(t => t.State == TaskState.Working);
    public int FiringCount => Reminders.Count(r => r.Firing);
    public int UnseenDoneCount => Tasks.Count(t => t.Unseen && t.State == TaskState.Done);
    public int FailedUnseenCount => Tasks.Count(t => t.Unseen && t.State == TaskState.Failed);

    /// <summary>100% when nothing needs you; every blocked session, due reminder and unread failure costs health.</summary>
    public int Attention => Math.Clamp(100 - 25 * NeedsYouCount - 20 * FiringCount - 10 * FailedUnseenCount, 0, 100);

    /// <summary>What the bar's big numbers are wired to (see Gauges and the HUD tab).</summary>
    public Gauges Gauges { get; }

    /// <summary>The marine's health: whatever the active profile's HEALTH slot follows (attention by default).</summary>
    public int Health => Gauges.Value("health");
    public int Ammo => Gauges.Value("ammo");
    public int ArmorValue => Gauges.Value("armor");

    /// <summary>Which PLAYPAL to show: red while hurt, gold after a pickup (ST_doPaletteStuff).</summary>
    public int PaletteIndex
    {
        get
        {
            if (DamageCount > 0) return 1 + Math.Min((DamageCount + 7) >> 3, 7);
            if (BonusCount > 0) return 9 + Math.Min((BonusCount + 7) >> 3, 3);
            return 0;
        }
    }

    /// <summary>The armour number: progress of the most recently updated running job.</summary>
    public int Armor => Tasks.Where(t => !t.IsClaude && t.State == TaskState.Working && t.Percent >= 0)
        .OrderByDescending(t => t.UpdatedAt).Select(t => t.Percent).FirstOrDefault();

    // ---------------------------------------------------------------- the tic

    public void Tick()
    {
        Tic++;
        for (int i = Hud.Count - 1; i >= 0; i--)
            if (--Hud[i].Tics <= 0) Hud.RemoveAt(i);
        if (DamageCount > 0) DamageCount--;
        if (BonusCount > 0) BonusCount--;

        var now = DateTime.Now;
        while (_toolTimes.Count > 0 && (now - _toolTimes.Peek()).TotalSeconds > 2.5) _toolTimes.Dequeue();
        _rampageTics = _toolTimes.Count >= 2 && WorkingCount > 0 ? _rampageTics + 1 : 0;

        if (Tic % TicRate == 0) EverySecond(now);

        int h = Health;
        if (h < _oldHealth)
        {
            _hurt = Math.Max(_hurt, _oldHealth - h);
            DamageCount = Math.Min(100, DamageCount + _oldHealth - h);
            if (h <= 0) { Play("PLDETH"); _soundHealth = h; }
        }
        _oldHealth = h;
        // Sounds go by how far health has moved since the last one: a source that drifts a point at a time
        // (context, plan left) stays quiet until it adds up. Going up is a stimpack: the gold flash too.
        if (h > 0 && Math.Abs(h - _soundHealth) >= HealthSoundStep)
        {
            if (h > _soundHealth) BonusCount = Math.Min(BonusCount + 6, 30);
            HealthSound(h < _soundHealth ? "PLPAIN" : "ITEMUP");
            _soundHealth = h;
        }
        UpdateFace(h);
    }

    private void UpdateFace(int health)
    {
        int pain = Assets.FaceStride * ((100 - Math.Min(health, 100)) * 5 / 101);
        if (_facePriority < 10 && health <= 0)
        {
            _facePriority = 9; FaceIndex = Assets.Dead; _faceCount = 1; _grin = false;
        }
        if (_facePriority < 9 && _grin)
        {
            _grin = false; _facePriority = 8; _faceCount = 2 * TicRate; FaceIndex = pain + Assets.EvilGrin;
        }
        if (_facePriority < 8 && _hurt > 0)
        {
            _facePriority = 7; _faceCount = TicRate;
            FaceIndex = pain + (_hurt > 20 ? Assets.Ouch : _rng.Next(2) == 0 ? Assets.TurnLeft : Assets.TurnRight);
            _hurt = 0;
        }
        if (_facePriority < 6 && _rampageTics > 2 * TicRate)
        {
            _facePriority = 5; FaceIndex = pain + Assets.Rampage; _faceCount = 1;
        }
        if (_facePriority < 5 && Settings.God)
        {
            _facePriority = 4; FaceIndex = Assets.God; _faceCount = 1;
        }
        if (_faceCount == 0)
        {
            FaceIndex = pain + _rng.Next(3); _faceCount = TicRate / 2; _facePriority = 0;
        }
        _faceCount--;
    }

    private void EverySecond(DateTime now)
    {
        RollDay();
        foreach (var r in Reminders.ToList())
        {
            if (!r.Firing && now >= r.Due) Fire(r, now);
            else if (r.Firing && now >= r.NextNag)
            {
                r.NextNag = now.AddMinutes(3);
                Message($"REMINDER: {r.Text}", Ink.Yellow);
                Play("GETPOW");
                _hurt = Math.Max(_hurt, 1);
            }
        }

        foreach (var t in Tasks.ToList())
        {
            var quiet = now - t.UpdatedAt;
            if (t.State == TaskState.Working && quiet.TotalMinutes > (t.IsClaude ? 45 : 360))
            {
                t.State = TaskState.Idle; t.Detail = "NO SIGNAL"; Touch();
            }
            else if (t.State != TaskState.Working && quiet.TotalHours > (t.IsClaude ? 8 : 12))
            {
                Tasks.Remove(t); Touch();
            }
        }

        if (_dirty && (now - _lastSave).TotalSeconds > 5) Save();
    }

    private void RollDay()
    {
        if (State.Day == DateTime.Today) return;
        State.Day = DateTime.Today;
        State.DoneToday = State.StartedToday = State.MessagesToday = State.ErrorsToday = 0;
        Touch();
    }

    public void Save()
    {
        Store.Save("state.json", State);
        _dirty = false;
        _lastSave = DateTime.Now;
    }

    private void Touch() => _dirty = true;

    // ---------------------------------------------------------------- feedback

    public void Play(string sound)
    {
        _lastSoundTic = Tic;
        if (Settings.God) return;
        Sound?.Play(sound);
    }

    /// <summary>
    /// The sound for health going up or down. Skipped when something already made a sound in the last half
    /// second, so nothing doubles up: a failure plays its own pain sound and also costs health, a finished task
    /// plays its own pickup. Quiet for the first seconds while polled sources report in.
    /// </summary>
    private void HealthSound(string sound)
    {
        if (Tic < 3 * TicRate || Tic - _lastSoundTic < TicRate / 2) return;
        Play(sound);
    }

    public void Message(string text, Ink ink = Ink.Red, string level = "info", bool log = true)
    {
        text = Clean(text).ToUpperInvariant();
        Hud.Add(new HudMessage { Text = text, Ink = ink, Tics = MessageTics });
        if (Hud.Count > 6) Hud.RemoveAt(0);
        if (log) AddLog(text, level);
    }

    public void AddLog(string text, string level = "info")
    {
        Log.Add(new LogLine { Text = Clean(text).ToUpperInvariant(), Level = level });
        if (Log.Count > 400) Log.RemoveRange(0, Log.Count - 400);
        Touch();
    }

    private void Announce(string headline, string line2, string line3, Ink ink)
    {
        Announcements.Add(new Announcement { Headline = Clean(headline).ToUpperInvariant(), Line2 = Clean(line2).ToUpperInvariant(), Line3 = Clean(line3).ToUpperInvariant(), Ink = ink });
        if (Announcements.Count > 20) Announcements.RemoveAt(0);
    }

    private void Pickup(string text, string sound, Ink ink = Ink.Gold)
    {
        Message(text, ink, "success");
        Play(sound);
        _grin = true;
        BonusCount = Math.Min(BonusCount + 6, 30);
    }

    /// <summary>A small hit: the face glances aside and the screen flashes, but health is unchanged.</summary>
    private void Flinch(int flash)
    {
        _hurt = Math.Max(_hurt, 1);
        DamageCount = Math.Min(100, DamageCount + flash);
    }

    // ---------------------------------------------------------------- Claude Code hooks

    /// <summary>A Claude Code hook event. window = the terminal or editor it came from, when that could be found.</summary>
    public void HandleClaude(JsonElement p, IntPtr window = default)
    {
        if (p.ValueKind != JsonValueKind.Object) return;
        var ev = Str(p, "hook_event_name");
        var sid = Str(p, "session_id");
        if (sid.Length == 0 || ev.Length == 0) return;
        var now = DateTime.Now;
        var cwd = Str(p, "cwd");

        var t = Tasks.Find(x => x.IsClaude && x.Id == sid);
        if (t == null)
        {
            if (ev == "SessionEnd") return;
            t = new TaskItem { Id = sid, IsClaude = true, Cwd = cwd, Name = ProjectName(cwd, sid), State = TaskState.Idle, Title = "NEW SESSION" };
            Tasks.Add(t);
        }
        if (cwd.Length > 0 && t.Cwd != cwd) { t.Cwd = cwd; t.Name = ProjectName(cwd, sid); }
        if (window != IntPtr.Zero) t.Window = window;
        t.UpdatedAt = now;
        Touch();

        // Async hooks can land out of order: tool chatter that arrives just after the stop is stale.
        bool justFinished = t.FinishedAt != null && (now - t.FinishedAt.Value).TotalSeconds < 3
                            && t.State is TaskState.Done or TaskState.Failed;
        var title = Str(p, "session_title");

        switch (ev)
        {
            case "SessionStart":
            {
                if (title.Length > 0) t.Title = title;
                var src = Str(p, "source");
                if (src is "startup" or "resume" or "fork")
                    AddLog($"{t.Name}: SESSION {(src == "startup" ? "STARTED" : src == "resume" ? "RESUMED" : "FORKED")}");
                break;
            }
            case "UserPromptSubmit":
                t.State = TaskState.Working;
                t.StartedAt = now;
                t.FinishedAt = null;
                t.ToolCount = 0;
                t.Unseen = false;
                t.Title = PromptTitle(Str(p, "prompt"));
                t.Detail = "THINKING...";
                State.StartedToday++;
                break;

            case "PreToolUse":
                if (justFinished) break;
                if (t.State is TaskState.Idle or TaskState.Done or TaskState.Failed)
                {
                    t.State = TaskState.Working;
                    t.FinishedAt = null;
                    if (t.ToolCount == 0) t.StartedAt = now;
                }
                if (t.State == TaskState.Working) t.Detail = DescribeTool(p);
                break;

            case "PermissionRequest":
                NeedsYou(t, "WANTS " + DescribeTool(p));
                break;

            case "PostToolUse":
            case "PostToolUseFailure":
                if (justFinished) break;
                t.ToolCount++;
                _toolTimes.Enqueue(now);
                if (t.State == TaskState.NeedsYou) t.State = TaskState.Working;
                if (ev == "PostToolUseFailure" && !(p.TryGetProperty("is_interrupt", out var intr) && intr.ValueKind == JsonValueKind.True))
                    t.Detail = "MISSED: " + DescribeTool(p);
                break;

            case "Notification":
            {
                var type = Str(p, "notification_type");
                var msg = Str(p, "message");
                if (type == "permission_prompt" || (type.Length == 0 && msg.Contains("permission", StringComparison.OrdinalIgnoreCase)))
                    NeedsYou(t, msg.Length > 0 ? msg : "PERMISSION");
                else if (type is "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input")
                    NeedsYou(t, msg.Length > 0 ? msg : "AN ANSWER");
                else if (type == "idle_prompt" && t.Unseen)
                {
                    Message($"{t.Name} IS WAITING FOR YOU", Ink.Blue);
                    Play(TinkSound);
                }
                else if (msg.Length > 0 && type is not ("auth_success" or "elicitation_complete" or "elicitation_response"))
                    AddLog($"{t.Name}: {msg}");
                break;
            }
            case "Stop":
                if (t.State == TaskState.Idle && t.ToolCount == 0 && t.FinishedAt == null && t.Detail.Length == 0) break;
                t.State = TaskState.Done;
                t.FinishedAt = now;
                t.Unseen = true;
                var took = TimeParse.Short(now - t.StartedAt);
                t.Detail = $"DONE IN {took}, {t.ToolCount} TOOL{(t.ToolCount == 1 ? "" : "S")}";
                State.DoneToday++;
                Pickup($"{t.Name}: TASK COMPLETE ({took})", "WPNUP");
                Announce($"{t.Name} DONE!", t.Title, t.Detail, Ink.Gold);
                break;

            case "StopFailure":
                t.State = TaskState.Failed;
                t.FinishedAt = now;
                t.Unseen = true;
                t.Detail = "API ERROR";
                State.ErrorsToday++;
                Message($"{t.Name}: TASK DIED (API ERROR)", Ink.Red, "error");
                Announce($"{t.Name} FAILED", t.Title, "API ERROR", Ink.Red);
                Play("PLPAIN");
                break;

            case "SessionEnd":
                Tasks.Remove(t);
                AddLog($"{t.Name}: SESSION ENDED");
                break;
        }
    }

    private void NeedsYou(TaskItem t, string what)
    {
        what = Clean(what);
        t.Detail = what;
        if (t.State == TaskState.NeedsYou) return;
        t.State = TaskState.NeedsYou;
        Message($"{t.Name} NEEDS YOU: {what}", Ink.Red, "warn");
        Play("PLPAIN");
    }

    private string TinkSound => Sound != null && Sound.Has("DSRADIO") ? "RADIO" : "TINK";

    // ---------------------------------------------------------------- generic API

    public void Notify(string title, string message, string level, string? sound)
    {
        var text = title.Length > 0 ? $"{title}: {message}" : message;
        if (text.Trim().Length == 0) return;
        State.MessagesToday++;
        level = level.ToLowerInvariant();
        switch (level)
        {
            case "success" or "ok" or "done":
                Message(text, Ink.Green, "success");
                Announce(title.Length > 0 ? title + " DONE!" : message, title.Length > 0 ? message : "", "", Ink.Gold);
                _grin = true;
                BonusCount = Math.Min(BonusCount + 6, 30);
                PlayOr(sound, "ITEMUP");
                break;
            case "warn" or "warning":
                Message(text, Ink.Yellow, "warn");
                Announce(title.Length > 0 ? title : message, title.Length > 0 ? message : "", "", Ink.Yellow);
                Flinch(6);
                PlayOr(sound, "OOF");
                break;
            case "error" or "fail" or "failed":
                State.ErrorsToday++;
                Message(text, Ink.Red, "error");
                Announce(title.Length > 0 ? title + " FAILED" : message, title.Length > 0 ? message : "", "", Ink.Red);
                Flinch(16);
                PlayOr(sound, "PLPAIN");
                break;
            default:
                Message(text, Ink.Red);
                Announce(title.Length > 0 ? title : message, title.Length > 0 ? message : "", "", Ink.Blue);
                PlayOr(sound, TinkSound);
                break;
        }
    }

    private void PlayOr(string? requested, string fallback)
    {
        if (string.Equals(requested, "none", StringComparison.OrdinalIgnoreCase) || requested == "0" || requested == "false") return;
        Play(string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim().ToUpperInvariant());
    }

    public void Progress(string id, string title, int? percent, string state, string detail, string source)
    {
        if (id.Length == 0) id = title;
        if (id.Length == 0) return;
        var now = DateTime.Now;
        var t = Tasks.Find(x => !x.IsClaude && x.Id == id);
        state = state.ToLowerInvariant();
        if (state is "remove" or "clear" or "dismiss")
        {
            if (t != null) Tasks.Remove(t);
            Touch();
            return;
        }
        if (t == null)
        {
            t = new TaskItem { Id = id, Name = source.Length > 0 ? Clean(source).ToUpperInvariant() : "JOB", Title = title.Length > 0 ? title : id, StartedAt = now };
            Tasks.Add(t);
            State.StartedToday++;
            AddLog($"{t.Title}: STARTED");
        }
        if (title.Length > 0) t.Title = title;
        if (source.Length > 0) t.Name = Clean(source).ToUpperInvariant();
        if (detail.Length > 0) t.Detail = detail;
        if (percent != null) t.Percent = Math.Clamp(percent.Value, 0, 100);
        t.UpdatedAt = now;
        Touch();

        bool wasActive = t.State is TaskState.Working or TaskState.Idle;
        switch (state)
        {
            case "done" or "success" or "complete" or "completed" or "ok":
                t.State = TaskState.Done;
                t.Percent = 100;
                t.FinishedAt = now;
                t.Unseen = true;
                if (detail.Length == 0) t.Detail = $"DONE IN {TimeParse.Short(now - t.StartedAt)}";
                if (wasActive)
                {
                    State.DoneToday++;
                    Pickup($"{t.Title}: DONE", "ITEMUP");
                    Announce($"{t.Title} DONE!", t.Name, t.Detail, Ink.Gold);
                }
                break;
            case "failed" or "fail" or "error":
                t.State = TaskState.Failed;
                t.FinishedAt = now;
                t.Unseen = true;
                if (detail.Length == 0) t.Detail = "FAILED";
                if (wasActive)
                {
                    State.ErrorsToday++;
                    Message($"{t.Title}: FAILED", Ink.Red, "error");
                    Announce($"{t.Title} FAILED", t.Name, t.Detail, Ink.Red);
                    Play("PLPAIN");
                }
                break;
            default:
                if (!wasActive) { t.StartedAt = now; t.FinishedAt = null; t.Unseen = false; }
                t.State = TaskState.Working;
                break;
        }
    }

    // ---------------------------------------------------------------- reminders

    public Reminder AddReminder(string text, DateTime due, TimeSpan? every, bool announce = true)
    {
        var r = new Reminder { Text = Clean(text), Due = due, EverySeconds = (long)(every?.TotalSeconds ?? 0) };
        Reminders.Add(r);
        Reminders.Sort((a, b) => a.Due.CompareTo(b.Due));
        Touch();
        if (announce)
        {
            var when = every != null ? $"EVERY {TimeParse.Short(every.Value)}" : due.Date == DateTime.Today
                ? $"AT {due:HH:mm}" : $"{due:ddd HH:mm}";
            Message($"REMINDER SET: {r.Text} ({when})", Ink.Yellow);
            Play("SWTCHN");
        }
        return r;
    }

    private void Fire(Reminder r, DateTime now)
    {
        r.Firing = true;
        r.NextNag = now.AddMinutes(3);
        Message($"REMINDER: {r.Text}", Ink.Yellow);
        Play("GETPOW");
        Touch();
        ReminderFired?.Invoke();
    }

    /// <summary>Done (or snoozed). Repeating reminders roll on to their next time.</summary>
    public void Acknowledge(Reminder r, TimeSpan? snooze = null)
    {
        var now = DateTime.Now;
        r.Firing = false;
        if (snooze != null)
        {
            r.Due = now + snooze.Value;
            AddLog($"SNOOZED: {r.Text} ({TimeParse.Short(snooze.Value)})");
        }
        else if (r.EverySeconds > 0)
        {
            while (r.Due <= now) r.Due = r.Due.AddSeconds(r.EverySeconds);
            AddLog($"DONE: {r.Text}");
        }
        else
        {
            Reminders.Remove(r);
            AddLog($"DONE: {r.Text}");
        }
        Reminders.Sort((a, b) => a.Due.CompareTo(b.Due));
        Play("ITEMUP");
        BonusCount = Math.Min(BonusCount + 6, 30);
        Touch();
    }

    public void DeleteReminder(Reminder r)
    {
        Reminders.Remove(r);
        AddLog($"REMINDER DELETED: {r.Text}");
        Touch();
    }

    // ---------------------------------------------------------------- task list actions

    public void MarkSeen(TaskItem t)
    {
        if (!t.Unseen) return;
        t.Unseen = false;
        Touch();
    }

    public void MarkAllSeen()
    {
        foreach (var t in Tasks) t.Unseen = false;
        Touch();
    }

    /// <summary>Brings a Claude session's terminal or editor to the front. Call from the user's own click.</summary>
    public bool GoTo(TaskItem t)
    {
        if (CallerWindow.Focus(t.Window)) { MarkSeen(t); return true; }
        Message($"{t.Name}: CAN'T FIND ITS WINDOW (IT SHOWS UP WITH ITS NEXT STEP)", Ink.Gray, log: false);
        return false;
    }

    public void Dismiss(TaskItem t)
    {
        if (t.State == TaskState.NeedsYou) return;
        Tasks.Remove(t);
        Touch();
    }

    public void ClearFinished()
    {
        Tasks.RemoveAll(t => !t.IsClaude && t.State is TaskState.Done or TaskState.Failed);
        foreach (var t in Tasks) t.Unseen = false;
        Touch();
    }

    // ---------------------------------------------------------------- text helpers

    public static string Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => "",
        };
    }

    public static string Clean(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    private static string ProjectName(string cwd, string sid)
    {
        var leaf = cwd.TrimEnd('\\', '/');
        leaf = leaf.Length == 0 ? "" : Path.GetFileName(leaf);
        return (leaf.Length > 0 ? leaf : "CLAUDE " + sid[..Math.Min(4, sid.Length)]).ToUpperInvariant();
    }

    private static string PromptTitle(string prompt)
    {
        var s = Regex.Replace(prompt, @"<([A-Za-z][\w-]*)[^>]*>.*?</\1>", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        var line = s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = Clean(line);
        if (line.Length == 0) line = prompt.Trim().Length > 0 ? "(PASTED TEXT)" : "(EMPTY PROMPT)";
        return line.Length > 120 ? line[..120] : line;
    }

    private static string DescribeTool(JsonElement p)
    {
        var name = Str(p, "tool_name");
        p.TryGetProperty("tool_input", out var input);
        var arg = name switch
        {
            "Bash" or "PowerShell" => Str(input, "description") is { Length: > 0 } d ? d : Str(input, "command"),
            "Read" or "Write" or "Edit" or "NotebookEdit" => Path.GetFileName(Str(input, "file_path").Replace('/', '\\')),
            "Grep" or "Glob" => Str(input, "pattern"),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            "Agent" or "Task" => Str(input, "description"),
            "Skill" => Str(input, "skill"),
            _ => "",
        };
        if (name.StartsWith("mcp__")) name = name.Split("__", StringSplitOptions.RemoveEmptyEntries).Last();
        arg = Clean(arg.Split('\n')[0]);
        if (arg.Length > 80) arg = arg[..80];
        return (arg.Length == 0 ? name : $"{name}: {arg}").ToUpperInvariant();
    }

    public object Snapshot() => new
    {
        health = Health,
        profile = Gauges.Active.Name,
        gauges = Gauges.Slots.ToDictionary(s => s, s =>
        {
            var b = Gauges.Binding(s);
            var r = Gauges.Read(b);
            return new { source = b.Source, value = r.Value, status = r.Status, error = r.Error };
        }),
        working = WorkingCount,
        needsYou = NeedsYouCount,
        doneToday = State.DoneToday,
        tasks = Tasks.Select(t => new { t.Id, kind = t.IsClaude ? "claude" : "job", t.Name, t.Title, t.Detail, state = t.State.ToString(), t.Percent, t.Unseen, t.StartedAt, t.UpdatedAt, window = CallerWindow.Title(t.Window) }),
        reminders = Reminders.Select(r => new { r.Id, r.Text, r.Due, r.EverySeconds, r.Firing }),
        log = Log.TakeLast(20).Select(l => new { l.At, l.Level, l.Text }),
    };
}
