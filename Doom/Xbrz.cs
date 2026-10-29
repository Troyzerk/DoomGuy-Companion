namespace DoomGuy.Doom;

/// <summary>
/// xBRZ 6x (Zenju's pixel-art scaler, as used by GZDoom's texture scaling and emulators), ported for
/// straight-alpha ARGB. It rounds off edges and diagonals while flat areas stay flat, so the art keeps
/// its hand-placed look at high resolution.
/// xBRZ: Copyright (C) Zenju (zenju AT gmx DOT de), GPL-3.0, https://sourceforge.net/projects/xbrz/
/// </summary>
public static class Xbrz
{
    public const int Factor = 6;
    private const double EqualColorTolerance = 30, CenterDirectionBias = 4, DominantDirectionThreshold = 3.6, SteepDirectionThreshold = 2.2;
    private const int None = 0, Normal = 1, Dominant = 2;

    // Rotated access into a 3x3 kernel stored a..i (row-major), for 0/90/180/270 degrees.
    private static readonly int[][] Rot =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8],
        [6, 3, 0, 7, 4, 1, 8, 5, 2],
        [8, 7, 6, 5, 4, 3, 2, 1, 0],
        [2, 5, 8, 1, 4, 7, 0, 3, 6],
    ];

    public static uint[] Scale(uint[] src, int w, int h)
    {
        const int s = Factor;
        int tw = w * s;
        var trg = new uint[tw * h * s];
        int pw = w + 1;
        var bf = new byte[pw * (h + 1)];
        var bg = new byte[pw * (h + 1)];
        var bj = new byte[pw * (h + 1)];
        var bk = new byte[pw * (h + 1)];

        uint P(int x, int y) => src[Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)];

        // Blend type of the four corners meeting at the centre of each 4x4 kernel (f g / j k).
        for (int y = -1; y < h; y++)
            for (int x = -1; x < w; x++)
            {
                uint a = P(x - 1, y - 1), b = P(x, y - 1), c = P(x + 1, y - 1);
                uint e = P(x - 1, y), f = P(x, y), g = P(x + 1, y), hh = P(x + 2, y);
                uint i = P(x - 1, y + 1), j = P(x, y + 1), k = P(x + 1, y + 1), l = P(x + 2, y + 1);
                uint n = P(x, y + 2), o = P(x + 1, y + 2);
                _ = a;
                int idx = (y + 1) * pw + x + 1;
                if ((f == g && j == k) || (f == j && g == k)) continue;
                double jg = Dist(i, f) + Dist(f, c) + Dist(n, k) + Dist(k, hh) + CenterDirectionBias * Dist(j, g);
                double fk = Dist(e, j) + Dist(j, o) + Dist(b, g) + Dist(g, l) + CenterDirectionBias * Dist(f, k);
                if (jg < fk)
                {
                    byte t = (byte)(DominantDirectionThreshold * jg < fk ? Dominant : Normal);
                    if (f != g && f != j) bf[idx] = t;
                    if (k != j && k != g) bk[idx] = t;
                }
                else if (fk < jg)
                {
                    byte t = (byte)(DominantDirectionThreshold * fk < jg ? Dominant : Normal);
                    if (j != f && j != k) bj[idx] = t;
                    if (g != f && g != k) bg[idx] = t;
                }
            }

        var ker = new uint[9];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint center = src[y * w + x];
                int ox = x * s, oy = y * s;
                for (int r = 0; r < s; r++)
                    Array.Fill(trg, center, (oy + r) * tw + ox, s);

                byte info = (byte)(bk[y * pw + x] | bj[y * pw + x + 1] << 2 | bf[(y + 1) * pw + x + 1] << 4 | bg[(y + 1) * pw + x] << 6);
                if (info == 0) continue;

                for (int ky = 0; ky < 3; ky++)
                    for (int kx = 0; kx < 3; kx++)
                        ker[ky * 3 + kx] = P(x + kx - 1, y + ky - 1);
                for (int rot = 0; rot < 4; rot++) BlendPixel(rot, ker, trg, tw, ox, oy, info);
            }
        return trg;
    }

    private static void BlendPixel(int rot, uint[] ker, uint[] trg, int tw, int ox, int oy, byte info)
    {
        int bl = rot == 0 ? info : ((info << (2 * rot)) | (info >> (8 - 2 * rot))) & 0xFF;
        if (((bl >> 4) & 3) < Normal) return;
        var m = Rot[rot];
        uint b = ker[m[1]], c = ker[m[2]], d = ker[m[3]], e = ker[m[4]], f = ker[m[5]], g = ker[m[6]], h = ker[m[7]], i = ker[m[8]];

        bool doLine;
        if (((bl >> 4) & 3) >= Dominant) doLine = true;
        else if (((bl >> 2) & 3) != None && !Eq(e, g)) doLine = false;
        else if (((bl >> 6) & 3) != None && !Eq(e, c)) doLine = false;
        else doLine = !(!Eq(e, i) && Eq(g, h) && Eq(h, i) && Eq(i, f) && Eq(f, c));

        uint px = Dist(e, f) <= Dist(e, h) ? f : h;
        var o = new Out(trg, tw, ox, oy, rot);
        if (doLine)
        {
            double fg = Dist(f, g), hc = Dist(h, c);
            bool shallow = SteepDirectionThreshold * fg <= hc && e != g && d != g;
            bool steep = SteepDirectionThreshold * hc <= fg && e != c && b != c;
            if (shallow && steep) LineSteepAndShallow(px, o);
            else if (shallow) LineShallow(px, o);
            else if (steep) LineSteep(px, o);
            else LineDiagonal(px, o);
        }
        else Corner(px, o);
    }

    /// <summary>The 6x6 output block, addressed in the rotated frame (MatrixRotation in xBRZ).</summary>
    private readonly struct Out(uint[] trg, int tw, int ox, int oy, int rot)
    {
        public ref uint Ref(int i, int j)
        {
            for (int r = 0; r < rot; r++) (i, j) = (Factor - 1 - j, i);
            return ref trg[(oy + i) * tw + ox + j];
        }
    }

    private const int S = Factor;

    private static void LineShallow(uint col, Out o)
    {
        Grad(ref o.Ref(S - 1, 0), col, 1, 4); Grad(ref o.Ref(S - 2, 2), col, 1, 4); Grad(ref o.Ref(S - 3, 4), col, 1, 4);
        Grad(ref o.Ref(S - 1, 1), col, 3, 4); Grad(ref o.Ref(S - 2, 3), col, 3, 4); Grad(ref o.Ref(S - 3, 5), col, 3, 4);
        o.Ref(S - 1, 2) = col; o.Ref(S - 1, 3) = col; o.Ref(S - 1, 4) = col; o.Ref(S - 1, 5) = col;
        o.Ref(S - 2, 4) = col; o.Ref(S - 2, 5) = col;
    }

    private static void LineSteep(uint col, Out o)
    {
        Grad(ref o.Ref(0, S - 1), col, 1, 4); Grad(ref o.Ref(2, S - 2), col, 1, 4); Grad(ref o.Ref(4, S - 3), col, 1, 4);
        Grad(ref o.Ref(1, S - 1), col, 3, 4); Grad(ref o.Ref(3, S - 2), col, 3, 4); Grad(ref o.Ref(5, S - 3), col, 3, 4);
        o.Ref(2, S - 1) = col; o.Ref(3, S - 1) = col; o.Ref(4, S - 1) = col; o.Ref(5, S - 1) = col;
        o.Ref(4, S - 2) = col; o.Ref(5, S - 2) = col;
    }

    private static void LineSteepAndShallow(uint col, Out o)
    {
        Grad(ref o.Ref(0, S - 1), col, 1, 4); Grad(ref o.Ref(2, S - 2), col, 1, 4);
        Grad(ref o.Ref(1, S - 1), col, 3, 4); Grad(ref o.Ref(3, S - 2), col, 3, 4);
        Grad(ref o.Ref(S - 1, 0), col, 1, 4); Grad(ref o.Ref(S - 2, 2), col, 1, 4);
        Grad(ref o.Ref(S - 1, 1), col, 3, 4); Grad(ref o.Ref(S - 2, 3), col, 3, 4);
        o.Ref(2, S - 1) = col; o.Ref(3, S - 1) = col; o.Ref(4, S - 1) = col; o.Ref(5, S - 1) = col;
        o.Ref(4, S - 2) = col; o.Ref(5, S - 2) = col;
        o.Ref(S - 1, 2) = col; o.Ref(S - 1, 3) = col;
    }

    private static void LineDiagonal(uint col, Out o)
    {
        Grad(ref o.Ref(S - 1, S / 2), col, 1, 2); Grad(ref o.Ref(S - 2, S / 2 + 1), col, 1, 2); Grad(ref o.Ref(S - 3, S / 2 + 2), col, 1, 2);
        o.Ref(S - 2, S - 1) = col; o.Ref(S - 1, S - 1) = col; o.Ref(S - 1, S - 2) = col;
    }

    private static void Corner(uint col, Out o)
    {
        Grad(ref o.Ref(5, 5), col, 97, 100);
        Grad(ref o.Ref(4, 5), col, 42, 100); Grad(ref o.Ref(5, 4), col, 42, 100);
        Grad(ref o.Ref(5, 3), col, 6, 100); Grad(ref o.Ref(3, 5), col, 6, 100);
    }

    /// <summary>Blends M/N of the front colour over the back, weighting by alpha.</summary>
    private static void Grad(ref uint back, uint front, int m, int n)
    {
        int wf = (int)(front >> 24) * m, wb = (int)(back >> 24) * (n - m), ws = wf + wb;
        if (ws == 0) { back = 0; return; }
        uint bk = back;
        uint Ch(int shift) => (uint)((((front >> shift) & 255) * wf + ((bk >> shift) & 255) * wb) / ws);
        back = ((uint)(ws / n) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    private static bool Eq(uint a, uint b) => Dist(a, b) < EqualColorTolerance;

    /// <summary>YCbCr (BT.2020) distance, alpha-aware, as in xBRZ's ColorDistanceARGB.</summary>
    public static double Dist(uint p1, uint p2)
    {
        if (p1 == p2) return 0;
        int rd = (int)((p1 >> 16) & 255) - (int)((p2 >> 16) & 255);
        int gd = (int)((p1 >> 8) & 255) - (int)((p2 >> 8) & 255);
        int bd = (int)(p1 & 255) - (int)(p2 & 255);
        const double kb = 0.0593, kr = 0.2627, kg = 1 - kb - kr;
        const double sb = 0.5 / (1 - kb), sr = 0.5 / (1 - kr);
        double y = kr * rd + kg * gd + kb * bd, cb = sb * (bd - y), cr = sr * (rd - y);
        double d = Math.Sqrt(y * y + cb * cb + cr * cr);
        double a1 = (p1 >> 24) / 255.0, a2 = (p2 >> 24) / 255.0;
        return a1 < a2 ? a1 * d + 255 * (a2 - a1) : a2 * d + 255 * (a1 - a2);
    }
}
