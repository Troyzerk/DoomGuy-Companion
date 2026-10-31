namespace DoomGuy.Doom;

/// <summary>Text colours. Doom's HUD font is red; the others are palette translations of it.</summary>
public enum Ink { Red, Gray, Yellow, Green, Blue, Gold, Dark, White, Black, Dim }

/// <summary>Everything DoomGuy draws or plays, read from the user's own IWAD.</summary>
public sealed class Assets
{
    public readonly WadFile Wad;
    public readonly uint[][] Palettes = new uint[14][];
    public readonly byte[][] Colormaps = new byte[34][];
    public readonly Font Font;
    public readonly Patch?[] BigNum = new Patch?[10];
    public readonly Patch?[] YellowNum = new Patch?[10];
    public readonly Patch?[] GrayNum = new Patch?[10];
    public readonly Patch? Percent, StBar, StArms, TitlePic;
    public readonly Patch?[] Keys = new Patch?[6];
    public readonly Patch?[] Faces = new Patch?[42];
    public readonly Patch?[] Skull = new Patch?[2];
    public readonly Patch? ThermL, ThermM, ThermR, ThermO;
    public readonly Patch? BrdrT, BrdrB, BrdrL, BrdrR, BrdrTL, BrdrTR, BrdrBL, BrdrBR;
    public readonly byte[]? BackFlat, PanelFlat;
    public readonly bool IsDoom2;
    private readonly Dictionary<Ink, byte[]> _ink = new();
    private readonly Dictionary<string, Patch?> _sprites = new(StringComparer.OrdinalIgnoreCase);

    // Face table layout, as in st_stuff.c.
    public const int FaceStride = 8, TurnRight = 3, TurnLeft = 4, Ouch = 5, EvilGrin = 6, Rampage = 7, God = 40, Dead = 41;

    /// <summary>The WAD the 42 face frames came from (a Doom WAD's Doomguy over Freedoom's art, or the art's own).</summary>
    public readonly WadFile FaceWad;

    /// <summary>
    /// faceWad: where the marine's face comes from when that isn't the art's own WAD, so Doomguy can sit on
    /// Freedoom's bar. Their base palettes are identical, so his pixels keep their colours.
    /// </summary>
    public Assets(WadFile wad, WadFile? faceWad = null)
    {
        Wad = wad;
        FaceWad = faceWad != null && faceWad.Has("STFST00") ? faceWad : wad;
        IsDoom2 = wad.Has("MAP01");

        var playpal = wad.Lump("PLAYPAL") ?? throw new InvalidDataException("The WAD has no PLAYPAL; it is not a Doom IWAD.");
        for (int p = 0; p < 14; p++)
        {
            Palettes[p] = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                int o = (p * 768 + i * 3) % playpal.Length;
                Palettes[p][i] = 0xFF000000u | ((uint)playpal[o] << 16) | ((uint)playpal[o + 1] << 8) | playpal[o + 2];
            }
        }
        var colormap = wad.Lump("COLORMAP");
        for (int m = 0; m < 34; m++)
        {
            Colormaps[m] = new byte[256];
            for (int i = 0; i < 256; i++)
                Colormaps[m][i] = colormap != null && colormap.Length >= (m + 1) * 256 ? colormap[m * 256 + i] : (byte)i;
        }

        Font = new Font(wad);
        for (int i = 0; i < 10; i++)
        {
            BigNum[i] = Patch.Parse(wad.Lump($"STTNUM{i}"));
            YellowNum[i] = Patch.Parse(wad.Lump($"STYSNUM{i}"));
            GrayNum[i] = Patch.Parse(wad.Lump($"STGNUM{i}"));
        }
        Percent = Patch.Parse(wad.Lump("STTPRCNT"));
        StBar = Patch.Parse(wad.Lump("STBAR"));
        StArms = Patch.Parse(wad.Lump("STARMS"));
        var title = wad.Lump("TITLEPIC");
        TitlePic = title != null && title.Length == 64000 ? Patch.FromRaw(title, 320, 200) : Patch.Parse(title);
        for (int i = 0; i < 6; i++) Keys[i] = Patch.Parse(wad.Lump($"STKEYS{i}"));
        var fw = FaceWad;
        for (int pain = 0; pain < 5; pain++)
        {
            int b = pain * FaceStride;
            for (int s = 0; s < 3; s++) Faces[b + s] = Patch.Parse(fw.Lump($"STFST{pain}{s}"));
            Faces[b + TurnRight] = Patch.Parse(fw.Lump($"STFTR{pain}0"));
            Faces[b + TurnLeft] = Patch.Parse(fw.Lump($"STFTL{pain}0"));
            Faces[b + Ouch] = Patch.Parse(fw.Lump($"STFOUCH{pain}"));
            Faces[b + EvilGrin] = Patch.Parse(fw.Lump($"STFEVL{pain}"));
            Faces[b + Rampage] = Patch.Parse(fw.Lump($"STFKILL{pain}"));
        }
        Faces[God] = Patch.Parse(fw.Lump("STFGOD0"));
        Faces[Dead] = Patch.Parse(fw.Lump("STFDEAD0"));
        Skull[0] = Patch.Parse(wad.Lump("M_SKULL1"));
        Skull[1] = Patch.Parse(wad.Lump("M_SKULL2"));
        ThermL = Patch.Parse(wad.Lump("M_THERML"));
        ThermM = Patch.Parse(wad.Lump("M_THERMM"));
        ThermR = Patch.Parse(wad.Lump("M_THERMR"));
        ThermO = Patch.Parse(wad.Lump("M_THERMO"));
        BrdrT = Patch.Parse(wad.Lump("BRDR_T")); BrdrB = Patch.Parse(wad.Lump("BRDR_B"));
        BrdrL = Patch.Parse(wad.Lump("BRDR_L")); BrdrR = Patch.Parse(wad.Lump("BRDR_R"));
        BrdrTL = Patch.Parse(wad.Lump("BRDR_TL")); BrdrTR = Patch.Parse(wad.Lump("BRDR_TR"));
        BrdrBL = Patch.Parse(wad.Lump("BRDR_BL")); BrdrBR = Patch.Parse(wad.Lump("BRDR_BR"));
        // The flat Doom fills the screen border with (FLOOR7_2 in Doom, GRNROCK in Doom II).
        BackFlat = wad.Lump(IsDoom2 ? "GRNROCK" : "FLOOR7_2") ?? wad.Lump("FLOOR7_2");
        PanelFlat = wad.Lump("FLOOR4_8") ?? BackFlat;

        BuildInks();
    }

    public byte[] Dark(int level) => Colormaps[Math.Clamp(level, 0, 31)];

    public byte[]? Ink(Ink ink) => ink == Doom.Ink.Red ? null : _ink[ink];

    public Patch? Sprite(string name)
    {
        if (!_sprites.TryGetValue(name, out var p)) _sprites[name] = p = Patch.Parse(Wad.Lump(name));
        return p;
    }

    public byte Nearest(int r, int g, int b)
    {
        var pal = Palettes[0];
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            int dr = (int)((pal[i] >> 16) & 255) - r, dg = (int)((pal[i] >> 8) & 255) - g, db = (int)(pal[i] & 255) - b;
            int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (d < bestD) { bestD = d; best = i; }
        }
        return (byte)best;
    }

    private void BuildInks()
    {
        var targets = new Dictionary<Ink, (int R, int G, int B)>
        {
            [Doom.Ink.Gray] = (215, 215, 215),
            [Doom.Ink.Yellow] = (255, 240, 70),
            [Doom.Ink.Green] = (110, 255, 110),
            [Doom.Ink.Blue] = (140, 160, 255),
            [Doom.Ink.Gold] = (255, 175, 45),
            [Doom.Ink.Dark] = (185, 75, 60),
            [Doom.Ink.White] = (255, 255, 255),
            [Doom.Ink.Dim] = (125, 118, 110),
        };
        var pal = Palettes[0];
        foreach (var (ink, t) in targets)
        {
            var map = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double v = Math.Max((pal[i] >> 16) & 255, Math.Max((pal[i] >> 8) & 255, pal[i] & 255)) / 255.0;
                map[i] = Nearest((int)(t.R * v), (int)(t.G * v), (int)(t.B * v));
            }
            _ink[ink] = map;
        }
        var black = new byte[256];
        Array.Fill(black, Nearest(0, 0, 0));
        _ink[Doom.Ink.Black] = black;
    }
}

/// <summary>The HUD font (STCFN033..095): upper case only, red, 8 pixels tall.</summary>
public sealed class Font
{
    private readonly Patch?[] _glyphs = new Patch?[96];
    public const int SpaceWidth = 4, LineHeight = 9;

    public Font(WadFile wad)
    {
        for (int c = 33; c < 96; c++) _glyphs[c] = Patch.Parse(wad.Lump($"STCFN{c:000}"));
    }

    public Patch? GlyphFor(char ch) => Glyph(ch);

    private Patch? Glyph(char ch)
    {
        ch = char.ToUpperInvariant(ch);
        ch = ch switch
        {
            '{' => '(', '}' => ')', '`' => '\'', '~' => '-', '|' => '!', '‘' or '’' => '\'',
            '“' or '”' => '"', '–' or '—' => '-', '…' => '.', _ => ch,
        };
        return ch > 32 && ch < 96 ? _glyphs[ch] : null;
    }

    /// <summary>tighten = pixels taken out between letters (1 makes them touch, for cramped spots).</summary>
    public int Width(string s, int tighten = 0)
    {
        int w = 0;
        foreach (var ch in s) w += (Glyph(ch)?.Width ?? SpaceWidth) - tighten;
        return s.Length > 0 ? w + tighten : 0;
    }

    public int Draw(Frame f, int x, int y, string s, byte[]? tr = null, int tighten = 0)
    {
        foreach (var ch in s)
        {
            var g = Glyph(ch);
            if (g == null) { x += SpaceWidth - tighten; continue; }
            f.Patch(g, x, y, tr);
            x += g.Width - tighten;
        }
        return x;
    }

    /// <summary>Text with a one-pixel black outline, for drawing over the desktop.</summary>
    public void DrawOutlined(Frame f, int x, int y, string s, byte[]? tr, byte black)
    {
        int cx = x;
        foreach (var ch in s)
        {
            var g = Glyph(ch);
            if (g == null) { cx += SpaceWidth; continue; }
            foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (1, 1) })
                for (int py = 0; py < g.Height; py++)
                    for (int px = 0; px < g.Width; px++)
                        if (g.Pix[py * g.Width + px] >= 0) f.Put(cx + px + dx - g.Left, y + py + dy - g.Top, black);
            cx += g.Width;
        }
        Draw(f, x, y, s, tr);
    }

    public string Fit(string s, int maxW)
    {
        if (Width(s) <= maxW) return s;
        while (s.Length > 0 && Width(s + "...") > maxW) s = s[..^1];
        return s.TrimEnd() + "...";
    }

    public List<string> Wrap(string s, int maxW, int maxLines = 99)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Length == 0 ? word : line + " " + word;
            if (Width(t) <= maxW || line.Length == 0) { line = t; continue; }
            lines.Add(line);
            line = word;
        }
        if (line.Length > 0) lines.Add(line);
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines[^1] = Fit(lines[^1] + " ...", maxW);
        }
        return lines.Select(l => Fit(l, maxW)).ToList();
    }
}
