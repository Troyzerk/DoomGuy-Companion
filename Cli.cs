using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DoomGuy.Core;

namespace DoomGuy;

/// <summary>
/// DoomGuy.exe as a command: talks to the running app over its local API and exits.
///   notify "text" [--level success|warn|error] [--title T] [--sound NAME|none]
///   remind "stand up in 30m"
///   progress &lt;id&gt; &lt;percent|done|failed|remove&gt; [title]
///   hook                       (Claude Code hook: forwards stdin)
///   state | install-hooks | uninstall-hooks | help
/// </summary>
public static class Cli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    private static readonly string[] Commands = ["notify", "remind", "progress", "hook", "state", "install-hooks", "uninstall-hooks", "help", "dump-faces", "dump-patch", "gauge", "profile"];

    /// <summary>Test aid: contact sheets of the original faces, the 6x versions and the in-betweens.</summary>
    private static void DumpFaces(string dir)
    {
        var wad = Store.Load<Settings>("settings.json").WadPath ?? Doom.WadFile.FindInstalled() ?? Doom.WadFile.Bundled() ?? throw new FileNotFoundException("No WAD.");
        var a = new Doom.Assets(new Doom.WadFile(wad));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hd = new Doom.HdFaces(a);
        Console.WriteLine($"HD faces: {sw.ElapsedMilliseconds} ms, canvas {hd.W}x{hd.H}");
        Directory.CreateDirectory(dir);

        // Row 1: nearest-neighbour originals; row 2: xBRZ. Columns: the 8 faces of pain level 0, then god and dead.
        int[] faces = [0, 1, 2, 3, 4, 5, 6, 7, Doom.Assets.God, Doom.Assets.Dead, 16, 32];
        var compare = new uint[faces.Length * hd.W * hd.H * 2];
        int sheetW = faces.Length * hd.W;
        for (int c = 0; c < faces.Length; c++)
        {
            var big = hd.Face(faces[c])!;
            for (int y = 0; y < hd.H; y++)
                for (int x = 0; x < hd.W; x++)
                {
                    compare[(y + hd.H) * sheetW + c * hd.W + x] = big[y * hd.W + x];
                    compare[y * sheetW + c * hd.W + x] = NearestOriginal(a, hd, faces[c], x, y);
                }
        }
        Png(Path.Combine(dir, "faces_compare.png"), compare, sheetW, hd.H * 2);

        (int, int)[] pairs = [(0, 1), (0, 2), (1, 2), (0, Doom.Assets.TurnLeft), (0, Doom.Assets.TurnRight), (0, Doom.Assets.Ouch), (0, Doom.Assets.EvilGrin), (0, Doom.Assets.Rampage), (0, 8)];
        sw.Restart();
        var rows = pairs.Select(p => new[] { hd.Face(p.Item1)! }.Concat(hd.Between(p.Item1, p.Item2)).Append(hd.Face(p.Item2)!).ToArray()).ToList();
        Console.WriteLine($"In-betweens: {sw.ElapsedMilliseconds} ms");
        int cols = rows.Max(r => r.Length);
        var sheet = new uint[cols * hd.W * rows.Count * hd.H];
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[r].Length; c++)
                for (int y = 0; y < hd.H; y++)
                    Array.Copy(rows[r][c], y * hd.W, sheet, (r * hd.H + y) * cols * hd.W + c * hd.W, hd.W);
        Png(Path.Combine(dir, "faces_between.png"), sheet, cols * hd.W, rows.Count * hd.H);
        Console.WriteLine($"Wrote {dir}");
    }

    private static uint NearestOriginal(Doom.Assets a, Doom.HdFaces hd, int face, int x, int y)
    {
        var p = a.Faces[face]!;
        int sx = x / Doom.HdFaces.S - (hd.OriginX - p.Left), sy = y / Doom.HdFaces.S - (hd.OriginY - p.Top);
        if (sx < 0 || sy < 0 || sx >= p.Width || sy >= p.Height) return 0;
        short v = p.Pix[sy * p.Width + sx];
        return v < 0 ? 0 : a.Palettes[0][v];
    }

    private static void Png(string path, uint[] px, int w, int h)
    {
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, px, w * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static bool TryRun(string[] args, out int code)
    {
        code = 0;
        if (args.Length == 0) return false;
        var cmd = args[0].TrimStart('-', '/').ToLowerInvariant();
        if (!Commands.Contains(cmd)) return false;
        AttachConsole(-1);
        var port = Store.Load<Settings>("settings.json").Port;
        var rest = Positional(args.Skip(1).ToArray());

        try
        {
            switch (cmd)
            {
                case "notify":
                    code = Report(Send(port, "/notify", Json(new
                    {
                        message = string.Join(' ', rest),
                        title = Opt(args, "--title"),
                        level = Opt(args, "--level") ?? "info",
                        sound = Opt(args, "--sound"),
                    })));
                    break;
                case "remind":
                    code = Report(Send(port, "/remind", Json(new { text = string.Join(' ', rest), @in = Opt(args, "--in"), at = Opt(args, "--at"), every = Opt(args, "--every") })));
                    break;
                case "progress":
                {
                    if (rest.Count < 2) { Console.WriteLine("usage: DoomGuy progress <id> <percent|done|failed|remove> [title]"); code = 2; break; }
                    bool isPct = int.TryParse(rest[1].TrimEnd('%'), out var pct);
                    code = Report(Send(port, "/progress", Json(new
                    {
                        id = rest[0],
                        percent = isPct ? pct : (int?)null,
                        state = isPct ? (pct >= 100 ? "done" : "running") : rest[1],
                        title = string.Join(' ', rest.Skip(2)),
                        detail = Opt(args, "--detail"),
                        source = Opt(args, "--source"),
                    })));
                    break;
                }
                case "hook":
                    Send(port, "/claude", Console.In.ReadToEnd());
                    code = 0; // never fail a Claude hook
                    break;
                case "state":
                    code = Report(Send(port, "/state", null));
                    break;
                case "install-hooks":
                    ClaudeHooks.Install(port);
                    Console.WriteLine($"Installed DoomGuy hooks in {ClaudeHooks.SettingsPath} (backup: settings.json.doomguy-backup).");
                    break;
                case "gauge":
                    if (rest.Count < 2) { Console.WriteLine("usage: DoomGuy gauge <name> <0-100|green|yellow|red>"); code = 2; break; }
                    code = Report(Send(port, "/gauge", Json(new { name = rest[0], value = rest[1] })));
                    break;
                case "profile":
                    code = Report(Send(port, "/profile", Json(new { name = rest.Count > 0 ? string.Join(" ", rest) : null })));
                    break;
                case "dump-faces":
                    DumpFaces(rest.Count > 0 ? rest[0] : ".");
                    break;
                case "dump-patch":
                {
                    // Test aid: a WAD picture at 4x, hard pixels, with a grid line every 8 screen pixels.
                    var wad = Store.Load<Settings>("settings.json").WadPath ?? (Doom.WadFile.FindInstalled() ?? Doom.WadFile.Bundled())!;
                    var a = new Doom.Assets(new Doom.WadFile(wad));
                    var p = Doom.Patch.Parse(a.Wad.Lump(rest[0]))!;
                    const int s = 4;
                    var px = new uint[p.Width * s * p.Height * s];
                    for (int y = 0; y < p.Height * s; y++)
                        for (int x = 0; x < p.Width * s; x++)
                        {
                            short v = p.Pix[y / s * p.Width + x / s];
                            px[y * p.Width * s + x] = (x / s % 8 == 0 && x % s == 0) || (y / s % 8 == 0 && y % s == 0) ? 0xFF00FFFF : v < 0 ? 0 : a.Palettes[0][v];
                        }
                    Png(rest[1], px, p.Width * s, p.Height * s);
                    break;
                }
                case "uninstall-hooks":
                    ClaudeHooks.Uninstall();
                    Console.WriteLine("Removed DoomGuy hooks.");
                    break;
                default:
                    Console.WriteLine(Usage);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            code = 1;
        }
        return true;
    }

    private const string Usage = """
        DoomGuy.exe                     start the companion (or bring it up)
        DoomGuy.exe --mini              start as the status bar
        DoomGuy.exe notify "text" [--level info|success|warn|error] [--title T] [--sound ITEMUP|none]
        DoomGuy.exe remind "stand up in 30m"   (or: remind "text" --in 30m | --at 17:30 | --every 1h)
        DoomGuy.exe progress <id> <0-100|done|failed|remove> [title] [--detail D] [--source S]
        DoomGuy.exe gauge <name> <0-100|green|yellow|red>   (feeds a slot wired to PUSHED)
        DoomGuy.exe profile [HOME|WORK|...]                 (switch what the bar follows)
        DoomGuy.exe state | install-hooks | uninstall-hooks
        API: http://127.0.0.1:6666/  (GET / for the endpoint list)
        """;

    private static int Report((bool Ok, string Body) r)
    {
        Console.WriteLine(r.Body.TrimEnd());
        return r.Ok ? 0 : 1;
    }

    private static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    private static string? Opt(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> Positional(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--")) { i++; continue; }
            list.Add(args[i]);
        }
        return list;
    }

    /// <summary>POSTs (or GETs, with no body) to the running app. Fails quietly if it isn't running.</summary>
    public static (bool Ok, string Body) Send(int port, string path, string? body)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var url = $"http://127.0.0.1:{port}{path}";
            var resp = body == null
                ? http.GetAsync(url).GetAwaiter().GetResult()
                : http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            return (resp.IsSuccessStatusCode, resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (false, $"DoomGuy isn't answering on port {port} ({ex.GetBaseException().Message}).");
        }
    }
}
