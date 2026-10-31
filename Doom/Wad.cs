using System.Buffers.Binary;
using System.Text;

namespace DoomGuy.Doom;

/// <summary>A Doom WAD: a flat directory of named lumps. Later lumps with the same name win, as in the engine.</summary>
public sealed class WadFile
{
    private readonly long _length;
    private readonly Dictionary<string, (int Pos, int Size)> _lumps = new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; }

    /// <summary>
    /// Only the directory is kept; lumps are read from disk when asked for (the art is all loaded at startup,
    /// sounds on first play), so the 12 MB file isn't held in memory for the app's lifetime.
    /// </summary>
    public WadFile(string path)
    {
        Path = path;
        using var fs = File.OpenRead(path);
        _length = fs.Length;
        var head = new byte[12];
        if (fs.Read(head, 0, 12) < 12) throw new InvalidDataException("Not a WAD file.");
        var id = Encoding.ASCII.GetString(head, 0, 4);
        if (id != "IWAD" && id != "PWAD") throw new InvalidDataException("Not a WAD file.");

        int count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        int dir = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
        if (count < 0 || dir < 12 || dir + (long)count * 16 > _length) throw new InvalidDataException("Damaged WAD directory.");
        var d = new byte[count * 16];
        fs.Position = dir;
        fs.ReadExactly(d);
        for (int i = 0; i < count; i++)
        {
            int e = i * 16;
            int pos = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(e));
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(e + 4));
            var name = Encoding.ASCII.GetString(d, e + 8, 8).TrimEnd('\0');
            if (pos >= 0 && size >= 0 && pos + (long)size <= _length) _lumps[name] = (pos, size);
        }
    }

    public bool Has(string name) => _lumps.ContainsKey(name);

    public byte[]? Lump(string name)
    {
        if (!_lumps.TryGetValue(name, out var l)) return null;
        var data = new byte[l.Size];
        try
        {
            using var h = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.Read(h, data, l.Pos);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return data;
    }

    /// <summary>
    /// Freedoom's Phase 1 IWAD, shipped beside the exe so DoomGuy works without a copy of Doom: free,
    /// BSD-licensed replacement art and sounds with the same lump names (Freedoom\COPYING.txt).
    /// </summary>
    public static string? Bundled()
    {
        var p = System.IO.Path.Combine(AppContext.BaseDirectory, "Freedoom", "freedoom1.wad");
        return File.Exists(p) ? p : null;
    }

    public static bool IsBundled(string path) =>
        Bundled() is { } b && string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Looks for an original Doom IWAD in the usual Steam and GOG install folders.</summary>
    public static string? FindInstalled()
    {
        var libraries = new List<string>();
        foreach (var steam in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
        {
            libraries.Add(System.IO.Path.Combine(steam, "steamapps", "common"));
            var vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var line in File.ReadAllLines(vdf))
            {
                var t = line.Trim();
                if (!t.StartsWith("\"path\"")) continue;
                var parts = t.Split('"', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3) libraries.Add(System.IO.Path.Combine(parts[^1].Replace(@"\\", @"\"), "steamapps", "common"));
            }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            libraries.Add(System.IO.Path.Combine(drive.Name, "GOG Games"));

        var candidates = new[]
        {
            @"Ultimate Doom\base\DOOM.WAD",
            @"Ultimate Doom\rerelease\doom.wad",
            @"Ultimate Doom\base\doom2\DOOM2.WAD",
            @"Doom 2\base\DOOM2.WAD",
            @"DOOM II\base\DOOM2.WAD",
            @"DOOM 3 BFG Edition\base\wads\DOOM.WAD",
            @"DOOM The Ultimate\DOOM.WAD",
            @"DOOM II\DOOM2.WAD",
        };
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var c in candidates)
            {
                var p = System.IO.Path.Combine(lib, c);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}

/// <summary>A picture in Doom's column-post format, unpacked to palette indices (-1 = transparent).</summary>
public sealed class Patch
{
    public int Width, Height, Left, Top;
    public short[] Pix = [];

    public static Patch? Parse(byte[]? d)
    {
        if (d == null || d.Length < 8) return null;
        int w = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(0));
        int h = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(2));
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || d.Length < 8 + w * 4) return null;
        var p = new Patch
        {
            Width = w,
            Height = h,
            Left = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(4)),
            Top = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(6)),
            Pix = new short[w * h],
        };
        Array.Fill(p.Pix, (short)-1);
        for (int x = 0; x < w; x++)
        {
            int ofs = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(8 + x * 4));
            while (ofs >= 0 && ofs + 2 < d.Length && d[ofs] != 0xFF)
            {
                int top = d[ofs], len = d[ofs + 1];
                for (int i = 0; i < len; i++)
                {
                    int y = top + i, src = ofs + 3 + i;
                    if (y < h && src < d.Length) p.Pix[y * w + x] = d[src];
                }
                ofs += len + 4;
            }
        }
        return p;
    }

    /// <summary>Wraps a raw 320x200 picture (or any raw block) as an opaque patch.</summary>
    public static Patch? FromRaw(byte[]? d, int w, int h)
    {
        if (d == null || d.Length < w * h) return null;
        var p = new Patch { Width = w, Height = h, Pix = new short[w * h] };
        for (int i = 0; i < w * h; i++) p.Pix[i] = d[i];
        return p;
    }
}
