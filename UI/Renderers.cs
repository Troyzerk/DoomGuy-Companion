using DoomGuy.Core;
using DoomGuy.Doom;
using static DoomGuy.Doom.HdCanvas;

namespace DoomGuy.UI;

/// <summary>
/// How the main window looks. The screens lay out in Doom's 320x200 units and ask for parts by meaning
/// (panel, row highlight, progress bar...); each renderer draws them in its own style.
/// </summary>
public interface IRenderer
{
    bool Hd { get; }
    void BeginFrame();
    void SetClip(int x, int y, int w, int h);
    void ResetClip();
    /// <summary>The window background, tab strip and the content panel (with its header strip).</summary>
    void Background(int x, int y, int w, int h);
    void Separator(int x, int y, int w);
    void RowHighlight(int x, int y, int w, int h, bool selected);
    void TabMark(int x, int y, int w);
    void Field(int x, int y, int w, int h);
    void Progress(int x, int y, int w, int h, int pct, bool done);
    void Dialog(int x, int y, int w, int h);
    void Dim();
    int Text(int x, int y, string s, Ink ink, bool glow = false);
    void Sprite(string name, int cx, int bottom);
    void Skull(int x, int y, int frame);
    void Thermo(int x, int y, int value);
    void StatusBar(Hub hub);
    void TitlePic();
    void Snapshot();
    void ApplyMelt(int[] melt);
}

/// <summary>The 1993 look: the 8-bit screen, flats, the view border, hard pixels.</summary>
public sealed class ClassicRenderer : IRenderer
{
    private readonly Assets A;
    private readonly Frame F;
    private readonly Frame _old;
    private readonly byte _line, _yellow, _field, _barBack, _bar, _barDone;

    public ClassicRenderer(Assets a, Frame f)
    {
        A = a;
        F = f;
        _old = new Frame(f.W, f.H);
        _line = a.Nearest(70, 50, 30);
        _yellow = a.Nearest(255, 240, 70);
        _field = a.Nearest(34, 26, 18);
        _barBack = a.Nearest(35, 30, 25);
        _bar = a.Nearest(220, 30, 20);
        _barDone = a.Nearest(60, 200, 60);
    }

    public bool Hd => false;
    public void BeginFrame() => F.ResetClip();
    public void SetClip(int x, int y, int w, int h) => F.SetClip(x, y, w, h);
    public void ResetClip() => F.ResetClip();

    public void Background(int x, int y, int w, int h)
    {
        F.Flat(A.BackFlat, 0, 0, F.W, 168);
        for (int xx = x; xx < x + w; xx += 8)
        {
            F.Patch(A.BrdrT, xx, y - 8, offsets: false);
            F.Patch(A.BrdrB, xx, y + h, offsets: false);
        }
        for (int yy = y; yy < y + h; yy += 8)
        {
            F.Patch(A.BrdrL, x - 8, yy, offsets: false);
            F.Patch(A.BrdrR, x + w, yy, offsets: false);
        }
        F.Patch(A.BrdrTL, x - 8, y - 8, offsets: false);
        F.Patch(A.BrdrTR, x + w, y - 8, offsets: false);
        F.Patch(A.BrdrBL, x - 8, y + h, offsets: false);
        F.Patch(A.BrdrBR, x + w, y + h, offsets: false);
        F.Flat(A.PanelFlat, x, y, w, h, A.Dark(26));
    }

    public void Separator(int x, int y, int w) => F.Fill(x, y, w, 1, _line);
    public void RowHighlight(int x, int y, int w, int h, bool selected) => F.Flat(A.PanelFlat, x, y, w, h, A.Dark(selected ? 19 : 20));
    public void TabMark(int x, int y, int w) => F.Fill(x, y, w, 1, _yellow);
    public void Field(int x, int y, int w, int h) => F.Fill(x, y, w, h, _field);

    public void Progress(int x, int y, int w, int h, int pct, bool done)
    {
        F.Fill(x, y, w, h, _barBack);
        F.Fill(x, y, w * pct / 100, h, done ? _barDone : _bar);
    }

    public void Dialog(int x, int y, int w, int h) { }
    public void Dim() => F.Shade(0, 0, F.W, F.H, A.Dark(20));

    public int Text(int x, int y, string s, Ink ink, bool glow = false)
    {
        A.Font.Draw(F, x + 1, y + 1, s, A.Ink(Ink.Black));
        return A.Font.Draw(F, x, y, s, A.Ink(ink));
    }

    public void Sprite(string name, int cx, int bottom)
    {
        var p = A.Sprite(name);
        if (p != null) F.Patch(p, cx - p.Width / 2, bottom - p.Height, offsets: false);
    }

    public void Skull(int x, int y, int frame) => F.Patch(A.Skull[frame], x, y, offsets: false);

    public void Thermo(int x, int y, int value)
    {
        const int notches = 9;
        F.Patch(A.ThermL, x, y, offsets: false);
        for (int i = 0; i < notches; i++) F.Patch(A.ThermM, x + 8 + i * 8, y, offsets: false);
        F.Patch(A.ThermR, x + 8 + notches * 8, y, offsets: false);
        F.Patch(A.ThermO, x + 8 + Math.Clamp(value, 0, 8) * 8, y, offsets: false);
    }

    public void StatusBar(Hub hub) => UI.StatusBar.Draw(F, A, hub, 0, 168);
    public void TitlePic() => F.Patch(A.TitlePic, 0, 0, offsets: false);
    public void Snapshot() => _old.CopyFrom(F);

    public void ApplyMelt(int[] melt)
    {
        for (int i = 0; i < melt.Length; i++)
        {
            int yy = Math.Max(0, melt[i]);
            if (yy >= F.H) continue;
            for (int x = i * 2; x < i * 2 + 2; x++)
                for (int r = yy; r < F.H; r++)
                {
                    int d = r * F.W + x, s = (r - yy) * F.W + x;
                    F.Idx[d] = _old.Idx[s];
                    F.On[d] = _old.On[s];
                }
        }
    }
}

/// <summary>
/// The new look, at 4x: dark gunmetal panels with cut corners and red accents, glowing selection, smooth
/// bars, and Doom's own art (bar, digits, keys, sprites, skull, title) upscaled with xBRZ. Same font,
/// same face, same melt.
/// </summary>
public sealed class ModernRenderer : IRenderer
{
    private readonly Assets A;
    private readonly HdArt _art;
    private readonly HdFont _font, _small;
    private readonly HdFaces _faces;
    private readonly Func<uint[]?> _face;
    public readonly HdCanvas Canvas = new(320, 200);
    private uint[]? _base;
    private (int, int, int, int) _baseRect;
    private uint[]? _faceSrc, _faceK;

    private static readonly uint Accent = Rgba(225, 45, 25), AccentGlow = Rgba(235, 55, 30, 0.55);

    public ModernRenderer(Assets a, HdArt art, HdFaces faces, Func<uint[]?> face)
    {
        A = a;
        _art = art;
        _font = new HdFont(a);
        _small = new HdFont(a, 3);
        _faces = faces;
        _face = face;
    }

    public bool Hd => true;
    public void BeginFrame() => Canvas.ResetClip();
    public void SetClip(int x, int y, int w, int h) => Canvas.ClipUnits(x, y, w, h);
    public void ResetClip() => Canvas.ResetClip();

    public void Background(int x, int y, int w, int h)
    {
        if (_base == null || _baseRect != (x, y, w, h)) Bake(x, y, w, h);
        Array.Copy(_base!, Canvas.Px, _base!.Length);
    }

    /// <summary>Everything that never moves (backdrop, tab strip, panel) is drawn once and copied each frame.</summary>
    private void Bake(int x, int y, int w, int h)
    {
        var c = new HdCanvas(320, 168);
        c.GradientV(0, 0, c.W, c.H, Rgba(27, 20, 17), Rgba(9, 7, 6));

        // Doom's border rock, smooth and faint, for grit.
        var flat = A.BackFlat;
        if (flat != null)
        {
            var pal = A.Palettes[0];
            for (int py = 0; py < c.H; py++)
                for (int px = 0; px < c.W; px++)
                {
                    float fx = px / (float)K % 64, fy = py / (float)K % 64;
                    int x0 = (int)fx, y0 = (int)fy, x1 = (x0 + 1) & 63, y1 = (y0 + 1) & 63;
                    float tx = fx - x0, ty = fy - y0;
                    float Ch(int sh) =>
                        ((pal[flat[y0 * 64 + x0]] >> sh) & 255) * (1 - tx) * (1 - ty) + ((pal[flat[y0 * 64 + x1]] >> sh) & 255) * tx * (1 - ty) +
                        ((pal[flat[y1 * 64 + x0]] >> sh) & 255) * (1 - tx) * ty + ((pal[flat[y1 * 64 + x1]] >> sh) & 255) * tx * ty;
                    uint tex = 0x2A000000u | (uint)Ch(16) << 16 | (uint)Ch(8) << 8 | (uint)Ch(0);
                    int i = py * c.W + px;
                    c.Px[i] = Blend(Pre(tex), c.Px[i]);
                }
        }

        // Vignette.
        for (int py = 0; py < c.H; py++)
            for (int px = 0; px < c.W; px++)
            {
                double dx = (px - c.W / 2.0) / (c.W / 2.0), dy = (py - c.H / 2.0) / (c.H / 1.6);
                double v = Math.Clamp(1 - 0.45 * (dx * dx + dy * dy), 0.35, 1);
                uint p = c.Px[py * c.W + px];
                uint S(int sh) => (uint)(((p >> sh) & 255) * v);
                c.Px[py * c.W + px] = p & 0xFF000000 | S(16) << 16 | S(8) << 8 | S(0);
            }

        // Tab strip with a red rule under it.
        c.GradientV(0, 0, c.W, 14 * K, Rgba(44, 31, 25, 0.96), Rgba(24, 18, 15, 0.96));
        c.Fill(0, 14 * K - 2, c.W, 2, Rgba(150, 34, 20, 0.95));
        c.GradientV(0, 14 * K, c.W, 6, Rgba(200, 40, 25, 0.22), Rgba(200, 40, 25, 0));

        // The content panel: cut corners, thin metal edge, red accents on the cuts.
        int cut = 6 * K, x0p = (x - 3) * K, y0p = (y - 3) * K, wp = (w + 6) * K, hp = (h + 6) * K;
        c.Chamfer(x0p - 2, y0p - 2, wp + 4, hp + 4, cut + 1, Rgba(0, 0, 0, 0.35), Rgba(0, 0, 0, 0.35), 2);
        c.Chamfer(x0p, y0p, wp, hp, cut, Rgba(13, 10, 9, 0.92), Rgba(112, 86, 66, 0.8), 2);
        c.Fill(x0p + cut, y0p, 36 * K, 3, Accent);
        c.Fill(x0p + wp - cut - 36 * K, y0p + hp - 3, 36 * K, 3, Accent);
        c.GradientH(x * K, y * K, w * K, 11 * K, Rgba(150, 32, 20, 0.42), Rgba(150, 32, 20, 0));

        _base = c.Px;
        _baseRect = (x, y, w, h);
    }

    private static uint Blend(uint s, uint d)
    {
        uint sa = s >> 24, inv = 255 - sa;
        uint Ch(int sh) => ((s >> sh) & 255) + (((d >> sh) & 255) * inv + 127) / 255;
        return Ch(24) << 24 | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    public void Separator(int x, int y, int w)
    {
        int half = w * K / 2, py = y * K + 1;
        Canvas.GradientH(x * K, py, half, 2, Rgba(170, 125, 90, 0), Rgba(170, 125, 90, 0.5));
        Canvas.GradientH(x * K + half, py, half, 2, Rgba(170, 125, 90, 0.5), Rgba(170, 125, 90, 0));
    }

    public void RowHighlight(int x, int y, int w, int h, bool selected)
    {
        Canvas.GradientH(x * K, y * K, w * K, h * K, selected ? Rgba(255, 190, 110, 0.10) : Rgba(200, 40, 25, 0.18),
            selected ? Rgba(255, 190, 110, 0.02) : Rgba(200, 40, 25, 0.03));
        Canvas.Fill(x * K, y * K, 3, h * K, selected ? Rgba(240, 180, 60) : Accent);
    }

    public void TabMark(int x, int y, int w)
    {
        Canvas.GradientV(x * K, y * K - 10, w * K, 10, Rgba(235, 55, 30, 0), Rgba(235, 55, 30, 0.30));
        Canvas.Fill(x * K, y * K, w * K, 3, Accent);
    }

    public void Field(int x, int y, int w, int h)
    {
        Canvas.Fill(x * K, y * K, w * K, h * K, Rgba(0, 0, 0, 0.6));
        Canvas.Fill(x * K, (y + h) * K - 2, w * K, 2, Accent);
    }

    public void Progress(int x, int y, int w, int h, int pct, bool done)
    {
        int px = x * K, py = y * K, pw = w * K, ph = h * K, fw = pw * Math.Clamp(pct, 0, 100) / 100;
        Canvas.Fill(px, py, pw, ph, Rgba(255, 255, 255, 0.07));
        if (fw > 0)
        {
            Canvas.GradientH(px, py, fw, ph, done ? Rgba(30, 110, 40) : Rgba(150, 20, 10), done ? Rgba(100, 220, 90) : Rgba(255, 115, 40));
            Canvas.Fill(px, py, fw, 2, Rgba(255, 255, 255, 0.25));
            Canvas.GradientH(px + fw - Math.Min(fw, 12), py - 2, Math.Min(fw, 12), ph + 4, Rgba(255, 140, 60, 0), Rgba(255, 140, 60, 0.35));
        }
    }

    public void Dialog(int x, int y, int w, int h)
    {
        Canvas.Chamfer(x * K, y * K, w * K, h * K, 4 * K, Rgba(16, 12, 10, 0.96), Rgba(150, 40, 25, 0.9), 3);
        Canvas.Fill(x * K + 4 * K, y * K, 24 * K, 3, Accent);
    }

    public void Dim() => Canvas.Fill(0, 0, Canvas.W, Canvas.H, Rgba(0, 0, 0, 0.62));

    public int Text(int x, int y, string s, Ink ink, bool glow = false)
    {
        int end = _font.Draw(Canvas, x * K, y * K, s, ink, glow ? AccentGlow : 0);
        return (end + K - 1) / K;
    }

    public void Sprite(string name, int cx, int bottom)
    {
        var img = _art.Sprite(name);
        if (img != null) Canvas.Blit(img.Px, img.W, img.H, cx * K - img.W / 2, bottom * K - img.H + img.Pad);
    }

    public void Skull(int x, int y, int frame) => Canvas.Image(_art.Of(A.Skull[frame]), x, y, offsets: false);

    public void Thermo(int x, int y, int value)
    {
        const int notches = 9;
        Canvas.Image(_art.Of(A.ThermL), x, y, offsets: false);
        for (int i = 0; i < notches; i++) Canvas.Image(_art.Of(A.ThermM), x + 8 + i * 8, y, offsets: false);
        Canvas.Image(_art.Of(A.ThermR), x + 8 + notches * 8, y, offsets: false);
        Canvas.Image(_art.Of(A.ThermO), x + 8 + Math.Clamp(value, 0, 8) * 8, y, offsets: false);
    }

    /// <summary>Doom's bar, smooth: same layout as st_stuff.c, art upscaled, the HD face, labels on plates.</summary>
    public void StatusBar(Hub hub)
    {
        const int oy = 168;
        Canvas.GradientV(0, oy * K - 16, Canvas.W, 16, Rgba(0, 0, 0, 0), Rgba(0, 0, 0, 0.6));
        Canvas.Image(_art.Of(A.StBar, pad: false), 0, oy);
        Canvas.Image(_art.Of(A.StArms), 104, oy);

        foreach (var (slot, x, w) in UI.StatusBar.LabelPlates)
        {
            var label = hub.Gauges.Label(slot);
            if (label == Gauges.BakedLabel(slot)) continue;
            int px = x * K, py = (oy + UI.StatusBar.PlateY) * K, pw = w * K, ph = UI.StatusBar.PlateH * K;
            Canvas.Fill(px, py, pw, ph, Rgba(12, 11, 10));
            Canvas.Fill(px, py, pw, 3, Rgba(0, 0, 0, 0.6));
            Canvas.Fill(px, py + ph - 1, pw, 1, Rgba(255, 255, 255, 0.12));
            var text = label;
            while (text.Length > 1 && _small.Width(text) > pw - 8) text = text[..^1];
            _small.Draw(Canvas, px + (pw - _small.Width(text)) / 2, py + (ph - 7 * _small.K) / 2, text, Ink.Gray, shadow: 2);
        }

        Num(A.BigNum, 44, oy + 3, hub.Ammo);
        Num(A.BigNum, 90, oy + 3, hub.Health);
        Canvas.Image(_art.Of(A.Percent), 90, oy + 3);
        bool blink = hub.Tic / 10 % 2 == 0;
        for (int i = 0; i < 6; i++)
        {
            var t = i < hub.Tasks.Count ? hub.Tasks[i] : null;
            bool lit = t != null && (t.State == TaskState.Working || ((t.State == TaskState.NeedsYou || t.Unseen) && blink));
            Canvas.Image(_art.Of(lit ? A.YellowNum[i + 2] : A.GrayNum[i + 2]), 111 + i % 3 * 12, oy + 4 + i / 3 * 10);
        }

        // The face: the HD animation, scaled to this canvas once per new frame.
        var src = _face();
        if (src != null)
        {
            if (!ReferenceEquals(src, _faceSrc))
            {
                _faceSrc = src;
                _faceK = HdUtil.Resample(src, _faces.W, _faces.H, _faces.SmallW * K, _faces.SmallH * K);
            }
            Canvas.Blit(_faceK!, _faces.SmallW * K, _faces.SmallH * K, (UI.StatusBar.FaceX - _faces.OriginX) * K, (oy - _faces.OriginY) * K);
        }

        Num(A.BigNum, 221, oy + 3, hub.ArmorValue);
        Canvas.Image(_art.Of(A.Percent), 221, oy + 3);
        if (hub.UnseenDoneCount > 0) Canvas.Image(_art.Of(A.Keys[0]), 239, oy + 3);
        if (hub.FiringCount > 0 && blink) Canvas.Image(_art.Of(A.Keys[1]), 239, oy + 13);
        if (hub.NeedsYouCount > 0 && blink) Canvas.Image(_art.Of(A.Keys[2]), 239, oy + 23);
        var s = hub.State;
        Row(oy + 5, hub.WorkingCount, hub.Tasks.Count);
        Row(oy + 11, s.DoneToday, Math.Max(s.DoneToday, s.StartedToday));
        Row(oy + 17, hub.FiringCount, hub.Reminders.Count);
        Row(oy + 23, s.ErrorsToday, Math.Max(s.ErrorsToday, s.MessagesToday));
    }

    private void Row(int y, int cur, int max)
    {
        Num(A.YellowNum, 288, y, cur);
        Num(A.YellowNum, 314, y, max);
    }

    private void Num(Patch?[] digits, int x, int y, int n)
    {
        n = Math.Clamp(n, 0, 999);
        int w = digits[0]?.Width ?? 4;
        if (n == 0) { Canvas.Image(_art.Of(digits[0]), x - w, y); return; }
        for (int i = 0; n > 0 && i < 3; i++)
        {
            x -= w;
            Canvas.Image(_art.Of(digits[n % 10]), x, y);
            n /= 10;
        }
    }

    public void TitlePic()
    {
        var hd = _art.Title;
        if (hd != null && hd.Px.Length == Canvas.Px.Length) { Canvas.CopyFrom(hd.Px); return; }
        // Until the smooth one is ready: the original, hard pixels.
        var p = A.TitlePic;
        if (p == null) return;
        var pal = A.Palettes[0];
        for (int y = 0; y < Canvas.H; y++)
            for (int x = 0; x < Canvas.W; x++)
            {
                short v = p.Pix[Math.Min(p.Height - 1, y / K) * p.Width + Math.Min(p.Width - 1, x / K)];
                Canvas.Px[y * Canvas.W + x] = v < 0 ? 0xFF000000 : pal[v];
            }
    }

    public void Snapshot() => Canvas.Snapshot();
    public void ApplyMelt(int[] melt) => Canvas.ApplyMelt(melt);
}
