using DoomGuy.Core;
using DoomGuy.Doom;

namespace DoomGuy.UI;

/// <summary>
/// Doom's status bar at its original coordinates (st_stuff.c), with DoomGuy's numbers in the slots:
/// AMMO, HEALTH (and the face) and ARMOR = whatever the active profile wires them to (HUD tab; by default
/// tasks working, how much needs you, progress of the latest job), ARMS 2-7 = the first six tasks, keys = blue done / yellow reminder / red needs you,
/// and the four ammo rows on the right = working/tasks, done/started today, due/reminders, errors/messages.
/// </summary>
public static class StatusBar
{
    public const int Width = 320, Height = 32, FaceX = 143;

    public static void Draw(Frame f, Assets a, Hub hub, int ox, int oy, bool drawFace = true)
    {
        f.Patch(a.StBar, ox, oy);
        f.Patch(a.StArms, ox + 104, oy);
        DrawLabels(f, a, hub, ox, oy);

        BigNum(f, a, ox + 44, oy + 3, hub.Ammo);
        BigNum(f, a, ox + 90, oy + 3, hub.Health);
        f.Patch(a.Percent, ox + 90, oy + 3);

        bool blink = hub.Tic / 10 % 2 == 0;
        for (int i = 0; i < 6; i++)
        {
            var t = i < hub.Tasks.Count ? hub.Tasks[i] : null;
            bool lit = t != null && (t.State == TaskState.Working || ((t.State == TaskState.NeedsYou || t.Unseen) && blink));
            f.Patch(lit ? a.YellowNum[i + 2] : a.GrayNum[i + 2], ox + 111 + i % 3 * 12, oy + 4 + i / 3 * 10);
        }

        if (drawFace) DrawFace(f, a, hub, ox + FaceX, oy);

        BigNum(f, a, ox + 221, oy + 3, hub.ArmorValue);
        f.Patch(a.Percent, ox + 221, oy + 3);

        if (hub.UnseenDoneCount > 0) f.Patch(a.Keys[0], ox + 239, oy + 3);
        if (hub.FiringCount > 0 && blink) f.Patch(a.Keys[1], ox + 239, oy + 13);
        if (hub.NeedsYouCount > 0 && blink) f.Patch(a.Keys[2], ox + 239, oy + 23);

        var s = hub.State;
        Row(f, a, ox, oy + 5, hub.WorkingCount, hub.Tasks.Count);
        Row(f, a, ox, oy + 11, s.DoneToday, Math.Max(s.DoneToday, s.StartedToday));
        Row(f, a, ox, oy + 17, hub.FiringCount, hub.Reminders.Count);
        Row(f, a, ox, oy + 23, s.ErrorsToday, Math.Max(s.ErrorsToday, s.MessagesToday));
    }

    /// <summary>Where the AMMO / HEALTH / ARMOR words sit in STBAR (x, width; rows 22-30 of the bar).</summary>
    public static readonly (string Slot, int X, int W)[] LabelPlates = [("ammo", 3, 42), ("health", 51, 50), ("armor", 181, 52)];
    public const int PlateY = 22, PlateH = 9;

    /// <summary>
    /// Replaces a slot's baked-in word with what it tracks: a small recessed plate over the old label
    /// with the new name on it. Slots whose label already matches keep Doom's original lettering.
    /// </summary>
    private static void DrawLabels(Frame f, Assets a, Hub hub, int ox, int oy)
    {
        foreach (var (slot, x, w) in LabelPlates)
        {
            var label = hub.Gauges.Label(slot);
            if (label == Gauges.BakedLabel(slot)) continue;
            int px = ox + x, py = oy + PlateY;
            f.Fill(px, py, w, PlateH, a.Nearest(30, 30, 30));
            f.Fill(px, py, w, 1, a.Nearest(10, 10, 10));
            f.Fill(px, py, 1, PlateH, a.Nearest(10, 10, 10));
            f.Fill(px, py + PlateH - 1, w, 1, a.Nearest(95, 95, 95));
            f.Fill(px + w - 1, py, 1, PlateH, a.Nearest(95, 95, 95));
            // The plates are narrow: close the letter gaps, and if it still doesn't fit, squeeze the word
            // sideways rather than cutting letters off (the AMMO plate only has 40 pixels).
            int tight = a.Font.Width(label) <= w - 4 ? 0 : 1, room = w - 2, tw = a.Font.Width(label, tight);
            if (tw <= room) a.Font.Draw(f, px + (w - tw) / 2, py + 1, label, a.Ink(Ink.Gray), tight);
            else Squeezed(f, a, px + 1, py + 1, label, room);
        }
    }

    /// <summary>Text drawn tightened and then narrowed to 'width' by dropping evenly spaced columns.</summary>
    private static void Squeezed(Frame f, Assets a, int x, int y, string text, int width)
    {
        int tw = a.Font.Width(text, 1);
        var tmp = new Frame(tw, 8);
        a.Font.Draw(tmp, 0, 0, text, a.Ink(Ink.Gray), 1);
        for (int dx = 0; dx < width; dx++)
        {
            int sx = dx * tw / width;
            for (int dy = 0; dy < tmp.H; dy++)
                if (tmp.On[dy * tw + sx]) f.Put(x + dx, y + dy, tmp.Idx[dy * tw + sx]);
        }
    }

    public static void DrawFace(Frame f, Assets a, Hub hub, int x, int y) =>
        f.Patch(a.Faces[Math.Clamp(hub.FaceIndex, 0, a.Faces.Length - 1)], x, y);

    private static void Row(Frame f, Assets a, int ox, int y, int cur, int max)
    {
        SmallNum(f, a, ox + 288, y, cur);
        SmallNum(f, a, ox + 314, y, max);
    }

    // STlib_drawNum: right-aligned at x, digits drawn leftwards.
    private static void BigNum(Frame f, Assets a, int x, int y, int n) => Num(f, a.BigNum, x, y, n);
    private static void SmallNum(Frame f, Assets a, int x, int y, int n) => Num(f, a.YellowNum, x, y, n);

    private static void Num(Frame f, Patch?[] digits, int x, int y, int n)
    {
        n = Math.Clamp(n, 0, 999);
        int w = digits[0]?.Width ?? 4;
        if (n == 0) { f.Patch(digits[0], x - w, y); return; }
        for (int i = 0; n > 0 && i < 3; i++)
        {
            x -= w;
            f.Patch(digits[n % 10], x, y);
            n /= 10;
        }
    }
}
