using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DoomGuy.Doom;

namespace DoomGuy.Core;

/// <summary>What one status bar number (health, ammo, armor) is wired to, with that source's settings.</summary>
public sealed class SlotBinding
{
    public string Source { get; set; } = "";
    public Dictionary<string, string> Args { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A named set of bindings (HOME, WORK...). Switching profile rewires the whole bar at once.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "HOME";
    public Dictionary<string, SlotBinding> Slots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A source a slot can follow. Percent sources read 0-100; counts can go to 999.</summary>
public sealed record SourceDef(string Id, string Label, string Help, string[] Args, bool Percent);

/// <summary>The latest value of a slot, and what to say about it.</summary>
public readonly record struct Reading(int? Value, string Status, bool Error);

/// <summary>
/// Turns sources into the numbers on the bar. Sources that live in DoomGuy (attention, tasks) are read
/// on demand; the rest (UGS, URLs, commands, Claude's transcripts) are polled in the background and the
/// last result is used. Values pushed from outside arrive through /gauge.
/// </summary>
public sealed class Gauges
{
    public static readonly string[] Slots = ["health", "ammo", "armor"];

    public static readonly SourceDef[] Sources =
    [
        new("attention", "NEEDS YOU", "100% UNTIL SOMETHING NEEDS YOU", [], true),
        new("working", "WORKING TASKS", "TASKS WORKING RIGHT NOW", [], false),
        new("chats", "ACTIVE CHATS", "CLAUDE SESSIONS OPEN RIGHT NOW", [], false),
        new("plan", "PLAN LEFT (5H)", "CLAUDE 5-HOUR PLAN LIMIT LEFT", [], true),
        new("week", "PLAN LEFT (7D)", "CLAUDE WEEKLY PLAN LIMIT LEFT", [], true),
        new("context", "CONTEXT LEFT", "CONTEXT LEFT IN THE LATEST CLAUDE CHAT", ["WINDOW K"], true),
        new("tokens", "TOKENS TODAY", "CLAUDE OUTPUT TOKENS TODAY (THOUSANDS)", [], false),
        new("budget", "TOKEN BUDGET", "DAILY CLAUDE TOKEN BUDGET LEFT", ["BUDGET M"], true),
        new("progress", "JOB PROGRESS", "LATEST JOB REPORTED TO /PROGRESS", [], true),
        new("done", "DONE TODAY", "TASKS FINISHED TODAY", [], false),
        new("ugs", "UGS BADGE", "UGS BADGE: GREEN 100, YELLOW 50, RED 15", ["SERVER", "PROJECT", "BADGE", "TOKEN"], true),
        new("url", "WEB URL", "NUMBER OR GREEN/YELLOW/RED FROM A URL", ["URL", "FIELD", "EVERY S"], true),
        new("command", "COMMAND", "POWERSHELL: LAST LINE = NUMBER OR COLOUR", ["COMMAND", "EVERY S"], true),
        new("pushed", "PUSHED", "SET BY /GAUGE?NAME=..&VALUE=..", ["NAME"], true),
    ];

    public static SourceDef Def(string id) => Sources.FirstOrDefault(s => s.Id == id) ?? Sources[0];

    public static string DefaultSource(string slot) => slot switch { "ammo" => "working", "armor" => "progress", _ => "attention" };

    public static List<Profile> DefaultProfiles() =>
    [
        new() { Name = "HOME" },
        new()
        {
            Name = "WORK",
            Slots = new(StringComparer.OrdinalIgnoreCase)
            {
                ["health"] = new() { Source = "ugs", Args = new(StringComparer.OrdinalIgnoreCase) { ["BADGE"] = "Cook" } },
            },
        },
    ];

    private readonly Hub _hub;
    private readonly ClaudeLedger _ledger = new();
    private readonly Dictionary<string, Poll> _polls = new();
    private readonly Dictionary<string, (int Value, DateTime At)> _pushed = new(StringComparer.OrdinalIgnoreCase);

    // From Claude Code's status line feed: exact context per session, and the plan limits.
    private readonly Dictionary<string, (double UsedPct, long Size)> _ctx = new();
    private (double Used, DateTime Resets, DateTime At)? _five, _week;
    public DateTime StatusLineSeen { get; private set; }
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private sealed class Poll
    {
        public DateTime Next;
        public bool Busy;
        public Reading Last = new(null, "WAITING...", false);
    }

    public Gauges(Hub hub) { _hub = hub; }

    public Profile Active
    {
        get
        {
            var s = _hub.Settings;
            if (s.Profiles.Count == 0) s.Profiles = DefaultProfiles();
            return s.Profiles.FirstOrDefault(p => p.Name.Equals(s.Profile, StringComparison.OrdinalIgnoreCase)) ?? s.Profiles[0];
        }
    }

    public SlotBinding Binding(string slot)
    {
        var p = Active;
        if (!p.Slots.TryGetValue(slot, out var b)) p.Slots[slot] = b = new SlotBinding { Source = DefaultSource(slot) };
        if (Sources.All(s => s.Id != b.Source)) b.Source = DefaultSource(slot);
        return b;
    }

    /// <summary>The words printed under a slot on the bar: what it tracks (LABEL overrides).</summary>
    public string Label(string slot)
    {
        var b = Binding(slot);
        string Arg(string n) => b.Args.TryGetValue(n, out var v) ? v.Trim() : "";
        if (Arg("LABEL").Length > 0) return Arg("LABEL").ToUpperInvariant();
        return b.Source switch
        {
            "attention" => "HEALTH",
            "working" => "WORKING",
            "chats" => "CHATS",
            "plan" => "5H LEFT",
            "week" => "7D LEFT",
            "context" => "CONTEXT",
            "tokens" => "K TOKENS",
            "budget" => "BUDGET",
            "progress" => "JOB",
            "done" => "DONE",
            "ugs" => (Arg("BADGE").Length > 0 ? Arg("BADGE") : "COOK").ToUpperInvariant(),
            "url" => "URL",
            "command" => "CMD",
            "pushed" => (Arg("NAME").Length > 0 ? Arg("NAME") : "PUSHED").ToUpperInvariant(),
            _ => slot.ToUpperInvariant(),
        };
    }

    /// <summary>The label baked into Doom's bar for each slot (no plate needed when it already says this).</summary>
    public static string BakedLabel(string slot) => slot switch { "ammo" => "AMMO", "armor" => "ARMOR", _ => "HEALTH" };

    public int Value(string slot)
    {
        var r = Read(slot);
        int max = slot == "ammo" ? 999 : 100;
        return Math.Clamp(r.Value ?? (slot == "health" ? 100 : 0), 0, max);
    }

    public Reading Read(string slot) => Read(Binding(slot));

    public Reading Read(SlotBinding b)
    {
        string Arg(string name, string fallback = "") => b.Args.TryGetValue(name, out var v) && v.Trim().Length > 0 ? v.Trim() : fallback;
        switch (b.Source)
        {
            case "attention": return new(_hub.Attention, $"{_hub.NeedsYouCount} NEED YOU, {_hub.FiringCount} DUE", false);
            case "working": return new(_hub.WorkingCount, $"{_hub.WorkingCount} WORKING", false);
            case "chats":
            {
                int n = _hub.Tasks.Count(t => t.IsClaude);
                return new(n, $"{n} OPEN", false);
            }
            case "progress": return new(_hub.Armor, _hub.Armor > 0 ? $"{_hub.Armor}%" : "NO JOB RUNNING", false);
            case "done": return new(_hub.State.DoneToday, $"{_hub.State.DoneToday} TODAY", false);
            case "tokens":
                _ledger.Touch();
                return new((int)Math.Min(999, _ledger.OutputToday / 1000), $"{_ledger.OutputToday / 1000}K OUTPUT TOKENS TODAY", false);
            case "budget":
            {
                _ledger.Touch();
                double budget = Num(Arg("BUDGET M", "20"), 20) * 1_000_000;
                int left = (int)Math.Round(100 - _ledger.WeightedToday * 100.0 / Math.Max(1, budget));
                return new(Math.Max(0, left), $"{_ledger.WeightedToday / 1_000_000.0:0.0}M OF {budget / 1_000_000:0.#}M USED", false);
            }
            case "plan":
            case "week":
            {
                var w = b.Source == "plan" ? _five : _week;
                if (w == null || w.Value.Resets < DateTime.Now)
                    return new(null, StatusLineSeen == default ? "TURN ON THE CLAUDE STATUS LINE (HUD TAB)" : "NO LIMIT REPORTED (PRO/MAX ONLY)", StatusLineSeen == default);
                int left = (int)Math.Round(100 - w.Value.Used);
                return new(left, $"{w.Value.Used:0}% USED, RESETS {Resets(w.Value.Resets)}", false);
            }
            case "context":
            {
                var session = _hub.Tasks.Where(t => t.IsClaude).OrderByDescending(t => t.UpdatedAt).FirstOrDefault();
                if (session != null && _ctx.TryGetValue(session.Id, out var exact))
                    return new((int)Math.Round(100 - exact.UsedPct), $"{session.Name}: {exact.UsedPct:0}% OF {exact.Size / 1000}K USED", false);
                _ledger.Touch();
                if (session == null || !_ledger.Context.TryGetValue(session.Id, out var used)) return new(null, "NO CLAUDE SESSION SEEN YET", false);
                double window = Num(Arg("WINDOW K", "1000"), 1000) * 1000;
                int left = (int)Math.Round(100 - used * 100.0 / window);
                return new(Math.Max(0, left), $"{session.Name}: {used / 1000}K OF {window / 1000}K", false);
            }
            case "pushed":
            {
                var name = Arg("NAME", "health");
                if (_pushed.TryGetValue(name, out var p)) return new(p.Value, $"{name.ToUpperInvariant()} = {p.Value} AT {p.At:HH:mm}", false);
                if (_hub.State.Pushed.TryGetValue(name, out var saved)) return new(saved, $"{name.ToUpperInvariant()} = {saved} (BEFORE RESTART)", false);
                return new(null, $"NOTHING PUSHED TO {name.ToUpperInvariant()} YET", false);
            }
            case "ugs":
            case "url":
            case "command":
                return Polled(b);
        }
        return new(null, "", true);
    }

    /// <summary>Takes in Claude Code's status line JSON (context per session, plan limits).</summary>
    public void StatusLine(JsonElement p)
    {
        StatusLineSeen = DateTime.Now;
        var sid = Hub.Str(p, "session_id");
        if (p.TryGetProperty("context_window", out var cw) && cw.ValueKind == JsonValueKind.Object
            && cw.TryGetProperty("used_percentage", out var up) && up.TryGetDouble(out var used) && sid.Length > 0)
            _ctx[sid] = (used, cw.TryGetProperty("context_window_size", out var sz) && sz.TryGetInt64(out var size) ? size : 0);
        if (p.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
        {
            _five = Limit(rl, "five_hour") ?? _five;
            _week = Limit(rl, "seven_day") ?? _week;
        }
    }

    private static (double, DateTime, DateTime)? Limit(JsonElement rl, string name)
    {
        if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percentage", out var u) || !u.TryGetDouble(out var used)) return null;
        var resets = w.TryGetProperty("resets_at", out var r) && r.TryGetInt64(out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime : DateTime.Now.AddHours(5);
        return (used, resets, DateTime.Now);
    }

    private static string Resets(DateTime at) =>
        at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("ddd HH:mm").ToUpperInvariant();

    /// <summary>The short text DoomGuy hands back for Claude's status line.</summary>
    public string StatusText()
    {
        var parts = new List<string> { $"DoomGuy {_hub.Health}%" };
        if (_five is { } f && f.Resets > DateTime.Now) parts.Add($"5h {f.Used:0}% used");
        if (_week is { } w && w.Resets > DateTime.Now) parts.Add($"7d {w.Used:0}% used");
        if (_hub.NeedsYouCount > 0) parts.Add($"{_hub.NeedsYouCount} need you");
        return string.Join(" | ", parts);
    }

    /// <summary>Sets a pushed value; 'value' can be a number or a word like green/yellow/red.</summary>
    public bool Push(string name, string value)
    {
        var v = Map(value);
        if (v == null) return false;
        _pushed[name.Trim()] = (v.Value, DateTime.Now);
        _hub.State.Pushed[name.Trim()] = v.Value;
        return true;
    }

    /// <summary>Forget cached results (after a source or its settings change).</summary>
    public void Invalidate() => _polls.Clear();

    // ---------------------------------------------------------------- polled sources

    private Reading Polled(SlotBinding b)
    {
        var key = b.Source + "|" + string.Join("|", b.Args.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
        if (!_polls.TryGetValue(key, out var poll)) _polls[key] = poll = new Poll();
        var now = DateTime.Now;
        if (!poll.Busy && now >= poll.Next)
        {
            poll.Busy = true;
            var args = new Dictionary<string, string>(b.Args, StringComparer.OrdinalIgnoreCase);
            var source = b.Source;
            var ui = System.Windows.Application.Current?.Dispatcher;
            Task.Run(async () =>
            {
                Reading r;
                try { r = await Fetch(source, args, poll.Last); }
                catch (Exception ex) { r = new(poll.Last.Value, Explain(ex, args), true); }
                void Done()
                {
                    poll.Last = r;
                    poll.Busy = false;
                    double every = source == "ugs" ? 60 : Num(args.GetValueOrDefault("EVERY S", ""), source == "command" ? 60 : 30);
                    poll.Next = DateTime.Now.AddSeconds(Math.Max(5, every));
                }
                if (ui != null) ui.BeginInvoke(Done); else Done();
            });
        }
        return poll.Last;
    }

    /// <summary>Short, readable failure text for the HUD (the full message is too long for one line).</summary>
    private static string Explain(Exception ex, Dictionary<string, string> args)
    {
        var target = args.GetValueOrDefault("SERVER") ?? args.GetValueOrDefault("URL") ?? "";
        var host = Uri.TryCreate(target.Contains("://") ? target : "http://" + target, UriKind.Absolute, out var u) ? u.Host.ToUpperInvariant() : "THE SERVER";
        return ex switch
        {
            HttpRequestException { StatusCode: { } code } => $"{host} SAID {(int)code}",
            HttpRequestException => $"CAN'T REACH {host}",
            TaskCanceledException or TimeoutException => $"{host} TIMED OUT",
            JsonException => "REPLY ISN'T THE JSON EXPECTED",
            _ => "ERROR: " + ex.GetBaseException().Message.ToUpperInvariant(),
        };
    }

    private static async Task<Reading> Fetch(string source, Dictionary<string, string> args, Reading last)
    {
        string Arg(string n) => args.TryGetValue(n, out var v) ? v.Trim() : "";
        switch (source)
        {
            case "ugs": return await FetchUgs(Arg("SERVER"), Arg("PROJECT"), Arg("BADGE"), Arg("TOKEN"), last);
            case "url":
            {
                var url = Arg("URL");
                if (url.Length == 0) return new(null, "SET A URL", true);
                var body = await Http.GetStringAsync(url);
                var text = Arg("FIELD").Length > 0 ? JsonField(body, Arg("FIELD")) : body;
                var v = Map(text ?? "");
                return v == null ? new(last.Value, $"CAN'T READ \"{Short(text)}\"", true) : new(v, $"{Short(text)} AT {DateTime.Now:HH:mm}", false);
            }
            case "command":
            {
                var cmd = Arg("COMMAND");
                if (cmd.Length == 0) return new(null, "SET A COMMAND", true);
                var line = await RunPowerShell(cmd);
                var v = Map(line);
                return v == null ? new(last.Value, $"OUTPUT \"{Short(line)}\" ISN'T A NUMBER OR COLOUR", true) : new(v, $"{Short(line)} AT {DateTime.Now:HH:mm}", false);
            }
        }
        return new(null, "", true);
    }

    /// <summary>
    /// UGS metadata server (or Horde's /ugs endpoint): GET {server}/api/build?project=..&amp;lastbuildid=0 lists the
    /// badges posted by the build system. The newest finished result of the named badge decides the colour.
    /// </summary>
    private static async Task<Reading> FetchUgs(string server, string project, string badge, string token, Reading last)
    {
        if (server.Length == 0 || project.Length == 0) return new(null, "SET SERVER AND PROJECT", true);
        if (badge.Length == 0) badge = "Cook";
        var baseUrl = server.TrimEnd('/');
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) baseUrl = "http://" + baseUrl;
        var url = $"{baseUrl}/api/build?project={Uri.EscapeDataString(project)}&lastbuildid=0";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (token.Length > 0) req.Headers.Authorization = new("Bearer", token);
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return new(last.Value, $"UGS SAID {(int)resp.StatusCode} {resp.ReasonPhrase?.ToUpperInvariant()}", true);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("builds", out var b) || doc.RootElement.TryGetProperty("Builds", out b) ? b : default;
        if (list.ValueKind != JsonValueKind.Array) return new(last.Value, "UNEXPECTED REPLY FROM UGS", true);

        int bestChange = -1, buildingChange = -1;
        string? bestResult = null;
        foreach (var e in list.EnumerateArray())
        {
            var type = Prop(e, "BuildType") ?? Prop(e, "Name") ?? "";
            if (!type.Equals(badge, StringComparison.OrdinalIgnoreCase)) continue;
            int change = int.TryParse(Prop(e, "ChangeNumber") ?? Prop(e, "Change"), out var c) ? c : 0;
            var result = UgsResult(Prop(e, "Result") ?? "");
            if (result == "STARTING") { buildingChange = Math.Max(buildingChange, change); continue; }
            if (result is "SKIPPED" or "") continue;
            if (change > bestChange) { bestChange = change; bestResult = result; }
        }
        if (bestResult == null) return new(last.Value, $"NO \"{badge.ToUpperInvariant()}\" BADGE FOR THIS PROJECT", true);
        int value = bestResult switch { "SUCCESS" => 100, "WARNING" => 50, _ => 15 };
        var building = buildingChange > bestChange ? $" ({buildingChange} RUNNING)" : "";
        return new(value, $"{badge.ToUpperInvariant()} {bestResult}, CL {bestChange}{building}", false);
    }

    private static string UgsResult(string r) => r.Trim().ToUpperInvariant() switch
    {
        "0" or "STARTING" => "STARTING",
        "1" or "FAILURE" => "FAILURE",
        "2" or "WARNING" => "WARNING",
        "3" or "SUCCESS" => "SUCCESS",
        "4" or "SKIPPED" => "SKIPPED",
        var other => other,
    };

    private static string? Prop(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return null;
    }

    private static string? JsonField(string body, string path)
    {
        var e = JsonDocument.Parse(body).RootElement;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (e.ValueKind == JsonValueKind.Array && int.TryParse(part, out var i) && i < e.GetArrayLength()) e = e[i];
            else if (e.ValueKind == JsonValueKind.Object && e.EnumerateObject().FirstOrDefault(p => p.Name.Equals(part, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: not JsonValueKind.Undefined } prop) e = prop.Value;
            else return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString();
    }

    private static async Task<string> RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException("command took over 30 s"); }
        return (await output).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
    }

    /// <summary>Numbers stay numbers (0.75 reads as 75%); colours and build words map to 100 / 50 / 15.</summary>
    public static int? Map(string text)
    {
        var s = text.Trim().Trim('"').Trim().ToLowerInvariant();
        if (s.EndsWith('%')) s = s[..^1];
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return (int)Math.Round(d > 0 && d < 1 && s.Contains('.') ? d * 100 : d);
        return s switch
        {
            "green" or "success" or "succeeded" or "ok" or "pass" or "passed" or "good" or "true" or "up" => 100,
            "yellow" or "amber" or "warning" or "warn" or "warnings" or "unstable" or "partial" => 50,
            "red" or "failure" or "failed" or "fail" or "error" or "broken" or "bad" or "false" or "down" => 15,
            _ => null,
        };
    }

    private static double Num(string s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : fallback;

    private static string Short(string? s)
    {
        s = Hub.Clean(s ?? "").ToUpperInvariant();
        return s.Length > 24 ? s[..24] + "..." : s;
    }
}

/// <summary>
/// Reads Claude Code's transcripts (~/.claude/projects/**/*.jsonl) incrementally: today's token totals and
/// each session's latest context size, from the usage recorded on assistant messages.
/// </summary>
public sealed class ClaudeLedger
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = [];
    private DateTime _day = DateTime.Today;
    private DateTime _nextScan;
    private bool _busy;

    public long OutputToday { get; private set; }
    public long WeightedToday { get; private set; }
    /// <summary>Session id -> tokens in context at its latest assistant message.</summary>
    public readonly Dictionary<string, long> Context = new();

    /// <summary>Starts a background scan if one is due (every 20 s while a Claude source is in use).</summary>
    public void Touch()
    {
        if (_busy || DateTime.Now < _nextScan) return;
        _busy = true;
        var ui = System.Windows.Application.Current?.Dispatcher;
        Task.Run(() =>
        {
            var result = Scan();
            void Apply()
            {
                if (_day != DateTime.Today) { _day = DateTime.Today; OutputToday = WeightedToday = 0; _seen.Clear(); }
                OutputToday += result.Output;
                WeightedToday += result.Weighted;
                foreach (var (k, v) in result.Context) Context[k] = v;
                _busy = false;
                _nextScan = DateTime.Now.AddSeconds(20);
            }
            if (ui != null) ui.BeginInvoke(Apply); else Apply();
        });
    }

    private (long Output, long Weighted, Dictionary<string, long> Context) Scan()
    {
        long output = 0, weighted = 0;
        var context = new Dictionary<string, long>();
        if (!Directory.Exists(_root)) return (0, 0, context);
        var today = DateTime.Today;
        foreach (var file in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < today) continue;
                long from;
                lock (_offsets) from = _offsets.GetValueOrDefault(file);
                if (info.Length < from) from = 0;
                if (info.Length == from) continue;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(from, SeekOrigin.Begin);
                var buf = new byte[info.Length - from];
                int read = 0;
                while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
                int end = Array.LastIndexOf(buf, (byte)'\n', Math.Max(0, read - 1));
                if (end < 0) continue;
                lock (_offsets) _offsets[file] = from + end + 1;
                foreach (var line in Encoding.UTF8.GetString(buf, 0, end).Split('\n'))
                {
                    if (!line.Contains("\"usage\"") || !line.Contains("\"assistant\"")) continue;
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var u)) continue;
                    long L(string n) => u.TryGetProperty(n, out var v) && v.TryGetInt64(out var x) ? x : 0;
                    long inTok = L("input_tokens"), cw = L("cache_creation_input_tokens"), cr = L("cache_read_input_tokens"), outTok = L("output_tokens");
                    if (r.TryGetProperty("sessionId", out var sid) && sid.GetString() is { } s && !(r.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True))
                        context[s] = inTok + cw + cr;
                    var when = r.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var t) ? t.ToLocalTime() : DateTime.Now;
                    if (when.Date != today) continue;
                    var id = msg.TryGetProperty("id", out var mid) ? mid.GetString() ?? line.GetHashCode().ToString() : line.GetHashCode().ToString();
                    lock (_seen) if (!_seen.Add(id)) continue;
                    output += outTok;
                    weighted += inTok + cw + outTok;
                }
            }
            catch { }
        }
        return (output, weighted, context);
    }
}
