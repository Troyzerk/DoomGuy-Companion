namespace DoomGuy.Doom;

/// <summary>
/// The HUD font upscaled the same way as the face (xBRZ 6x, then averaged down to <see cref="K"/> pixels per
/// font pixel), one bitmap per glyph and ink, with a soft drop shadow and an optional glow. Premultiplied BGRA.
/// </summary>
public sealed class HdFont
{
    public const int GlowPad = 6;

    public sealed class Glyph
    {
        public int W, H, OffX, OffY, Advance;
        public uint[] Px = [];
        public byte[] Shadow = [];
        /// <summary>Blurred alpha, <see cref="GlowPad"/> pixels bigger on every side.</summary>
        public byte[] Glow = [];
    }

    /// <summary>Output pixels per font pixel (4 = full size on the HD canvas, 3 = the small labels).</summary>
    public readonly int K;
    private readonly Assets _a;
    private readonly bool _pixel;
    private readonly Dictionary<(char, Ink), Glyph?> _cache = new();

    /// <summary>pixel = the original blocky letters (for HD MODE off), not scaled smooth.</summary>
    public HdFont(Assets a, int k = HdCanvas.K, bool pixel = false) { _a = a; K = k; _pixel = pixel; }

    public Glyph? Get(char ch, Ink ink)
    {
        if (_cache.TryGetValue((ch, ink), out var g)) return g;
        var p = _a.Font.GlyphFor(ch);
        _cache[(ch, ink)] = g = p == null ? null : Build(p, _a.Ink(ink));
        return g;
    }

    /// <summary>Width of a string in output pixels.</summary>
    public int Width(string s)
    {
        int w = 0;
        foreach (var ch in s) w += (_a.Font.GlyphFor(ch)?.Width ?? Font.SpaceWidth) * K;
        return w;
    }

    private Glyph Build(Patch p, byte[]? tr)
    {
        // A 1px transparent margin so the scaler can round the outline.
        int cw = p.Width + 2, ch = p.Height + 2;
        var small = new uint[cw * ch];
        var pal = _a.Palettes[0];
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
            {
                short v = p.Pix[y * p.Width + x];
                if (v >= 0) small[(y + 1) * cw + x + 1] = pal[tr == null ? v : tr[v]];
            }
        int w = cw * K, h = ch * K;
        uint[] px;
        if (_pixel)
        {
            px = new uint[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) px[y * w + x] = HdUtil.Premultiply(small[y / K * cw + x / K]);
        }
        else
        {
            var big = Xbrz.Scale(small, cw, ch);
            for (int i = 0; i < big.Length; i++) big[i] = HdUtil.Premultiply(big[i]);
            px = HdUtil.Resample(big, cw * Xbrz.Factor, ch * Xbrz.Factor, w, h);
        }
        var alpha = px.Select(c => (byte)(c >> 24)).ToArray();
        var shadow = alpha.Select(a => (byte)(a * 0.8)).ToArray();

        int gw = w + 2 * GlowPad, gh = h + 2 * GlowPad;
        var glow = new byte[gw * gh];
        for (int y = 0; y < h; y++) Array.Copy(alpha, y * w, glow, (y + GlowPad) * gw + GlowPad, w);
        glow = HdUtil.Blur(glow, gw, gh, 2);

        return new Glyph { W = w, H = h, OffX = (-1 - p.Left) * K, OffY = (-1 - p.Top) * K, Advance = p.Width * K, Px = px, Shadow = shadow, Glow = glow };
    }

    /// <summary>Draws text at a pixel position on the canvas: glow (optional), shadow, then the letters.</summary>
    public int Draw(HdCanvas c, int px, int py, string s, Ink ink, uint glow = 0, int shadow = 3)
    {
        foreach (var ch in s)
        {
            var g = Get(ch, ink);
            if (g == null) { px += Font.SpaceWidth * K; continue; }
            if (glow >> 24 != 0) c.BlitAlpha(g.Glow, g.W + 2 * GlowPad, g.H + 2 * GlowPad, px + g.OffX - GlowPad, py + g.OffY - GlowPad, glow);
            if (shadow > 0) c.BlitAlpha(g.Shadow, g.W, g.H, px + g.OffX + shadow, py + g.OffY + shadow, 0xFF000000);
            c.Blit(g.Px, g.W, g.H, px + g.OffX, py + g.OffY);
            px += g.Advance;
        }
        return px;
    }
}
