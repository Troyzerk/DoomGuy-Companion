using DoomGuy.Core;
using DoomGuy.Doom;

namespace DoomGuy.UI;

/// <summary>
/// The minimized view: the status bar (or just the face) floating on the desktop, with Doom's HUD
/// messages above it. Hovering lists what is going on instead.
/// </summary>
public sealed class MiniScreen : Screen
{
    public const int W = 320, H = 32 + MessageRows * 9 + 4, MessageRows = 5;
    public const int BarY = H - 32;

    private readonly Hub _hub;
    private readonly byte _black;

    public Action? OpenMain;

    public MiniScreen(Assets assets, ScreenHost host, Hub hub) : base(assets, host)
    {
        _hub = hub;
        _black = A.Nearest(0, 0, 0);
        host.IsDragZone = (_, _) => true;
    }

    public bool FaceOnly => _hub.Settings.MiniMode == "face";

    public void Draw()
    {
        BeginFrame();
        F.Clear();
        // With the HD face on, the face itself is a separate smooth layer (see Stage); face-only mode is the head alone.
        if (FaceOnly) { if (!_hub.Settings.HdFace) StatusBar.DrawFace(F, A, _hub, StatusBar.FaceX, BarY); }
        else StatusBar.Draw(F, A, _hub, 0, BarY, drawFace: !_hub.Settings.HdFace);

        var lines = Host.MouseInside ? Summary() : _hub.Hud.TakeLast(MessageRows).Select(m => (m.Text, m.Ink)).ToList();
        int y = BarY - 10;
        for (int i = lines.Count - 1; i >= 0 && y >= 1; i--, y -= 9)
        {
            var (text, ink) = lines[i];
            text = Fit(text, W - 4);
            int x = FaceOnly ? Math.Clamp(160 - Width(text) / 2, 2, W - 2 - Width(text)) : 2;
            A.Font.DrawOutlined(F, x, y, text, A.Ink(ink), _black);
        }

        if (In.DoublePressed) OpenMain?.Invoke();
        In.EndTic();
    }

    private List<(string, Ink)> Summary()
    {
        var now = DateTime.Now;
        var lines = new List<(string, Ink)>();
        foreach (var r in _hub.Reminders.Where(r => r.Firing).Take(2))
            lines.Add(($"REMINDER: {r.Text.ToUpperInvariant()}", Ink.Yellow));
        foreach (var t in _hub.Tasks.Where(t => t.State != TaskState.Idle).OrderBy(t => t.State switch
                 {
                     TaskState.NeedsYou => 0, TaskState.Working => 1, _ => t.Unseen ? 2 : 3,
                 }).Take(MessageRows - 1))
        {
            var (state, ink) = t.State switch
            {
                TaskState.NeedsYou => ("NEEDS YOU", Ink.Red),
                TaskState.Working => ($"{TimeParse.Short(now - t.StartedAt)}" + (t.Percent >= 0 && !t.IsClaude ? $" {t.Percent}%" : ""), Ink.Yellow),
                TaskState.Done => ("DONE", t.Unseen ? Ink.Green : Ink.Gray),
                TaskState.Failed => ("FAILED", Ink.Red),
                _ => ("", Ink.Gray),
            };
            var detail = t.State == TaskState.Working || t.State == TaskState.NeedsYou ? t.Detail : t.Title;
            lines.Add(($"{t.Name} {state}: {detail.ToUpperInvariant()}", ink));
        }
        if (lines.Count == 0) lines.Add(($"HEALTH {_hub.Health}%. ALL QUIET.", Ink.Gray));
        if (lines.Count > MessageRows - 1) lines = lines.Take(MessageRows - 1).ToList();
        lines.Add(("DOUBLE-CLICK: OPEN.", Ink.Dark));
        return lines;
    }
}

/// <summary>
/// The head's speech bubble in face-only mode: finished tasks and notifications are announced here one at
/// a time and stay until clicked; with nothing to announce, hovering the head shows a short summary.
/// Wrapped rather than cut off, so it reads at a glance. With HD MODE on it is drawn like the main window
/// (the xBRZ'd Doom font on a dark panel, <see cref="HdCanvas.K"/> pixels per Doom pixel); off, the same
/// bubble in plain 8-bit pixels to match the pixel head. Sizes are in Doom pixels.
/// </summary>
public sealed class Bubble
{
    public const int W = 208, H = 84, Tail = 8;
    private const int PadX = 6, PadY = 5, Pitch = 11, SmallPitch = 9, FadeInTics = 6;
    private const int TextW = W - Tail - 2 * PadX;

    private static readonly uint Fill = HdCanvas.Rgba(17, 13, 11, 0.96), Edge = HdCanvas.Rgba(112, 86, 66, 0.9),
        Shade = HdCanvas.Rgba(0, 0, 0, 0.35);

    /// <summary>The announcement's colour for the accent strip; the inks' own targets (Assets.BuildInks).</summary>
    private static uint Accent(Ink ink) => ink switch
    {
        Ink.Yellow => HdCanvas.Rgba(255, 240, 70),
        Ink.Green => HdCanvas.Rgba(110, 255, 110),
        Ink.Blue => HdCanvas.Rgba(140, 160, 255),
        Ink.Gold => HdCanvas.Rgba(255, 175, 45),
        _ => HdCanvas.Rgba(225, 45, 25),
    };

    private readonly record struct Line(string Text, Ink Ink, bool Small = false);

    private readonly HdCanvas _hdCanvas = new(W, H), _pixelCanvas = new(W, H, 1);
    private readonly Assets A;
    private readonly Hub _hub;
    private readonly HdFont _font, _small, _pixelFont;
    private Announcement? _current;
    private int _tics;
    private string _drawn = "";

    public double Opacity { get; private set; }
    public bool Dirty { get; set; }
    public bool Pixel { get; private set; }
    public HdCanvas Canvas => Pixel ? _pixelCanvas : _hdCanvas;

    public Bubble(Assets assets, Hub hub)
    {
        A = assets;
        _hub = hub;
        _font = new HdFont(assets);
        _small = new HdFont(assets, 3);
        _pixelFont = new HdFont(assets, 1, pixel: true);
    }

    public bool Announcing => _current != null;

    public void Tick()
    {
        if (_current != null) _tics++;
        else if (_hub.Announcements.Count > 0)
        {
            _current = _hub.Announcements[0];
            _hub.Announcements.RemoveAt(0);
            _tics = 0;
        }
    }

    /// <summary>Clicked away; the next one in the queue takes its place on the next tic.</summary>
    public void Dismiss() => _current = null;

    /// <summary>
    /// A Claude session is waiting on you: the bubble says so ahead of everything else and can't be clicked
    /// away. It goes when the session moves on (answered, denied, stopped or closed).
    /// </summary>
    public bool Pinned { get; private set; }

    private Announcement? NeedsYouCard()
    {
        var waiting = _hub.Tasks.Where(t => t.State == TaskState.NeedsYou).ToList();
        if (waiting.Count == 0) return null;
        var t = waiting[0];
        return new Announcement { Headline = $"{t.Name} NEEDS YOU", Line2 = t.Detail, Line3 = t.Title, Ink = Ink.Red };
    }

    /// <summary>
    /// Redraws if the content changed. pointRight = the head is to the right of the bubble; anchorY = the
    /// head's middle in Doom pixels, where the tail points and the box centres; pixel = HD MODE off.
    /// </summary>
    public void Draw(bool hover, bool pointRight, int anchorY, bool pixel)
    {
        var pinned = NeedsYouCard();
        Pinned = pinned != null;
        var shown = pinned ?? _current;
        var lines = shown != null ? Lines(shown) : hover ? Summary() : [];
        Opacity = lines.Count == 0 ? 0 : shown != _current || _current == null ? 1 : Math.Min(1.0, (_tics + 1) / (double)FadeInTics);
        var key = $"{pointRight}|{anchorY}|{pixel}|{shown?.Ink}|" + string.Join("|", lines);
        if (key == _drawn) return;
        _drawn = key;
        Dirty = true;
        Pixel = pixel;
        var c = Canvas;
        Array.Clear(c.Px);
        if (lines.Count == 0) return;

        // Everything below is in canvas pixels: k per Doom pixel, and lines (thick) at least one pixel.
        int k = pixel ? 1 : HdCanvas.K, thick = pixel ? 1 : 2;
        HdFont FontOf(Line l) => pixel ? _pixelFont : l.Small ? _small : _font;
        int Px(Line l) => l.Small ? SmallPitch * FontOf(l).K : Pitch * k;
        int textW = lines.Max(l => FontOf(l).Width(l.Text));
        int last = lines[^1].Small ? (SmallPitch - 8) * FontOf(lines[^1]).K : (Pitch - 8) * k;
        int bw = textW + 2 * PadX * k, bh = lines.Sum(Px) - last + 2 * PadY * k, cut = 3 * k;
        int by = Math.Clamp(anchorY * k - bh / 2, 0, Math.Max(0, c.H - bh - k)), bx = pointRight ? (W - Tail) * k - bw : Tail * k;

        c.Chamfer(bx + k, by + k, bw, bh, cut, Shade, Shade, thick);
        c.Chamfer(bx, by, bw, bh, cut, Fill, Edge, thick);
        if (shown != null)
        {
            // The announcement's colour: a strip along the top and a fading wash behind the headline.
            var accent = Accent(shown.Ink);
            int headline = lines.TakeWhile(l => l.Ink == shown.Ink).Count();
            c.GradientH(bx + thick, by + thick, bw - 2 * thick, (PadY + Pitch * headline) * k, accent & 0xFFFFFF | 0x30000000, accent & 0xFFFFFF);
            c.Fill(bx + cut, by, Math.Min(32 * k, bw - 2 * cut), pixel ? 1 : 3, accent);
        }

        // The tail: a wedge from the box's side to the head, opening the border where it joins.
        int mid = Math.Clamp(anchorY * k, by + cut + 6 * k, Math.Max(by + cut + 6 * k, by + bh - cut - 6 * k)), tail = Tail * k, half0 = 5 * k;
        for (int i = -thick; i < tail; i++)
        {
            int half = i <= 0 ? half0 : half0 * (tail - i) / tail, x = pointRight ? bx + bw - 1 + i : bx - i;
            if (i > 0) c.Fill(x, mid - half, 1, 2 * half + 1, Edge);
            if (half > thick) c.Fill(x, mid - half + thick, 1, 2 * (half - thick) + 1, Fill);
        }

        int y = by + PadY * k;
        foreach (var l in lines)
        {
            var font = FontOf(l);
            // The click hint sits at the bottom right, out of the way of what is being said.
            int x = l.Small ? bx + bw - PadX * k - font.Width(l.Text) : bx + PadX * k;
            font.Draw(c, x, y, l.Text, l.Ink, shadow: pixel ? 1 : 2);
            y += Px(l);
        }
    }

    private List<Line> Lines(Announcement a)
    {
        var list = A.Font.Wrap(a.Headline, TextW, 2).Select(l => new Line(l, a.Ink)).ToList();
        // A notification's message gets four lines when there is no third part; a task keeps title and detail apart.
        if (a.Line3.Length == 0) list.AddRange(A.Font.Wrap(a.Line2, TextW, 4).Select(l => new Line(l, Ink.White)));
        else
        {
            list.AddRange(A.Font.Wrap(a.Line2, TextW, 2).Select(l => new Line(l, Ink.White)));
            list.Add(new Line(A.Font.Fit(a.Line3, TextW), Ink.Gray));
        }
        if (Pinned)
        {
            int others = _hub.NeedsYouCount - 1;
            var first = _hub.Tasks.First(t => t.State == TaskState.NeedsYou);
            var hint = CallerWindow.Alive(first.Window) ? "CLICK TO GO ANSWER IT" : "ANSWER IN CLAUDE TO CLEAR";
            list.Add(new Line(others > 0 ? $"{hint} ({others} MORE WAITING)" : hint, Ink.Dim, Small: true));
            return list;
        }
        int more = _hub.Announcements.Count;
        list.Add(new Line(more > 0 ? $"CLICK FOR NEXT ({more} MORE)" : "CLICK TO DISMISS", Ink.Dim, Small: true));
        return list;
    }

    private List<Line> Summary()
    {
        var now = DateTime.Now;
        var all = new List<Line>();
        foreach (var r in _hub.Reminders.Where(r => r.Firing)) all.Add(new($"REMINDER: {r.Text.ToUpperInvariant()}", Ink.Yellow));
        foreach (var t in _hub.Tasks.Where(t => t.State == TaskState.NeedsYou)) all.Add(new($"{t.Name} NEEDS YOU", Ink.Red));
        foreach (var t in _hub.Tasks.Where(t => t.State == TaskState.Working))
            all.Add(new($"{t.Name} {TimeParse.Short(now - t.StartedAt)}" + (!t.IsClaude && t.Percent >= 0 ? $" {t.Percent}%" : ""), Ink.Yellow));
        foreach (var t in _hub.Tasks.Where(t => t.Unseen)) all.Add(new($"{t.Name} {(t.State == TaskState.Failed ? "FAILED" : "DONE")}", t.State == TaskState.Failed ? Ink.Red : Ink.Green));
        if (all.Count == 0) all.Add(new($"ALL QUIET. HEALTH {_hub.Health}%", Ink.Gray));
        if (all.Count > 4) all = [.. all.Take(3), new($"+{all.Count - 3} MORE", Ink.Gray)];
        return all.Select(l => l with { Text = A.Font.Fit(l.Text, TextW) }).ToList();
    }
}

/// <summary>The reminder box: what is due, and DONE or snooze buttons.</summary>
public sealed class AlertScreen : Screen
{
    public const int W = 256, H = 96;
    private readonly Hub _hub;

    public AlertScreen(Assets assets, ScreenHost host, Hub hub) : base(assets, host)
    {
        _hub = hub;
        host.IsDragZone = (x, y) => !OverWidget(x, y);
    }

    public void Draw()
    {
        BeginFrame();
        var firing = _hub.Reminders.Where(r => r.Firing).ToList();
        F.Flat(A.BackFlat, 0, 0, W, H);
        Border(8, 8, W - 16, H - 16);
        F.Flat(A.PanelFlat, 8, 8, W - 16, H - 16, A.Dark(20));
        if (firing.Count == 0) { In.EndTic(); return; }

        var r = firing[0];
        var now = DateTime.Now;
        TextCenter(W / 2, 12, firing.Count > 1 ? $"REMINDER (1 OF {firing.Count})" : "REMINDER", Ink.Yellow);
        var lines = A.Font.Wrap(r.Text.ToUpperInvariant(), W - 28, 3);
        int y = 27 + (3 - lines.Count) * 4;
        foreach (var l in lines) { TextCenter(W / 2, y, l, Ink.Red); y += 9; }
        var late = now - r.Due;
        TextCenter(W / 2, 58, $"DUE {r.Due:HH:mm}" + (late.TotalMinutes >= 1 ? $" ({TimeParse.Short(late)} AGO)" : ""), Ink.Gray);

        int bx = 26;
        if (Button(bx, 72, "DONE", Ink.Green, Ink.Yellow)) _hub.Acknowledge(r);
        bx += Width("DONE") + 22;
        foreach (var (label, mins) in new[] { ("+5M", 5), ("+15M", 15), ("+1H", 60) })
        {
            if (Button(bx, 72, label, Ink.Red, Ink.Yellow)) _hub.Acknowledge(r, TimeSpan.FromMinutes(mins));
            bx += Width(label) + 22;
        }
        In.EndTic();
    }
}
