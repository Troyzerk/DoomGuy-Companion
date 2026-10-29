using System.Buffers.Binary;
using System.Media;

namespace DoomGuy.Doom;

/// <summary>Plays the WAD's DMX sound lumps (8-bit unsigned PCM) by wrapping them as WAV in memory.</summary>
public sealed class Sound
{
    private readonly WadFile _wad;
    private readonly Dictionary<string, byte[]?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastPlayed = new(StringComparer.OrdinalIgnoreCase);
    private SoundPlayer? _player;

    public bool Enabled = true;
    public int Volume = 6; // 0..8

    public Sound(WadFile wad) { _wad = wad; }

    public void Play(string lump, bool force = false)
    {
        if ((!Enabled || Volume <= 0) && !force) return;
        var name = lump.StartsWith("DS", StringComparison.OrdinalIgnoreCase) ? lump : "DS" + lump;
        var now = DateTime.UtcNow;
        if (_lastPlayed.TryGetValue(name, out var last) && (now - last).TotalMilliseconds < 120) return;
        _lastPlayed[name] = now;

        if (!_cache.TryGetValue(name, out var wav)) _cache[name] = wav = ToWav(_wad.Lump(name));
        if (wav == null) return;
        try
        {
            var data = Volume >= 8 ? wav : Scale(wav, Volume / 8.0);
            _player?.Stop();
            _player = new SoundPlayer(new MemoryStream(data));
            _player.Play();
        }
        catch { }
    }

    public bool Has(string lump) => _wad.Has(lump.StartsWith("DS", StringComparison.OrdinalIgnoreCase) ? lump : "DS" + lump);

    private static byte[]? ToWav(byte[]? d)
    {
        if (d == null || d.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(d) != 3) return null;
        int rate = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2));
        int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)), (uint)(d.Length - 8));
        // DMX pads each sound with 16 bytes at both ends.
        int start = 8, len = count;
        if (len > 32) { start += 16; len -= 32; }

        var wav = new byte[44 + len];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + len);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], rate);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 8);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], len);
        Array.Copy(d, start, wav, 44, len);
        return wav;
    }

    private static byte[] Scale(byte[] wav, double v)
    {
        var o = (byte[])wav.Clone();
        for (int i = 44; i < o.Length; i++) o[i] = (byte)Math.Clamp(128 + (int)((o[i] - 128) * v), 0, 255);
        return o;
    }
}
