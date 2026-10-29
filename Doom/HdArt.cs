namespace DoomGuy.Doom;

/// <summary>Pixel helpers shared by the HD pieces (premultiplied BGRA throughout).</summary>
public static class HdUtil
{
    public static uint Premultiply(uint c)
    {
        uint a = c >> 24;
        if (a == 255) return c;
        if (a == 0) return 0;
        uint M(int shift) => (((c >> shift) & 255) * a + 127) / 255;
        return a << 24 | M(16) << 16 | M(8) << 8 | M(0);
    }

    /// <summary>Box filter with fractional coverage, on premultiplied pixels.</summary>
    public static uint[] Resample(uint[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new uint[dw * dh];
        double fx = (double)sw / dw, fy = (double)sh / dh;
        for (int v = 0; v < dh; v++)
        {
            double y0 = v * fy, y1 = y0 + fy;
            for (int u = 0; u < dw; u++)
            {
                double x0 = u * fx, x1 = x0 + fx;
                double a = 0, r = 0, g = 0, b = 0, area = 0;
                for (int sy = (int)y0; sy < Math.Min(sh, (int)Math.Ceiling(y1)); sy++)
                {
                    double wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    for (int sx = (int)x0; sx < Math.Min(sw, (int)Math.Ceiling(x1)); sx++)
                    {
                        double wgt = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        uint c = src[sy * sw + sx];
                        a += (c >> 24) * wgt; r += ((c >> 16) & 255) * wgt; g += ((c >> 8) & 255) * wgt; b += (c & 255) * wgt;
                        area += wgt;
                    }
                }
                if (area <= 0) continue;
                uint A = (uint)Math.Round(a / area);
                uint C(double s) => (uint)Math.Min(A, Math.Round(s / area));
                dst[v * dw + u] = A << 24 | C(r) << 16 | C(g) << 8 | C(b);
            }
        }
        return dst;
    }

    /// <summary>Two passes of a separable box blur on an alpha map (a cheap Gaussian-ish glow).</summary>
    public static byte[] Blur(byte[] a, int w, int h, int radius)
    {
        var tmp = new float[w * h];
        var cur = a.Select(x => (float)x).ToArray();
        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0; int n = 0;
                    for (int k = -radius; k <= radius; k++) { int xx = x + k; if (xx >= 0 && xx < w) { s += cur[y * w + xx]; n++; } }
                    tmp[y * w + x] = s / n;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0; int n = 0;
                    for (int k = -radius; k <= radius; k++) { int yy = y + k; if (yy >= 0 && yy < h) { s += tmp[yy * w + x]; n++; } }
                    cur[y * w + x] = s / n;
                }
        }
        return cur.Select(x => (byte)Math.Clamp(x, 0, 255)).ToArray();
    }
}

/// <summary>An upscaled picture: <see cref="Pad"/> pixels of transparent margin around the art.</summary>
public sealed class HdImage
{
    public int W, H, Pad, Left, Top;
    public uint[] Px = [];
}

/// <summary>Doom's pictures at <see cref="HdCanvas.K"/>x: xBRZ 6x, averaged down. Cached per picture and ink.</summary>
public sealed class HdArt
{
    private const int K = HdCanvas.K;
    private readonly Assets _a;
    private readonly Dictionary<(Patch, byte[]?), HdImage> _cache = new();
    private HdImage? _title;
    private bool _titleStarted;

    public HdArt(Assets a) { _a = a; }

    public (int Count, long Bytes) CacheStats => (_cache.Count, _cache.Values.Sum(i => (long)i.Px.Length * 4) + (_title?.Px.Length ?? 0) * 4L);

    public HdImage? Of(Patch? p, byte[]? tr = null, bool pad = true)
    {
        if (p == null) return null;
        if (_cache.TryGetValue((p, tr), out var img)) return img;
        int m = pad ? 1 : 0, w = p.Width + 2 * m, h = p.Height + 2 * m;
        var argb = new uint[w * h];
        var pal = _a.Palettes[0];
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
            {
                short v = p.Pix[y * p.Width + x];
                if (v >= 0) argb[(y + m) * w + x + m] = pal[tr == null ? v : tr[v]];
            }
        img = Upscale(argb, w, h);
        img.Pad = m * K;
        img.Left = p.Left;
        img.Top = p.Top;
        return _cache[(p, tr)] = img;
    }

    public HdImage? Sprite(string name) => Of(_a.Sprite(name));

    /// <summary>The title picture, upscaled in the background (null until ready).</summary>
    public HdImage? Title
    {
        get
        {
            if (!_titleStarted && _a.TitlePic != null)
            {
                _titleStarted = true;
                var p = _a.TitlePic;
                Task.Run(() =>
                {
                    var argb = new uint[p.Width * p.Height];
                    for (int i = 0; i < argb.Length; i++) argb[i] = p.Pix[i] < 0 ? 0 : _a.Palettes[0][p.Pix[i]];
                    _title = Upscale(argb, p.Width, p.Height);
                });
            }
            return _title;
        }
    }

    public static HdImage Upscale(uint[] argb, int w, int h)
    {
        var big = Xbrz.Scale(argb, w, h);
        for (int i = 0; i < big.Length; i++) big[i] = HdUtil.Premultiply(big[i]);
        return new HdImage { W = w * K, H = h * K, Px = HdUtil.Resample(big, w * Xbrz.Factor, h * Xbrz.Factor, w * K, h * K) };
    }
}

/// <summary>
/// A full-colour drawing surface at K pixels per screen pixel. Layout stays in Doom's 320x200 units
/// (so the screens don't change), drawing happens in real pixels.
/// </summary>
public sealed class HdCanvas
{
    public const int K = 4;
    public readonly int W, H;
    public readonly uint[] Px;
    private uint[]? _old;
    private int _cx0, _cy0, _cx1, _cy1;

    /// <summary>k = pixels per unit; only the 320x200 canvases use the unit helpers, which assume <see cref="K"/>.</summary>
    public HdCanvas(int unitsW, int unitsH, int k = K)
    {
        W = unitsW * k;
        H = unitsH * k;
        Px = new uint[W * H];
        ResetClip();
    }

    public void ResetClip() { _cx0 = 0; _cy0 = 0; _cx1 = W; _cy1 = H; }

    public void ClipUnits(int x, int y, int w, int h)
    {
        _cx0 = Math.Max(0, x * K); _cy0 = Math.Max(0, y * K);
        _cx1 = Math.Min(W, (x + w) * K); _cy1 = Math.Min(H, (y + h) * K);
    }

    /// <summary>Straight ARGB (0xAARRGGBB) to premultiplied.</summary>
    public static uint Pre(uint argb) => HdUtil.Premultiply(argb);

    public static uint Rgba(int r, int g, int b, double a = 1) => (uint)Math.Clamp((int)(a * 255 + 0.5), 0, 255) << 24 | (uint)r << 16 | (uint)g << 8 | (uint)b;

    private static uint Over(uint s, uint d)
    {
        uint sa = s >> 24;
        if (sa == 255) return s;
        if (sa == 0) return d;
        uint inv = 255 - sa;
        uint Ch(int sh) => ((s >> sh) & 255) + (((d >> sh) & 255) * inv + 127) / 255;
        return Ch(24) << 24 | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    /// <summary>Fills a pixel rectangle with a straight-alpha colour.</summary>
    public void Fill(int x, int y, int w, int h, uint argb)
    {
        uint s = Pre(argb);
        int x0 = Math.Max(x, _cx0), y0 = Math.Max(y, _cy0), x1 = Math.Min(x + w, _cx1), y1 = Math.Min(y + h, _cy1);
        if (x0 >= x1 || y0 >= y1 || s >> 24 == 0) return;
        for (int yy = y0; yy < y1; yy++)
        {
            int row = yy * W;
            if (s >> 24 == 255) Array.Fill(Px, s, row + x0, x1 - x0);
            else for (int xx = x0; xx < x1; xx++) Px[row + xx] = Over(s, Px[row + xx]);
        }
    }

    public void FillUnits(int x, int y, int w, int h, uint argb) => Fill(x * K, y * K, w * K, h * K, argb);

    public void GradientV(int x, int y, int w, int h, uint top, uint bottom)
    {
        for (int i = 0; i < h; i++) Fill(x, y + i, w, 1, Lerp(top, bottom, h <= 1 ? 0 : i / (double)(h - 1)));
    }

    public void GradientH(int x, int y, int w, int h, uint left, uint right)
    {
        for (int i = 0; i < w; i++) Fill(x + i, y, 1, h, Lerp(left, right, w <= 1 ? 0 : i / (double)(w - 1)));
    }

    public static uint Lerp(uint a, uint b, double t)
    {
        uint C(int sh) => (uint)Math.Round(((a >> sh) & 255) * (1 - t) + ((b >> sh) & 255) * t);
        return C(24) << 24 | C(16) << 16 | C(8) << 8 | C(0);
    }

    /// <summary>A panel with the top-left and bottom-right corners cut off at 45 degrees, and a border.</summary>
    public void Chamfer(int x, int y, int w, int h, int cut, uint fill, uint border, int thick)
    {
        (int, int) Span(int rx, int ry, int rw, int rh, int c, int py) =>
            (rx + Math.Max(0, c - (py - ry)), rx + rw - Math.Max(0, c - (ry + rh - 1 - py)));
        int ix = x + thick, iy = y + thick, iw = w - 2 * thick, ih = h - 2 * thick, ic = Math.Max(0, cut - thick / 2);
        for (int py = y; py < y + h; py++)
        {
            var (lo, ro) = Span(x, y, w, h, cut, py);
            if (py < iy || py >= iy + ih) { Fill(lo, py, ro - lo, 1, border); continue; }
            var (li, ri) = Span(ix, iy, iw, ih, ic, py);
            Fill(lo, py, li - lo, 1, border);
            Fill(ri, py, ro - ri, 1, border);
            Fill(li, py, ri - li, 1, fill);
        }
    }

    public void Blit(uint[] src, int sw, int sh, int dx, int dy)
    {
        int x0 = Math.Max(dx, _cx0), y0 = Math.Max(dy, _cy0), x1 = Math.Min(dx + sw, _cx1), y1 = Math.Min(dy + sh, _cy1);
        for (int y = y0; y < y1; y++)
        {
            int srow = (y - dy) * sw - dx, drow = y * W;
            for (int x = x0; x < x1; x++)
            {
                uint s = src[srow + x];
                if (s >> 24 != 0) Px[drow + x] = Over(s, Px[drow + x]);
            }
        }
    }

    /// <summary>An alpha map painted in one colour (glows, shadows).</summary>
    public void BlitAlpha(byte[] a, int sw, int sh, int dx, int dy, uint argb)
    {
        int x0 = Math.Max(dx, _cx0), y0 = Math.Max(dy, _cy0), x1 = Math.Min(dx + sw, _cx1), y1 = Math.Min(dy + sh, _cy1);
        uint ca = argb >> 24;
        for (int y = y0; y < y1; y++)
        {
            int srow = (y - dy) * sw - dx, drow = y * W;
            for (int x = x0; x < x1; x++)
            {
                uint al = a[srow + x] * ca / 255;
                if (al == 0) continue;
                Px[drow + x] = Over(Pre(al << 24 | (argb & 0xFFFFFF)), Px[drow + x]);
            }
        }
    }

    /// <summary>Draws an upscaled picture at a unit position, honouring its patch offsets unless told not to.</summary>
    public void Image(HdImage? img, int ux, int uy, bool offsets = true)
    {
        if (img == null) return;
        int px = (offsets ? ux - img.Left : ux) * K - img.Pad, py = (offsets ? uy - img.Top : uy) * K - img.Pad;
        Blit(img.Px, img.W, img.H, px, py);
    }

    public void CopyFrom(uint[] src) => Array.Copy(src, Px, Px.Length);

    public void Snapshot()
    {
        _old ??= new uint[Px.Length];
        Array.Copy(Px, _old, Px.Length);
    }

    /// <summary>Doom's screen melt: columns of the old screen slide down (offsets in screen units).</summary>
    public void ApplyMelt(int[] melt)
    {
        if (_old == null) return;
        int colW = W / melt.Length;
        for (int i = 0; i < melt.Length; i++)
        {
            int yy = Math.Max(0, melt[i]) * K;
            if (yy >= H) continue;
            int x0 = i * colW;
            for (int r = yy; r < H; r++) Array.Copy(_old, (r - yy) * W + x0, Px, r * W + x0, colW);
        }
    }
}
