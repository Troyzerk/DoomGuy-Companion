namespace DoomGuy.Doom;

/// <summary>
/// The marine's faces at 6x (xBRZ), on one shared canvas so every frame lines up, plus generated
/// in-between frames: motion between two faces is estimated on the original pixels and both frames
/// are warped towards each other, so glances, turns and grimaces move instead of cutting.
/// Pixels are premultiplied BGRA, ready for WPF.
/// </summary>
public sealed class HdFaces
{
    public const int S = Xbrz.Factor;
    public readonly int SmallW, SmallH, W, H;
    /// <summary>Where the face patch origin sits on the 1x canvas (the status bar draws it at 144,168).</summary>
    public readonly int OriginX, OriginY;

    private readonly uint[]?[] _small;
    private readonly uint[]?[] _hd;
    private readonly Dictionary<(int, int), uint[][]> _between = new();

    public HdFaces(Assets a)
    {
        var faces = a.Faces;
        int minX = 0, minY = 0, maxX = 1, maxY = 1;
        foreach (var p in faces)
        {
            if (p == null) continue;
            minX = Math.Min(minX, -p.Left); minY = Math.Min(minY, -p.Top);
            maxX = Math.Max(maxX, p.Width - p.Left); maxY = Math.Max(maxY, p.Height - p.Top);
        }
        // A transparent margin so the scaler has room to round the outline.
        OriginX = 2 - minX; OriginY = 2 - minY;
        SmallW = maxX - minX + 4; SmallH = maxY - minY + 4;
        W = SmallW * S; H = SmallH * S;

        _small = new uint[faces.Length][];
        _hd = new uint[faces.Length][];
        var pal = a.Palettes[0];
        for (int i = 0; i < faces.Length; i++)
        {
            var p = faces[i];
            if (p == null) continue;
            var c = new uint[SmallW * SmallH];
            int x0 = OriginX - p.Left, y0 = OriginY - p.Top;
            for (int y = 0; y < p.Height; y++)
                for (int x = 0; x < p.Width; x++)
                {
                    short v = p.Pix[y * p.Width + x];
                    if (v >= 0) c[(y0 + y) * SmallW + x0 + x] = pal[v];
                }
            _small[i] = c;
            var hd = Xbrz.Scale(c, SmallW, SmallH);
            for (int k = 0; k < hd.Length; k++) hd[k] = Premultiply(hd[k]);
            _hd[i] = hd;
        }
    }

    public uint[]? Face(int i) => i >= 0 && i < _hd.Length ? _hd[i] : null;

    public (int Faces, int Pairs, int PixelFaces, long Bytes) CacheStats =>
        (_hd.Count(f => f != null), _between.Count, _pixel.Count,
         (_hd.Count(f => f != null) + _pixel.Count + _between.Values.Sum(f => f.Length)) * (long)W * H * 4);

    private readonly Dictionary<int, uint[]> _pixel = new();

    /// <summary>The original face blown up with hard pixel edges, on the same canvas (for HD FACE off).</summary>
    public uint[]? PixelFace(int i)
    {
        if (i < 0 || i >= _small.Length || _small[i] is not { } small) return null;
        if (_pixel.TryGetValue(i, out var px)) return px;
        px = new uint[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                px[y * W + x] = Premultiply(small[y / S * SmallW + x / S]);
        return _pixel[i] = px;
    }

    /// <summary>
    /// In-betweens are about 1 MB a pair and a long day visits a hundred-odd pairs, so only the most recently
    /// used are kept; the idle glances are used constantly and stay, a rebuild costs ~30 ms.
    /// </summary>
    private const int KeepPairs = 32;
    private readonly Dictionary<(int, int), long> _used = new();
    private long _useClock;

    /// <summary>The frames strictly between a and b, in order from a to b.</summary>
    public uint[][] Between(int a, int b)
    {
        if (a == b || Face(a) == null || Face(b) == null) return [];
        var key = a < b ? (a, b) : (b, a);
        if (!_between.TryGetValue(key, out var frames))
        {
            if (_between.Count >= KeepPairs)
            {
                var oldest = _used.MinBy(kv => kv.Value).Key;
                _between.Remove(oldest);
                _used.Remove(oldest);
            }
            _between[key] = frames = Build(key.Item1, key.Item2);
        }
        _used[key] = ++_useClock;
        return a < b ? frames : frames.Reverse().ToArray();
    }

    private uint[][] Build(int a, int b)
    {
        var (abx, aby, abMax, shift) = Flow(_small[a]!, _small[b]!);
        var (bax, bay, baMax, _) = Flow(_small[b]!, _small[a]!);
        // A head that moves as a whole (a turn) is really a rotation no warp can line up, so hand over
        // in the middle frame only; small changes (glances, expressions, blood) cross-fade throughout.
        bool turn = shift >= 2 || IsTurn(a) != IsTurn(b);
        float move = Math.Max(abMax, baMax);
        // Small glances get three in-betweens, a turned head or a new expression up to five.
        int n = Math.Clamp(2 + (int)Math.Ceiling(move), 3, 5);
        var A = _hd[a]!; var B = _hd[b]!;
        var frames = new uint[n][];
        for (int f = 0; f < n; f++)
        {
            float t = (f + 1f) / (n + 1f);
            // Positions move linearly; the colour hand-over is eased so the two frames overlap less.
            float wb = turn ? Smooth(Math.Clamp((t - 0.35f) / 0.3f, 0, 1)) : Smooth(t);
            var o = new uint[W * H];
            for (int y = 0; y < H; y++)
            {
                float sy = (y + 0.5f) / S - 0.5f;
                for (int x = 0; x < W; x++)
                {
                    float sx = (x + 0.5f) / S - 0.5f;
                    float ux = Bilinear(abx, sx, sy) * S, uy = Bilinear(aby, sx, sy) * S;
                    float vx = Bilinear(bax, sx, sy) * S, vy = Bilinear(bay, sx, sy) * S;
                    var pa = Sample(A, x - t * ux, y - t * uy);
                    var pb = Sample(B, x - (1 - t) * vx, y - (1 - t) * vy);
                    o[y * W + x] = Pack(pa * (1 - wb) + pb * wb);
                }
            }
            frames[f] = o;
        }
        return frames;
    }

    /// <summary>
    /// Per-pixel motion from a to b on the 1x art: each pixel looks for where its 3x3 neighbourhood went
    /// (within 3 pixels, staying put when it can), then the field is smoothed so features move together.
    /// </summary>
    private static bool IsTurn(int face) => face < Assets.God && face % Assets.FaceStride is Assets.TurnLeft or Assets.TurnRight;

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    private (float[] X, float[] Y, float Max, int Shift) Flow(uint[] a, uint[] b)
    {
        int w = SmallW, h = SmallH;
        const int R = 3;
        var fx = new float[w * h];
        var fy = new float[w * h];
        var wt = new float[w * h];
        uint Get(uint[] img, int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0 : img[y * w + x];
        double Patch(int x, int y, int dx, int dy)
        {
            double s = 0;
            for (int qy = -1; qy <= 1; qy++)
                for (int qx = -1; qx <= 1; qx++)
                    s += Xbrz.Dist(Get(a, x + qx, y + qy), Get(b, x + qx + dx, y + qy + dy));
            return s;
        }

        // A turned head moves as a whole: find that shift first, then let each pixel search around it.
        int gx = 0, gy = 0;
        double gBest = double.MaxValue;
        for (int dy = -6; dy <= 6; dy++)
            for (int dx = -8; dx <= 8; dx++)
            {
                double s = 0;
                int count = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        uint pa = a[y * w + x], pb = Get(b, x + dx, y + dy);
                        if ((pa | pb) >> 24 == 0) continue;
                        s += Xbrz.Dist(pa, pb);
                        count++;
                    }
                s = count == 0 ? double.MaxValue : s / count + 2 * (Math.Abs(dx) + Math.Abs(dy));
                if (s < gBest) { gBest = s; gx = dx; gy = dy; }
            }

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (Get(a, x, y) >> 24 == 0) continue;
                wt[y * w + x] = 1;
                double best = Patch(x, y, 0, 0) + 10 * (Math.Abs(gx) + Math.Abs(gy));
                if (best < 1) continue;
                int bx = 0, by = 0;
                for (int dy = gy - R; dy <= gy + R; dy++)
                    for (int dx = gx - R; dx <= gx + R; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int off = Math.Min(Math.Abs(dx - gx) + Math.Abs(dy - gy), Math.Abs(dx) + Math.Abs(dy));
                        double c = Patch(x, y, dx, dy) + 10 * off;
                        if (c < best) { best = c; bx = dx; by = dy; }
                    }
                fx[y * w + x] = bx;
                fy[y * w + x] = by;
            }

        for (int pass = 0; pass < 2; pass++)
        {
            var nx = new float[w * h];
            var ny = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sx = 0, sy = 0, sw = 0;
                    for (int qy = -1; qy <= 1; qy++)
                        for (int qx = -1; qx <= 1; qx++)
                        {
                            int xx = x + qx, yy = y + qy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            float k = wt[yy * w + xx] * (qx == 0 && qy == 0 ? 2 : 1);
                            sx += fx[yy * w + xx] * k; sy += fy[yy * w + xx] * k; sw += k;
                        }
                    if (sw > 0) { nx[y * w + x] = sx / sw; ny[y * w + x] = sy / sw; }
                }
            fx = nx; fy = ny;
        }

        float max = 0;
        for (int i = 0; i < fx.Length; i++) max = Math.Max(max, MathF.Sqrt(fx[i] * fx[i] + fy[i] * fy[i]));
        return (fx, fy, max, Math.Abs(gx) + Math.Abs(gy));
    }

    private float Bilinear(float[] f, float x, float y)
    {
        int w = SmallW, h = SmallH;
        x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = x - x0, ty = y - y0;
        float top = f[y0 * w + x0] * (1 - tx) + f[y0 * w + x1] * tx;
        float bot = f[y1 * w + x0] * (1 - tx) + f[y1 * w + x1] * tx;
        return top * (1 - ty) + bot * ty;
    }

    private System.Numerics.Vector4 Sample(uint[] img, float x, float y)
    {
        x -= 0.5f; y -= 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float tx = x - x0, ty = y - y0;
        System.Numerics.Vector4 P(int xx, int yy) =>
            xx < 0 || yy < 0 || xx >= W || yy >= H ? default : Unpack(img[yy * W + xx]);
        var top = P(x0, y0) * (1 - tx) + P(x0 + 1, y0) * tx;
        var bot = P(x0, y0 + 1) * (1 - tx) + P(x0 + 1, y0 + 1) * tx;
        return top * (1 - ty) + bot * ty;
    }

    private static System.Numerics.Vector4 Unpack(uint c) =>
        new((c >> 16) & 255, (c >> 8) & 255, c & 255, c >> 24);

    private static uint Pack(System.Numerics.Vector4 v)
    {
        uint a = (uint)Math.Clamp(v.W + 0.5f, 0, 255);
        uint C(float f) => (uint)Math.Clamp(f + 0.5f, 0, a);
        return a << 24 | C(v.X) << 16 | C(v.Y) << 8 | C(v.Z);
    }

    private static uint Premultiply(uint c)
    {
        uint a = c >> 24;
        if (a == 255) return c;
        if (a == 0) return 0;
        uint M(int shift) => (((c >> shift) & 255) * a + 127) / 255;
        return a << 24 | M(16) << 16 | M(8) << 8 | M(0);
    }

    /// <summary>Doom's palette flashes on full-colour pixels: red when hurt, gold on a pickup.</summary>
    public static void Tint(uint[] src, uint[] dst, int palette)
    {
        (int R, int G, int B, float K) t = palette switch
        {
            >= 1 and <= 8 => (255, 0, 0, palette / 9f),
            >= 9 and <= 12 => (215, 186, 69, (palette - 8) / 8f),
            13 => (0, 255, 0, 0.125f),
            _ => (0, 0, 0, 0f),
        };
        if (t.K <= 0) { Array.Copy(src, dst, src.Length); return; }
        for (int i = 0; i < src.Length; i++)
        {
            uint c = src[i], a = c >> 24;
            if (a == 0) { dst[i] = 0; continue; }
            uint Ch(int shift, int target)
            {
                float v = (c >> shift) & 255;
                return (uint)Math.Clamp(v + (target * a / 255f - v) * t.K + 0.5f, 0, a);
            }
            dst[i] = a << 24 | Ch(16, t.R) << 16 | Ch(8, t.G) << 8 | Ch(0, t.B);
        }
    }
}

/// <summary>Plays the in-betweens whenever the face the game logic wants changes. One frame per tic.</summary>
public sealed class FaceAnimator
{
    private readonly HdFaces _hd;
    private int _target = -1;
    private uint[][] _seq = [];
    private int _pos;

    public FaceAnimator(HdFaces hd) { _hd = hd; }

    public uint[]? Current { get; private set; }
    public bool Changed { get; set; }

    public void Tick(int face)
    {
        if (face != _target)
        {
            _seq = _target < 0 ? [] : _hd.Between(_target, face);
            _pos = 0;
            _target = face;
            if (_seq.Length == 0) Show(_hd.Face(face));
        }
        if (_pos < _seq.Length) Show(_seq[_pos++]);
        else if (_pos == _seq.Length && _seq.Length > 0) { Show(_hd.Face(_target)); _pos++; }
    }

    private void Show(uint[]? f)
    {
        if (f == null || ReferenceEquals(f, Current)) return;
        Current = f;
        Changed = true;
    }
}
