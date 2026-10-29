using System.IO;
using System.Text;
using DoomGuy.Doom;
using Xunit;

namespace DoomGuy.Tests;

/// <summary>The WAD reader, against small WADs built here (the real game data can't be in the repo).</summary>
public class WadTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files) try { File.Delete(f); } catch { }
    }

    private string Build(string id, params (string Name, byte[] Data)[] lumps)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes(id));
        w.Write(lumps.Length);
        w.Write(0); // directory offset, filled in below
        var pos = new List<int>();
        foreach (var (_, data) in lumps) { pos.Add((int)ms.Position); w.Write(data); }
        int dir = (int)ms.Position;
        for (int i = 0; i < lumps.Length; i++)
        {
            w.Write(pos[i]);
            w.Write(lumps[i].Data.Length);
            var name = new byte[8];
            Encoding.ASCII.GetBytes(lumps[i].Name).CopyTo(name, 0);
            w.Write(name);
        }
        ms.Position = 8;
        w.Write(dir);
        var path = Path.Combine(Path.GetTempPath(), $"doomguy-test-{Guid.NewGuid():N}.wad");
        File.WriteAllBytes(path, ms.ToArray());
        _files.Add(path);
        return path;
    }

    [Fact]
    public void Reads_lumps_by_name()
    {
        var wad = new WadFile(Build("IWAD", ("PLAYPAL", [1, 2, 3]), ("STBAR", [9, 8])));
        Assert.True(wad.Has("STBAR"));
        Assert.Equal(new byte[] { 1, 2, 3 }, wad.Lump("PLAYPAL"));
        Assert.Equal(new byte[] { 9, 8 }, wad.Lump("stbar"));
        Assert.Null(wad.Lump("MISSING"));
    }

    [Fact]
    public void A_later_lump_with_the_same_name_wins()
    {
        var wad = new WadFile(Build("PWAD", ("DSPISTOL", [1]), ("DSPISTOL", [2])));
        Assert.Equal(new byte[] { 2 }, wad.Lump("DSPISTOL"));
    }

    [Fact]
    public void Rejects_files_that_are_not_wads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"doomguy-test-{Guid.NewGuid():N}.wad");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("hello, not a wad"));
        _files.Add(path);
        Assert.Throws<InvalidDataException>(() => new WadFile(path));
    }

    [Fact]
    public void Rejects_a_directory_past_the_end_of_the_file()
    {
        var path = Build("IWAD", ("A", [1]));
        var bytes = File.ReadAllBytes(path);
        BitConverter.GetBytes(bytes.Length + 100).CopyTo(bytes, 8);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => new WadFile(path));
    }

    [Fact]
    public void Patch_unpacks_column_posts()
    {
        // A 2x3 patch: column 0 has a post of 2 pixels from row 1, column 1 is empty.
        byte[] d =
        [
            2, 0, 3, 0, 0, 0, 0, 0,       // width, height, left, top
            16, 0, 0, 0, 23, 0, 0, 0,     // column offsets
            1, 2, 0, 7, 8, 0, 0xFF,       // column 0: top 1, length 2, pad, 7, 8, pad, end
            0xFF,                         // column 1: end
        ];
        var p = Patch.Parse(d);
        Assert.NotNull(p);
        Assert.Equal((2, 3), (p!.Width, p.Height));
        Assert.Equal(new short[] { -1, -1, 7, -1, 8, -1 }, p.Pix);
    }
}
