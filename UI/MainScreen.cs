using System.Windows;
using System.Windows.Input;
using DoomGuy.Core;
using DoomGuy.Doom;

namespace DoomGuy.UI;

/// <summary>What the screens need from the app around them.</summary>
public interface IShell
{
    void Minimize();
    void ShowMain();
    void Quit();
    void ApplyWindowScale();
    void ApplyMini();
    void SaveSettings();
    string WadName { get; }
    string ApiStatus { get; }
}

/// <summary>The full 320x200 window: tabs, the content panel in Doom's view border, and the status bar.</summary>
public sealed class MainScreen : Screen
{
    private enum Tab { Tasks, Remind, Hud, Log, Setup }
    private static readonly string[] TabNames = ["TASKS", "REMIND", "HUD", "LOG", "SETUP"];

    private const int X = 8, Y = 20, W = 304, H = 140, Top = Y + 13;

    private readonly Hub _hub;
    private readonly IShell _shell;
    private readonly Random _rng = new();
    private Tab _tab;
    private Tab? _pendingTab;
    private readonly int[] _scroll = new int[5];
    private string _hudSlot = "health";
    private string? _editKey;
    private string _editText = "";
    private string _input = "";
    private string _inputNote = "";
    private bool _inputNoteBad;
    private int _menuSel;
    private readonly List<SetupItem> _items;

    private int[]? _melt;
    private bool _meltRequested;
    private int _titleTics;

    private string[]? _modal;
    private Action? _modalYes;
    private int _quitTics;

    private readonly ClassicRenderer _classic;
    private readonly ModernRenderer _modern;

    private sealed record SetupItem(string Label, string Help, Func<string> Value, Action<int> Change);

    public MainScreen(Assets assets, ScreenHost host, Hub hub, IShell shell, bool showTitle, ModernRenderer modern) : base(assets, host)
    {
        _hub = hub;
        _shell = shell;
        _titleTics = showTitle && A.TitlePic != null ? 50 : 0;
        _classic = new ClassicRenderer(A, F);
        _modern = modern;
        R = hub.Settings.HdFace ? _modern : _classic;
        host.IsDragZone = (x, y) => _modal == null && _titleTics == 0 && y < 13 && !OverWidget(x, y);

        var s = hub.Settings;
        _items =
        [
            new("SOUND", "DOOM SOUND EFFECTS FOR EVENTS",
                () => s.Sound ? "ON" : "OFF", _ => { s.Sound = !s.Sound; ApplySound(); }),
            new("VOLUME", "CLICK THE BAR, OR LEFT/RIGHT",
                () => "", d => { s.Volume = Math.Clamp(s.Volume + d, 0, 8); ApplySound(); }),
            new("HD MODE", "SMOOTH TEXT AND FACE (WITH IN-BETWEEN FRAMES), OR THE ORIGINAL PIXELS",
                () => s.HdFace ? "ON" : "OFF", _ => s.HdFace = !s.HdFace),
            new("GOD MODE", "QUIET: NO SOUNDS, AND THE GOLDEN EYES",
                () => s.God ? "ON" : "OFF", _ => s.God = !s.God),
            new("STATUS BAR", "WHEN MINIMIZED: THE WHOLE BAR, OR THE HEAD THAT ANNOUNCES THINGS",
                () => s.MiniMode == "face" ? "FACE ONLY" : "FULL BAR", _ => { s.MiniMode = s.MiniMode == "face" ? "bar" : "face"; shell.ApplyMini(); }),
            new("BAR SIZE", "PIXEL SCALE OF THE MINIMIZED BAR AND ITS MESSAGES",
                () => $"{s.MiniScale}X", d => { s.MiniScale = Wrap(s.MiniScale + d, 1, 5); shell.ApplyMini(); }),
            new("FACE SIZE", "SIZE OF THE HEAD IN FACE-ONLY MODE (6X = EVERY HD PIXEL)",
                () => $"{s.FaceScale}X", d => { s.FaceScale = Wrap(s.FaceScale + d, 2, 8); shell.ApplyMini(); }),
            new("HIDE IN FULLSCREEN", "HIDE THE BAR WHILE A GAME OR VIDEO IS FULLSCREEN",
                () => s.HideOverFullscreen ? "ON" : "OFF", _ => s.HideOverFullscreen = !s.HideOverFullscreen),
            new("START WITH WINDOWS", "LAUNCH AS THE STATUS BAR WHEN YOU SIGN IN",
                () => Autostart.Enabled ? "ON" : "OFF", _ => Try(() => Autostart.Enabled = !Autostart.Enabled)),
            new("CLAUDE HOOKS", "REPORT EVERY CLAUDE CODE SESSION HERE",
                () => ClaudeHooks.Installed() ? "ON" : "OFF", _ => ToggleHooks()),
        ];
    }

    private static int Wrap(int v, int lo, int hi) => v < lo ? hi : v > hi ? lo : v;

    private void ApplySound()
    {
        if (_hub.Sound == null) return;
        _hub.Sound.Enabled = _hub.Settings.Sound;
        _hub.Sound.Volume = _hub.Settings.Volume;
    }

    private void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { _hub.Message("COULDN'T: " + ex.Message, Ink.Red, "error"); }
    }

    private void ToggleHooks()
    {
        Try(() =>
        {
            if (ClaudeHooks.Installed())
            {
                ClaudeHooks.Uninstall();
                _hub.Message("CLAUDE HOOKS REMOVED", Ink.Yellow);
            }
            else
            {
                ClaudeHooks.Install(_hub.Settings.Port);
                _hub.Message("CLAUDE HOOKS INSTALLED. NEW PROMPTS WILL SHOW UP HERE.", Ink.Green, "success");
            }
        });
    }

    /// <summary>Animating on its own (melt, title screen): needs every frame drawn.</summary>
    public bool Busy => _melt != null || _meltRequested || _titleTics > 0;

    /// <summary>This frame was drawn by the HD renderer (the window shows its canvas instead of the pixels).</summary>
    public bool Modern => R is ModernRenderer;

    /// <summary>The 8-bit look's small-text layer, when this frame put anything on it.</summary>
    public HdCanvas? Overlay => R is ClassicRenderer { OverlayUsed: true } c ? c.Overlay : null;
    public HdCanvas Canvas => _modern.Canvas;

    public void ShowTab(int tab) => _pendingTab = (Tab)Math.Clamp(tab, 0, TabNames.Length - 1);

    public void Tick()
    {
        if (_titleTics > 0 && --_titleTics == 0) _meltRequested = true;
        if (_quitTics > 0 && --_quitTics == 0) _shell.Quit();
        if (_melt == null) return;
        bool done = true;
        for (int i = 0; i < _melt.Length; i++)
        {
            if (_melt[i] < 0) { _melt[i]++; done = false; }
            else if (_melt[i] < F.H)
            {
                int dy = _melt[i] < 16 ? _melt[i] + 1 : 8;
                _melt[i] = Math.Min(F.H, _melt[i] + dy);
                done = false;
            }
        }
        if (done) _melt = null;
    }

    private void StartMelt()
    {
        R!.Snapshot();
        _melt = new int[F.W / 2];
        _melt[0] = -_rng.Next(16);
        for (int i = 1; i < _melt.Length; i++)
        {
            _melt[i] = _melt[i - 1] + _rng.Next(3) - 1;
            if (_melt[i] > 0) _melt[i] = 0;
            else if (_melt[i] == -16) _melt[i] = -15;
        }
    }

    public void Draw()
    {
        IRenderer want = _hub.Settings.HdFace ? _modern : _classic;
        if (want != R) { R = want; _melt = null; }
        if (_pendingTab != null)
        {
            StartMelt();
            _tab = _pendingTab.Value;
            _pendingTab = null;
        }
        if (_meltRequested)
        {
            StartMelt();
            _meltRequested = false;
        }

        BeginFrame();
        R!.BeginFrame();
        if (_titleTics > 0)
        {
            R.TitlePic();
            if (In.Pressed || In.Keys.Count > 0) { _titleTics = 1; In.Pressed = false; }
            In.EndTic();
            return;
        }

        HandleKeys();

        // While the prompt is up, the screen underneath must not see the mouse.
        var (mx, my, pressed) = (In.MX, In.MY, In.Pressed);
        if (_modal != null) { In.MX = In.MY = -1; In.Pressed = false; In.Wheel = 0; }

        R.Background(X, Y, W, H);
        DrawTabs();
        DrawStatusLine();
        R.Separator(X, Y + 11, W);

        R.SetClip(X, Top, W, Y + H - Top);
        switch (_tab)
        {
            case Tab.Tasks: DrawTasks(); break;
            case Tab.Remind: DrawRemind(); break;
            case Tab.Hud: DrawHud(); break;
            case Tab.Log: DrawLog(); break;
            case Tab.Setup: DrawSetup(); break;
        }
        R.ResetClip();

        R.StatusBar(_hub);
        if (Hover(143, 168, 34, 32) && Clicked(143, 168, 34, 32)) { _hub.MarkAllSeen(); _hub.Play("PISTOL"); }

        if (_modal != null)
        {
            (In.MX, In.MY, In.Pressed) = (mx, my, pressed);
            DrawModal();
        }
        if (_melt != null) R.ApplyMelt(_melt);
        In.EndTic();
    }

    // ---------------------------------------------------------------- frame pieces

    private void DrawTabs()
    {
        int x = 6;
        for (int i = 0; i < TabNames.Length; i++)
        {
            var label = TabNames[i];
            if (i == 0 && _hub.NeedsYouCount > 0) label += "!";
            if (i == 1 && _hub.FiringCount > 0) label += "!";
            bool sel = (int)_tab == i;
            if (Button(x, 3, label, sel ? Ink.Yellow : Ink.Red, sel ? Ink.Yellow : Ink.Gold, glow: sel) && !sel)
            {
                _pendingTab = (Tab)i;
                _hub.Play("SWTCHN");
            }
            if (sel) R!.TabMark(x, 11, Width(label));
            x += Width(label) + 14;
        }
        if (Button(296, 3, "_", Ink.Red, Ink.Yellow)) _shell.Minimize();
        if (Button(308, 3, "X", Ink.Red, Ink.Yellow)) AskQuit();
    }

    private void DrawStatusLine()
    {
        if (_hub.Hud.Count > 0)
        {
            var m = _hub.Hud[^1];
            Text(X + 2, Y + 1, Fit(m.Text, W - 4), m.Ink);
            return;
        }
        string s;
        switch (_tab)
        {
            case Tab.Remind:
                var next = _hub.Reminders.FirstOrDefault(r => !r.Firing);
                s = _hub.FiringCount > 0 ? $"{_hub.FiringCount} DUE NOW. CLICK ONE TO TICK IT OFF."
                    : next != null ? $"NEXT: {next.Text} IN {TimeParse.Short(next.Due - DateTime.Now)}" : "NO REMINDERS SET";
                break;
            case Tab.Log:
                s = $"API {_shell.ApiStatus}   WAD {_shell.WadName}   {_hub.Log.Count} ENTRIES";
                break;
            case Tab.Hud:
                s = _editKey != null ? "TYPE, ENTER TO SAVE, ESC TO CANCEL" : Gauges.Def(_hub.Gauges.Binding(_hudSlot).Source).Help;
                break;
            case Tab.Setup:
                s = _menuSel >= 0 && _menuSel < _items.Count ? _items[_menuSel].Help : "";
                break;
            default:
                s = _tasksHint ?? $"HEALTH {_hub.Health}%   WORKING {_hub.WorkingCount}   NEEDS YOU {_hub.NeedsYouCount}   DONE {_hub.State.DoneToday}";
                break;
        }
        Text(X + 2, Y + 1, Fit(s.ToUpperInvariant(), W - 4), Ink.Gray);
    }

    // ---------------------------------------------------------------- TASKS

    /// <summary>What clicking the hovered task row would do, for the header line (null = the usual numbers).</summary>
    private string? _tasksHint;

    private void DrawTasks()
    {
        _tasksHint = null;
        const int rowH = 22;
        int bottom = Y + H - 11;
        var tasks = _hub.Tasks;
        var now = DateTime.Now;
        if (tasks.Count == 0)
        {
            TextCenter(X + W / 2, Y + 46, "NO TASKS. ALL QUIET ON PHOBOS.", Ink.Gray);
            TextCenter(X + W / 2, Y + 64, ClaudeHooks.Installed() ? "CLAUDE SESSIONS SHOW UP HERE AS YOU PROMPT." : "TURN ON CLAUDE HOOKS IN SETUP", Ink.Dim);
            TextCenter(X + W / 2, Y + 76, "SCRIPTS CAN REPORT JOBS TO /PROGRESS.", Ink.Dim);
            return;
        }

        int visible = (bottom - Top) / rowH;
        ref int scroll = ref _scroll[0];
        if (Hover(X, Top, W, bottom - Top)) scroll -= In.Wheel;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, tasks.Count - visible));

        R!.SetClip(X, Top, W, bottom - Top);
        for (int i = scroll; i < tasks.Count; i++)
        {
            int y = Top + (i - scroll) * rowH;
            if (y >= bottom) break;
            var t = tasks[i];
            bool over = Hover(X, y, W, rowH);
            if (over) R!.RowHighlight(X, y, W, rowH, false);

            TaskIcon(t, X + 10, y + rowH - 1);

            int nx = X + 22;
            int ex = Text(nx, y + 3, Fit(t.Name, 100), i < 6 ? Ink.Red : Ink.Dark);
            var (state, ink) = StateLabel(t, now);
            int stateW = Width(state);
            TextRight(X + W - 14, y + 3, state, ink);
            Text(ex + 6, y + 3, Fit(t.Title.ToUpperInvariant(), X + W - 14 - stateW - 6 - ex - 6), Ink.White);

            // The detail line (what it's doing right now) in the small font: more of it fits, and it reads as
            // the quieter line under the name.
            if (!t.IsClaude && t.Percent >= 0 && t.State is TaskState.Working or TaskState.Done)
            {
                int bw = 64;
                R!.Progress(nx, y + 14, bw, 5, t.Percent, t.State == TaskState.Done);
                int px = R.SmallText(nx + bw + 4, y + 13, $"{t.Percent}%", Ink.Yellow);
                R.SmallText(px + 4, y + 13, FitSmall(t.Detail.ToUpperInvariant(), X + W - 14 - px - 4), Ink.Gray);
            }
            else R!.SmallText(nx, y + 13, FitSmall(t.Detail.ToUpperInvariant(), X + W - 14 - nx), Ink.Gray);

            // Something waiting on you can't be dismissed: it clears itself once it's answered.
            if (over && t.State is not (TaskState.Working or TaskState.NeedsYou))
            {
                if (Button(X + W - 9, y + 8, "X", Ink.Dark, Ink.Yellow)) { _hub.Dismiss(t); _hub.Play("PSTOP"); break; }
            }
            // A Claude session whose window is known: clicking goes there (and marks it seen).
            bool canGo = t.IsClaude && CallerWindow.Alive(t.Window);
            if (over && canGo) _tasksHint = t.State == TaskState.NeedsYou ? $"CLICK TO GO ANSWER {t.Name}" : $"CLICK TO GO TO {t.Name}";
            if (Clicked(X, y, W, rowH))
            {
                if (canGo) _hub.Play(_hub.GoTo(t) ? "SWTCHN" : "NOWAY");
                else if (t.Unseen) { _hub.MarkSeen(t); _hub.Play("PISTOL"); }
            }
        }
        R!.SetClip(X, Top, W, Y + H - Top);

        if (tasks.Count > visible) TextRight(X + W - 2, bottom + 1, $"{scroll + 1}-{Math.Min(tasks.Count, scroll + visible)} OF {tasks.Count}", Ink.Dim);
        int bx = X + 2;
        if (_hub.Tasks.Any(t => t.Unseen)) { if (Button(bx, bottom + 1, "MARK ALL SEEN", Ink.Gray)) { _hub.MarkAllSeen(); _hub.Play("PISTOL"); } bx += Width("MARK ALL SEEN") + 12; }
        if (_hub.Tasks.Any(t => !t.IsClaude && t.State is TaskState.Done or TaskState.Failed))
            if (Button(bx, bottom + 1, "CLEAR FINISHED JOBS", Ink.Gray)) { _hub.ClearFinished(); _hub.Play("PSTOP"); }
    }

    /// <summary>Fit for the small font: it is 3/4 the width, so 4/3 the font pixels fit in the same units.</summary>
    private string FitSmall(string s, int units) => A.Font.Fit(s, units * 4 / 3);

    private void TaskIcon(TaskItem t, int cx, int bottom)
    {
        bool blink = _hub.Tic / 10 % 2 == 0;
        char bonus = BonusCycle[(int)(_hub.Tic / 6 % BonusCycle.Length)];
        switch (t.State)
        {
            case TaskState.Working: Sprite(t.IsClaude ? $"BON1{bonus}0" : $"BON2{bonus}0", cx, bottom); break;
            case TaskState.NeedsYou: Sprite(blink ? "RKEYA0" : "RKEYB0", cx, bottom - 2); break;
            case TaskState.Done: Sprite(t.Unseen && !blink ? "BKEYB0" : "BKEYA0", cx, bottom - 2); break;
            case TaskState.Failed: Sprite(t.Unseen && !blink ? "RSKUB0" : "RSKUA0", cx, bottom - 2); break;
            default: Sprite("CANDA0", cx, bottom); break;
        }
    }

    private (string, Ink) StateLabel(TaskItem t, DateTime now)
    {
        bool blink = _hub.Tic / 10 % 2 == 0;
        return t.State switch
        {
            TaskState.Working => ($"WORKING {TimeParse.Short(now - t.StartedAt)}", Ink.Yellow),
            TaskState.NeedsYou => ("NEEDS YOU", blink ? Ink.Red : Ink.Yellow),
            TaskState.Done => ($"DONE {TimeParse.Short(now - (t.FinishedAt ?? now))} AGO", t.Unseen ? Ink.Green : Ink.Gray),
            TaskState.Failed => ("FAILED", Ink.Red),
            _ => ("IDLE", Ink.Gray),
        };
    }

    // ---------------------------------------------------------------- REMIND

    private void DrawRemind()
    {
        var now = DateTime.Now;
        int y0 = Top;
        int ix = Text(X + 2, y0, ">", Ink.Yellow) + 4;
        var shown = _input.ToUpperInvariant();
        int room = X + W - 10 - ix;
        while (shown.Length > 0 && Width(shown) > room) shown = shown[1..];
        int cx = Text(ix, y0, shown, Ink.Red);
        if (_hub.Tic / 8 % 2 == 0) Text(cx, y0, "_", Ink.Red);
        if (_input.Length == 0) Text(cx + 10, y0, "E.G. STAND UP IN 30M", Ink.Dim);
        Widget(X, y0 - 2, W, 11);

        var note = _inputNote.Length > 0 ? _inputNote : "IN 30M / AT 17:30 / EVERY 1H / TOMORROW 9AM";
        Text(X + 2, y0 + 11, Fit(note, W - 4), _inputNoteBad ? Ink.Red : Ink.Gray);
        R!.Separator(X, y0 + 21, W);

        int top = y0 + 25, bottom = Y + H - 1, rowH = 12;
        var list = _hub.Reminders;
        if (list.Count == 0)
        {
            TextCenter(X + W / 2, top + 30, "NOTHING TO REMEMBER. LUCKY YOU.", Ink.Dim);
            return;
        }
        int visible = (bottom - top) / rowH;
        ref int scroll = ref _scroll[1];
        if (Hover(X, top, W, bottom - top)) scroll -= In.Wheel;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, list.Count - visible));
        bool blink = _hub.Tic / 10 % 2 == 0;

        for (int i = scroll; i < list.Count; i++)
        {
            int y = top + (i - scroll) * rowH;
            if (y + rowH > bottom + 2) break;
            var r = list[i];
            bool over = Hover(X, y - 2, W, rowH);
            if (over) R!.RowHighlight(X, y - 2, W, rowH, false);
            Text(X + 2, y, r.Firing ? (blink ? "DUE!" : "") : TimeParse.Short(r.Due - now), Ink.Yellow);
            var when = r.EverySeconds > 0 ? $"EVERY {TimeParse.Short(TimeSpan.FromSeconds(r.EverySeconds))}"
                : r.Due.Date == now.Date ? r.Due.ToString("HH:mm") : r.Due.ToString("ddd HH:mm").ToUpperInvariant();
            int ww = Width(when);
            TextRight(X + W - 14, y, when, Ink.Gray);
            Text(X + 44, y, Fit(r.Text.ToUpperInvariant(), W - 44 - 14 - ww - 8), r.Firing ? Ink.Yellow : Ink.Red);
            if (over && Button(X + W - 9, y, "X", Ink.Dark, Ink.Yellow)) { _hub.DeleteReminder(r); _hub.Play("PSTOP"); break; }
            if (r.Firing && Clicked(X, y - 2, W, rowH)) { _hub.Acknowledge(r); break; }
        }
    }

    private void SubmitReminder()
    {
        var text = _input.Trim();
        if (text.Length == 0) return;
        if (!TimeParse.Reminder(text, DateTime.Now, out var what, out var due, out var every) || what.Trim().Length == 0)
        {
            _inputNote = "SAY WHEN: \"IN 30M\", \"AT 17:30\", \"EVERY 1H\", \"TOMORROW 9AM\"";
            _inputNoteBad = true;
            _hub.Play("OOF");
            return;
        }
        _hub.AddReminder(what, due, every);
        _input = "";
        _inputNote = "";
        _inputNoteBad = false;
    }

    // ---------------------------------------------------------------- HUD (what the bar's numbers follow)

    private static readonly string[] SlotLabels = ["HEALTH", "AMMO", "ARMOR"];

    private void DrawHud()
    {
        var g = _hub.Gauges;
        var profile = g.Active;
        int y = Top + 1;

        // Profile: < NAME > (click the name to rename), + NEW.
        Text(X + 4, y, "PROFILE", Ink.Red);
        int px = X + 76;
        if (Button(px, y, "<", Ink.Gold, Ink.Yellow)) CycleProfile(-1);
        px += Width("<") + 6;
        if (_editKey == "profile") px = EditField(px, y, 120);
        else
        {
            if (Button(px, y, profile.Name.ToUpperInvariant(), Ink.Yellow, Ink.White)) BeginEdit("profile", profile.Name);
            px += Width(profile.Name.ToUpperInvariant()) + 6;
        }
        if (Button(px, y, ">", Ink.Gold, Ink.Yellow)) CycleProfile(+1);
        if (Button(X + W - 4 - Width("ADD"), y, "ADD", Ink.Gray, Ink.Yellow)) NewProfile();

        // The three slots.
        y = Top + 14;
        for (int i = 0; i < Gauges.Slots.Length; i++, y += 13)
        {
            var slot = Gauges.Slots[i];
            var b = g.Binding(slot);
            var def = Gauges.Def(b.Source);
            bool sel = slot == _hudSlot;
            if (sel) R!.RowHighlight(X, y - 2, W, 12, true);
            if (Clicked(X, y - 2, 70, 12)) { _hudSlot = slot; _hub.Play("PSTOP"); }
            Widget(X, y - 2, 70, 12);
            Text(X + 4, y, SlotLabels[i], sel ? Ink.Yellow : Ink.Red);
            int sx = X + 76;
            if (Button(sx, y, "<", Ink.Gold, Ink.Yellow)) CycleSource(slot, -1);
            sx += Width("<") + 6;
            Text(sx, y, def.Label, Ink.White);
            sx += Width(def.Label) + 6;
            if (Button(sx, y, ">", Ink.Gold, Ink.Yellow)) CycleSource(slot, +1);
            var r = g.Read(slot);
            var shown = r.Value == null ? "--" : def.Percent || Gauges.Inverted(b) ? $"{r.Value}%" : $"{r.Value}";
            TextRight(X + W - 4, y, shown, r.Error ? Ink.Red : Ink.Gold);
        }
        R!.Separator(X, y, W);

        // The selected slot's source: what it is, what it last read, and its settings.
        var bind = g.Binding(_hudSlot);
        var src = Gauges.Def(bind.Source);
        // (What the source means is in the header line above.)
        y += 4;
        var read = g.Read(bind);
        // INVERT sits on this line: the settings below already fill the panel for the busiest sources.
        bool inverted = Gauges.Inverted(bind);
        var invert = inverted ? "INVERT: ON" : "INVERT: OFF";
        int ix = X + W - 4 - Width(invert);
        Text(X + 4, y, Fit((read.Error ? "" : "NOW: ") + read.Status, ix - X - 12), read.Error ? Ink.Red : Ink.Gray);
        if (Button(ix, y, invert, inverted ? Ink.Yellow : Ink.Gray, Ink.Yellow)) ToggleInvert(bind);
        y += 13;

        // Its settings, then EMPTY AT for an inverted count, then LABEL: the word on the bar (blank = say what it tracks).
        var args = src.Args.AsEnumerable();
        if (inverted && !src.Percent) args = args.Append("EMPTY AT");
        foreach (var arg in args.Append("LABEL"))
        {
            var key = _hudSlot + ":" + arg;
            Text(X + 4, y, arg, Ink.Red);
            int fx = X + 76;
            if (_editKey == key) EditField(fx, y, W - 80);
            else
            {
                var v = bind.Args.GetValueOrDefault(arg, "");
                var shown = v.Length == 0 ? arg switch
                {
                    "LABEL" => $"({g.Label(_hudSlot)})",
                    "EMPTY AT" => $"({Gauges.EmptyAt(bind)}: 0% AT {Gauges.EmptyAt(bind)} OR MORE)",
                    _ => "(NOT SET)",
                } : arg == "TOKEN" ? new string('*', Math.Min(12, v.Length)) : v.ToUpperInvariant();
                if (Button(fx, y, Fit(shown, W - 84), v.Length == 0 ? Ink.Dim : Ink.White, Ink.Yellow)) BeginEdit(key, v);
            }
            y += 12;
        }

        if (bind.Source is "plan" or "week" or "context")
        {
            Text(X + 4, y, "STATUS LINE", Ink.Red);
            if (ClaudeHooks.OtherStatusLine()) Text(X + 76, y, "ANOTHER STATUS LINE IS SET", Ink.Dim);
            else
            {
                bool on = ClaudeHooks.StatusLineInstalled();
                if (Button(X + 76, y, on ? "ON" : "OFF  (CLICK TO TURN ON)", on ? Ink.Yellow : Ink.Gray, Ink.Yellow)) ToggleStatusLine(on);
            }
        }
    }

    private void CycleSource(string slot, int dir)
    {
        var b = _hub.Gauges.Binding(slot);
        int i = Array.FindIndex(Gauges.Sources, s => s.Id == b.Source);
        b.Source = Gauges.Sources[(i + dir + Gauges.Sources.Length) % Gauges.Sources.Length].Id;
        _hudSlot = slot;
        _editKey = null;
        _hub.Gauges.Invalidate();
        _shell.SaveSettings();
        _hub.Play("PISTOL");
    }

    private void ToggleInvert(SlotBinding b)
    {
        if (Gauges.Inverted(b)) b.Args.Remove("INVERT"); else b.Args["INVERT"] = "ON";
        _editKey = null;
        _shell.SaveSettings();
        _hub.Play("PISTOL");
    }

    private void CycleProfile(int dir)
    {
        var s = _hub.Settings;
        var list = s.Profiles;
        int i = list.IndexOf(_hub.Gauges.Active);
        s.Profile = list[(i + dir + list.Count) % list.Count].Name;
        _editKey = null;
        _hub.Gauges.Invalidate();
        _shell.SaveSettings();
        _hub.Play("SWTCHN");
    }

    private void NewProfile()
    {
        var s = _hub.Settings;
        int n = s.Profiles.Count + 1;
        while (s.Profiles.Any(p => p.Name.Equals($"PROFILE {n}", StringComparison.OrdinalIgnoreCase))) n++;
        s.Profiles.Add(new Profile { Name = $"PROFILE {n}" });
        s.Profile = $"PROFILE {n}";
        BeginEdit("profile", s.Profile);
        _shell.SaveSettings();
    }

    private void ToggleStatusLine(bool on)
    {
        Try(() =>
        {
            if (on) ClaudeHooks.UninstallStatusLine();
            else ClaudeHooks.InstallStatusLine(_hub.Settings.Port);
            _hub.Message(on ? "CLAUDE STATUS LINE REMOVED" : "CLAUDE STATUS LINE ON: LIMITS ARRIVE WITH CLAUDE'S NEXT REPLY", on ? Ink.Yellow : Ink.Green);
        });
        _hub.Play("PISTOL");
    }

    private void BeginEdit(string key, string value)
    {
        if (_editKey != null && _editKey != key) CommitEdit();
        _editKey = key;
        _editText = value;
        _hub.Play("PSTOP");
    }

    /// <summary>The field being typed in: tail of the text and a blinking cursor.</summary>
    private int EditField(int x, int y, int width)
    {
        var shown = _editText.ToUpperInvariant();
        while (shown.Length > 0 && Width(shown + "_") > width) shown = shown[1..];
        R!.Field(x - 2, y - 2, width + 4, 11);
        Widget(x - 2, y - 2, width + 4, 11);
        int end = Text(x, y, shown, Ink.Yellow);
        if (_hub.Tic / 8 % 2 == 0) Text(end, y, "_", Ink.Yellow);
        return x + width + 6;
    }

    private void EditKeys()
    {
        foreach (var ch in In.Typed)
            if (!char.IsControl(ch) && _editText.Length < 400) _editText += ch;
        In.Typed = "";
        foreach (var k in In.Keys)
        {
            if (k == Key.Escape) { _editKey = null; return; }
            if (k == Key.Back && _editText.Length > 0) _editText = In.Ctrl ? "" : _editText[..^1];
            else if (k == Key.V && In.Ctrl)
            {
                try { if (Clipboard.ContainsText()) _editText += Clipboard.GetText().Trim(); } catch { }
            }
            else if (k is Key.Enter or Key.Tab) { CommitEdit(); return; }
        }
        // Clicking anywhere outside the field saves it.
        if (In.Pressed && !OverWidget(In.PX, In.PY)) CommitEdit();
    }

    private void CommitEdit()
    {
        if (_editKey == null) return;
        var text = _editText.Trim();
        if (_editKey == "profile")
        {
            var p = _hub.Gauges.Active;
            var name = text.ToUpperInvariant();
            if (name.Length > 0 && !_hub.Settings.Profiles.Any(x => x != p && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                p.Name = name;
                _hub.Settings.Profile = name;
            }
        }
        else
        {
            var parts = _editKey.Split(':', 2);
            var b = _hub.Gauges.Binding(parts[0]);
            if (text.Length == 0) b.Args.Remove(parts[1]); else b.Args[parts[1]] = text;
            _hub.Gauges.Invalidate();
        }
        _editKey = null;
        _shell.SaveSettings();
        _hub.Play("PISTOL");
    }

    // ---------------------------------------------------------------- LOG

    /// <summary>One line on how today went: finished, errors, and the longest task or job.</summary>
    private string DaySummary()
    {
        var s = _hub.State;
        if (s.DoneToday == 0 && s.ErrorsToday == 0) return "TODAY: NOTHING FINISHED YET";
        var text = $"TODAY: {s.DoneToday} DONE, {s.ErrorsToday} ERROR{(s.ErrorsToday == 1 ? "" : "S")}";
        if (s.LongestTodaySeconds >= 1) text += $", LONGEST {TimeParse.Short(TimeSpan.FromSeconds(s.LongestTodaySeconds))} ({s.LongestTodayName})";
        return text;
    }

    private void DrawLog()
    {
        int rowH = 11, bottom = Y + H - 1;
        // The day's summary stays at the top; the entries scroll under it.
        R!.SmallText(X + 2, Top + 2, FitSmall(DaySummary(), W - 4), Ink.Gold);
        R!.Separator(X, Top + 10, W);
        int top = Top + 13;
        int visible = (bottom - top) / rowH;
        var log = _hub.Log;
        ref int scroll = ref _scroll[2];
        if (Hover(X, top, W, bottom - top)) scroll -= In.Wheel * 3;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, log.Count - visible));
        if (log.Count == 0) { TextCenter(X + W / 2, top + 40, "NOTHING HAS HAPPENED. YET.", Ink.Dim); return; }
        for (int i = 0; i < visible && scroll + i < log.Count; i++)
        {
            var l = log[log.Count - 1 - scroll - i];
            int y = top + i * rowH;
            Text(X + 2, y, l.At.Date == DateTime.Today ? l.At.ToString("HH:mm") : l.At.ToString("dd/MM"), Ink.Dim);
            var ink = l.Level switch { "success" => Ink.Gold, "warn" => Ink.Yellow, "error" => Ink.Red, _ => Ink.Gray };
            Text(X + 44, y, Fit(l.Text, W - 46), ink);
        }
    }

    // ---------------------------------------------------------------- SETUP

    private void DrawSetup()
    {
        const int rowH = 12;
        int y0 = Top + 2;
        for (int i = 0; i < _items.Count; i++)
        {
            int y = y0 + i * rowH;
            var item = _items[i];
            if (Hover(X, y - 2, W, rowH) && In.MX >= 0 && _menuSel != i)
            {
                _menuSel = i;
                _hub.Play("PSTOP");
            }
            Widget(X, y - 2, W, rowH);
            Text(X + 28, y, item.Label, Ink.Red);
            int vx = X + 176;
            if (item.Label == "VOLUME")
            {
                R!.Thermo(vx, y - 2, _hub.Settings.Volume);
                if (In.Pressed && In.PY >= y - 2 && In.PY < y - 2 + rowH && In.PX >= vx)
                {
                    In.Pressed = false;
                    _hub.Settings.Volume = Math.Clamp((In.PX - vx - 8) / 8, 0, 8);
                    ApplySound();
                    _shell.SaveSettings();
                    _hub.Play("STNMOV");
                }
            }
            else Text(vx, y, item.Value(), Ink.Yellow);

            if (Clicked(X, y - 2, W, rowH)) Change(i, +1);
            if (In.RightPressed && In.PY >= y - 2 && In.PY < y - 2 + rowH) { In.RightPressed = false; Change(i, -1); }
        }
        if (_menuSel >= 0 && _menuSel < _items.Count)
            R!.Skull(X + 4, y0 + _menuSel * rowH - 6, (int)(_hub.Tic / 8 % 2));
    }


    private void Change(int i, int dir)
    {
        _items[i].Change(dir);
        _shell.SaveSettings();
        _hub.Play(_items[i].Label == "VOLUME" ? "STNMOV" : "PISTOL");
    }

    // ---------------------------------------------------------------- keys and the quit prompt

    private void HandleKeys()
    {
        if (_modal != null)
        {
            foreach (var k in In.Keys)
            {
                if (k is Key.Y or Key.Enter) { var yes = _modalYes; _modal = null; yes?.Invoke(); return; }
                if (k is Key.N or Key.Escape) { _modal = null; _hub.Play("SWTCHX"); return; }
            }
            In.Typed = "";
            return;
        }

        if (_editKey != null)
        {
            EditKeys();
            return;
        }

        if (_tab == Tab.Remind)
        {
            foreach (var ch in In.Typed)
                if (!char.IsControl(ch) && _input.Length < 200) { _input += ch; _inputNote = ""; _inputNoteBad = false; }
        }

        foreach (var k in In.Keys)
        {
            if (k == Key.Tab)
            {
                _pendingTab = (Tab)(((int)_tab + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? TabNames.Length - 1 : 1)) % TabNames.Length);
                _hub.Play("SWTCHN");
                continue;
            }
            if (k == Key.Escape)
            {
                if (_tab == Tab.Remind && _input.Length > 0) { _input = ""; _inputNote = ""; }
                else _shell.Minimize();
                continue;
            }
            switch (_tab)
            {
                case Tab.Remind:
                    if (k == Key.Back && _input.Length > 0) _input = In.Ctrl ? "" : _input[..^1];
                    else if (k == Key.Enter) SubmitReminder();
                    else if (k == Key.V && In.Ctrl)
                    {
                        try { if (Clipboard.ContainsText()) _input += Hub.Clean(Clipboard.GetText()); } catch { }
                    }
                    break;
                case Tab.Setup:
                    if (k == Key.Up) { _menuSel = (_menuSel + _items.Count - 1) % _items.Count; _hub.Play("PSTOP"); }
                    else if (k == Key.Down) { _menuSel = (_menuSel + 1) % _items.Count; _hub.Play("PSTOP"); }
                    else if (k is Key.Left or Key.Right or Key.Enter) Change(_menuSel, k == Key.Left ? -1 : 1);
                    break;
                case Tab.Tasks or Tab.Log:
                    if (k == Key.Up) _scroll[(int)_tab]--;
                    else if (k == Key.Down) _scroll[(int)_tab]++;
                    break;
            }
        }
    }

    private static readonly string[] QuitLines =
    [
        "ARE YOU SURE YOU WANT TO QUIT?\nCLAUDE MIGHT STILL NEED YOU.",
        "DON'T GO NOW. THERE'S A TASK\nAROUND THAT CORNER!",
        "GO AHEAD AND LEAVE.\nSEE IF THE BUILD CARES.",
        "QUIT? YOUR HEALTH IS FINE.\nFOR NOW.",
        "IF YOU LEAVE, WHO WILL\nREMIND YOU TO DRINK WATER?",
        "YOU'RE LUCKY I DON'T SMACK\nYOU FOR THINKING ABOUT LEAVING.",
    ];

    private static readonly string[] QuitSounds = ["PLDETH", "DMPAIN", "POPAIN", "SLOP", "TELEPT", "POSIT1", "POSIT3", "SGTATK"];

    public void AskQuit()
    {
        if (_modal != null || _quitTics > 0) return;
        _hub.Play("SWTCHN");
        _modal = [.. QuitLines[_rng.Next(QuitLines.Length)].Split('\n'), "", "(PRESS Y TO QUIT)"];
        _modalYes = () =>
        {
            if (_hub.Sound != null && _hub.Settings.Sound && !_hub.Settings.God)
            {
                _hub.Sound.Play(QuitSounds[_rng.Next(QuitSounds.Length)]);
                _quitTics = 40;
            }
            else _shell.Quit();
        };
    }

    private void DrawModal()
    {
        R!.Dim();
        int y = 84 - _modal!.Length * 9 / 2;
        int boxW = _modal.Max(l => Width(l)) + 40;
        R.Dialog(F.W / 2 - boxW / 2, y - 12, boxW, _modal.Length * 9 + 34);
        foreach (var line in _modal)
        {
            TextCenter(F.W / 2, y, line, Ink.Red);
            y += 9;
        }
        y += 6;
        if (Button(F.W / 2 - 34, y, "YES", Ink.Red, Ink.Yellow)) { var yes = _modalYes; _modal = null; yes?.Invoke(); return; }
        if (Button(F.W / 2 + 14, y, "NO", Ink.Red, Ink.Yellow)) { _modal = null; _hub.Play("SWTCHX"); }
        In.Pressed = false;
    }
}
