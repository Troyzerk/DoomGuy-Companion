namespace DoomGuy.Doom;

/// <summary>
/// An 8-bit software screen, the way Doom draws: palette indices plus a coverage mask, turned into
/// colours only at the end through the current palette (so the red pain and gold pickup flashes are
/// just a palette swap, exactly like the game).
/// </summary>
public sealed class Frame
{
    public readonly int W, H;
    public readonly byte[] Idx;
    public readonly bool[] On;
    private int _cx0, _cy0, _cx1, _cy1;

    public Frame(int w, int h)
    {
        W = w; H = h;
        Idx = new byte[w * h];
        On = new bool[w * h];
        ResetClip();
    }

    public (int X0, int Y0, int X1, int Y1) Clip => (_cx0, _cy0, _cx1, _cy1);

    public void ResetClip() { _cx0 = 0; _cy0 = 0; _cx1 = W; _cy1 = H; }

    public void SetClip(int x, int y, int w, int h)
    {
        _cx0 = Math.Max(0, x); _cy0 = Math.Max(0, y);
        _cx1 = Math.Min(W, x + w); _cy1 = Math.Min(H, y + h);
    }

    public void Clear()
    {
        Array.Clear(Idx);
        Array.Clear(On);
    }

    public void CopyFrom(Frame f)
    {
        Array.Copy(f.Idx, Idx, Idx.Length);
        Array.Copy(f.On, On, On.Length);
    }

    public void Put(int x, int y, byte c)
    {
        if (x < _cx0 || y < _cy0 || x >= _cx1 || y >= _cy1) return;
        int i = y * W + x;
        Idx[i] = c; On[i] = true;
    }

    public void Erase(int x, int y)
    {
        if (x >= 0 && y >= 0 && x < W && y < H) On[y * W + x] = false;
    }

    public void Fill(int x, int y, int w, int h, byte c)
    {
        int x0 = Math.Max(x, _cx0), y0 = Math.Max(y, _cy0), x1 = Math.Min(x + w, _cx1), y1 = Math.Min(y + h, _cy1);
        for (int yy = y0; yy < y1; yy++)
        {
            int row = yy * W;
            for (int xx = x0; xx < x1; xx++) { Idx[row + xx] = c; On[row + xx] = true; }
        }
    }

    /// <summary>Draws a patch. With offsets on, the patch's own origin is honoured as V_DrawPatch does.</summary>
    public void Patch(Patch? p, int x, int y, byte[]? tr = null, bool offsets = true)
    {
        if (p == null) return;
        if (offsets) { x -= p.Left; y -= p.Top; }
        for (int py = 0; py < p.Height; py++)
        {
            int sy = y + py;
            if (sy < _cy0 || sy >= _cy1) continue;
            int row = sy * W, src = py * p.Width;
            for (int px = 0; px < p.Width; px++)
            {
                int sx = x + px;
                if (sx < _cx0 || sx >= _cx1) continue;
                short c = p.Pix[src + px];
                if (c < 0) continue;
                Idx[row + sx] = tr == null ? (byte)c : tr[c];
                On[row + sx] = true;
            }
        }
    }

    /// <summary>Draws a patch with every visible pixel set to one colour (shadows, outlines).</summary>
    public void Silhouette(Patch? p, int x, int y, byte c)
    {
        if (p == null) return;
        x -= p.Left; y -= p.Top;
        for (int py = 0; py < p.Height; py++)
            for (int px = 0; px < p.Width; px++)
                if (p.Pix[py * p.Width + px] >= 0) Put(x + px, y + py, c);
    }

    /// <summary>Tiles a 64x64 flat, aligned to the screen origin like Doom's border fill.</summary>
    public void Flat(byte[]? flat, int x, int y, int w, int h, byte[]? cmap = null)
    {
        if (flat == null || flat.Length < 4096) { Fill(x, y, w, h, 0); return; }
        int x0 = Math.Max(x, _cx0), y0 = Math.Max(y, _cy0), x1 = Math.Min(x + w, _cx1), y1 = Math.Min(y + h, _cy1);
        for (int yy = y0; yy < y1; yy++)
        {
            int row = yy * W, frow = (yy & 63) * 64;
            for (int xx = x0; xx < x1; xx++)
            {
                byte c = flat[frow + (xx & 63)];
                Idx[row + xx] = cmap == null ? c : cmap[c];
                On[row + xx] = true;
            }
        }
    }

    /// <summary>Remaps what is already drawn through a colormap (darken a region, as sector light does).</summary>
    public void Shade(int x, int y, int w, int h, byte[] cmap)
    {
        int x0 = Math.Max(x, _cx0), y0 = Math.Max(y, _cy0), x1 = Math.Min(x + w, _cx1), y1 = Math.Min(y + h, _cy1);
        for (int yy = y0; yy < y1; yy++)
        {
            int row = yy * W;
            for (int xx = x0; xx < x1; xx++) Idx[row + xx] = cmap[Idx[row + xx]];
        }
    }

    public void Resolve(uint[] pal, uint[] dst)
    {
        for (int i = 0; i < Idx.Length; i++) dst[i] = On[i] ? pal[Idx[i]] : 0u;
    }
}
