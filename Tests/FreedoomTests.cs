using System.IO;
using DoomGuy.Doom;
using Xunit;

namespace DoomGuy.Tests;

/// <summary>Runs when Freedoom has been fetched (tools\get-freedoom.ps1, which CI always does); skipped otherwise.</summary>
public sealed class FreedoomFactAttribute : FactAttribute
{
    public static readonly string Wad = Path.Combine(AppContext.BaseDirectory, "Freedoom", "freedoom1.wad");

    public FreedoomFactAttribute()
    {
        if (!File.Exists(Wad)) Skip = "Freedoom isn't fetched (run tools\\get-freedoom.ps1).";
    }
}

/// <summary>The WAD shipped with DoomGuy has everything it draws and plays, so it runs without a copy of Doom.</summary>
public class FreedoomTests
{
    [FreedoomFact]
    public void Everything_DoomGuy_draws_loads_from_Freedoom()
    {
        var a = new Assets(new WadFile(FreedoomFactAttribute.Wad));
        Assert.False(a.IsDoom2);
        Assert.NotNull(a.StBar);
        Assert.NotNull(a.StArms);
        Assert.NotNull(a.TitlePic);
        Assert.All(a.Faces, f => Assert.NotNull(f));
        Assert.All(a.BigNum, n => Assert.NotNull(n));
        Assert.All(a.Keys, k => Assert.NotNull(k));
        Assert.NotNull(a.BackFlat);
        foreach (var ch in "ABCXYZ0129:%!?") Assert.NotNull(a.Font.GlyphFor(ch));
        foreach (var s in new[] { "BON1A0", "BON2A0", "RKEYA0", "BKEYA0", "RSKUA0", "CANDA0" }) Assert.NotNull(a.Sprite(s));
    }

    [FreedoomFact]
    public void The_sounds_DoomGuy_plays_are_there()
    {
        var wad = new WadFile(FreedoomFactAttribute.Wad);
        foreach (var s in new[] { "PLPAIN", "PLDETH", "ITEMUP", "WPNUP", "GETPOW", "OOF", "NOWAY", "TINK", "PISTOL", "SWTCHN", "SWTCHX", "PSTOP", "STNMOV" })
            Assert.True(wad.Has("DS" + s), $"DS{s} is missing");
    }

    [FreedoomFact]
    public void The_shipped_WAD_is_recognised_as_the_bundled_one() =>
        // The test output gets the same Freedoom folder the app does, so this is the path the app would use.
        Assert.True(WadFile.IsBundled(FreedoomFactAttribute.Wad));
}
