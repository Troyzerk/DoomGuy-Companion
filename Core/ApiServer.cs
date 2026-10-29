using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DoomGuy.Core;

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 so anything (Claude hooks, scripts, builds, browsers) can reach
/// DoomGuy with curl. Loopback only, no http.sys URL ACL, no admin rights.
/// </summary>
public sealed class ApiServer
{
    private readonly Hub _hub;
    private readonly Dispatcher _ui;
    private TcpListener? _listener;

    public Action? ShowMain, ShowMini;
    public Action<int>? ShowTab;
    public Action? ProfileChanged;
    /// <summary>Memory, caches and frame times, for /perf.</summary>
    public Func<object>? Perf;
    public string? Error { get; private set; }
    public int Port { get; private set; }

    public ApiServer(Hub hub, Dispatcher ui) { _hub = hub; _ui = ui; }

    public bool Start(int port)
    {
        Port = port;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = AcceptLoop(_listener);
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ? $"PORT {port} IS ALREADY IN USE" : ex.Message;
            return false;
        }
    }

    public void Stop() { try { _listener?.Stop(); } catch { } }

    private async Task AcceptLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Handle(c);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            var (method, target, headers, body) = await Read(stream);
            if (method == null) return;
            int clientPort = (client.Client.RemoteEndPoint as IPEndPoint)?.Port ?? 0;
            var (status, type, text) = await Route(method, target!, headers!, body!, clientPort);
            var payload = Encoding.UTF8.GetBytes(text);
            var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request")}\r\n" +
                       $"Content-Type: {type}; charset=utf-8\r\nContent-Length: {payload.Length}\r\n" +
                       "Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(payload);
        }
        catch { }
    }

    private static async Task<(string?, string?, Dictionary<string, string>?, string?)> Read(NetworkStream s)
    {
        var buf = new byte[8192];
        var data = new List<byte>();
        int headerEnd = -1;
        using var cts = new CancellationTokenSource(5000);
        while (headerEnd < 0)
        {
            int n = await s.ReadAsync(buf, cts.Token);
            if (n <= 0) return (null, null, null, null);
            data.AddRange(buf.AsSpan(0, n).ToArray());
            if (data.Count > 64 * 1024) return (null, null, null, null);
            headerEnd = IndexOf(data, "\r\n\r\n"u8.ToArray());
        }
        var headText = Encoding.ASCII.GetString(data.GetRange(0, headerEnd).ToArray());
        var lines = headText.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return (null, null, null, null);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        int length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) ? Math.Min(len, 4 * 1024 * 1024) : 0;
        var body = data.Skip(headerEnd + 4).ToList();
        while (body.Count < length)
        {
            int n = await s.ReadAsync(buf, cts.Token);
            if (n <= 0) break;
            body.AddRange(buf.AsSpan(0, n).ToArray());
        }
        return (first[0].ToUpperInvariant(), first[1], headers, Encoding.UTF8.GetString(body.Take(length).ToArray()));
    }

    private readonly Dictionary<string, IntPtr> _sessionWindows = new();

    /// <summary>
    /// The window a Claude session runs in, found from the first hook that reaches us (and again only if
    /// that window has gone): hooks fire on every tool call, the window never changes.
    /// </summary>
    private IntPtr SessionWindow(string session, string cwd, int clientPort)
    {
        if (session.Length == 0 || clientPort == 0) return IntPtr.Zero;
        lock (_sessionWindows)
            if (_sessionWindows.TryGetValue(session, out var known) && CallerWindow.Alive(known)) return known;
        var found = CallerWindow.Find(clientPort, Port, cwd);
        if (found != IntPtr.Zero) lock (_sessionWindows) _sessionWindows[session] = found;
        return found;
    }

    private static int IndexOf(List<byte> d, byte[] pat)
    {
        for (int i = 0; i + pat.Length <= d.Count; i++)
        {
            int k = 0;
            while (k < pat.Length && d[i + k] == pat[k]) k++;
            if (k == pat.Length) return i;
        }
        return -1;
    }

    private async Task<(int, string, string)> Route(string method, string target, Dictionary<string, string> headers, string body, int clientPort)
    {
        var q = target.IndexOf('?');
        var path = (q >= 0 ? target[..q] : target).TrimEnd('/').ToLowerInvariant();
        var args = ParseQuery(q >= 0 ? target[(q + 1)..] : "");
        JsonElement json = default;
        if (body.Length > 0)
        {
            var type = headers.GetValueOrDefault("Content-Type", "");
            // curl -d "some words" sends plain text labelled as a form; no '=' means it is just the message.
            if (type.Contains("x-www-form-urlencoded") && body.Contains('=')) foreach (var kv in ParseQuery(body)) args[kv.Key] = kv.Value;
            else if (type.Contains("x-www-form-urlencoded") || type.StartsWith("text/"))
            {
                var plain = Uri.UnescapeDataString(body.Replace('+', ' ')).Trim();
                args.TryAdd("message", plain);
                args.TryAdd("text", plain);
            }
            else
            {
                try
                {
                    json = JsonDocument.Parse(body).RootElement.Clone();
                    if (json.ValueKind == JsonValueKind.Object)
                        foreach (var prop in json.EnumerateObject())
                            if (prop.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                                args.TryAdd(prop.Name, prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString());
                }
                catch (JsonException)
                {
                    // Plain text body: treat it as the message.
                    args.TryAdd("message", body.Trim());
                }
            }
        }
        string A(params string[] names)
        {
            foreach (var n in names) if (args.TryGetValue(n, out var v) && v.Length > 0) return v;
            return "";
        }

        switch (path)
        {
            case "" or "/help":
                return (200, "text/plain", Help());

            case "/state":
                var snap = await _ui.InvokeAsync(() => Store.Serialize(_hub.Snapshot()));
                return (200, "application/json", snap);

            case "/claude":
                if (json.ValueKind != JsonValueKind.Object) return (400, "text/plain", "expected the Claude Code hook JSON as the body\n");
                var window = SessionWindow(Hub.Str(json, "session_id"), Hub.Str(json, "cwd"), clientPort);
                await _ui.InvokeAsync(() => _hub.HandleClaude(json, window));
                return (200, "application/json", "{}");

            case "/notify":
            {
                var msg = A("message", "text", "msg", "body");
                if (msg.Length == 0) return (400, "text/plain", "message is required\n");
                await _ui.InvokeAsync(() => _hub.Notify(A("title", "source"), msg, A("level", "type") is { Length: > 0 } l ? l : "info", A("sound")));
                return (200, "text/plain", "ok\n");
            }

            case "/progress":
            {
                int? pct = int.TryParse(A("percent", "progress", "pct"), out var p) ? p : null;
                var state = A("state", "status");
                if (state.Length == 0) state = pct >= 100 ? "done" : "running";
                await _ui.InvokeAsync(() => _hub.Progress(A("id", "key"), A("title", "name"), pct, state, A("detail", "message"), A("source")));
                return (200, "text/plain", "ok\n");
            }

            case "/remind":
            {
                var text = A("text", "message", "what");
                var now = DateTime.Now;
                DateTime due;
                TimeSpan? every = null;
                var inArg = A("in"); var atArg = A("at"); var everyArg = A("every");
                if (everyArg.Length > 0)
                {
                    if (!TimeParse.When("every " + everyArg + (atArg.Length > 0 ? " at " + atArg : ""), now, out due, out every))
                        return (400, "text/plain", "could not read 'every'\n");
                }
                else if (inArg.Length > 0)
                {
                    var d = TimeParse.Duration(inArg);
                    if (d == null) return (400, "text/plain", "could not read 'in' (try 30m, 1h30m, 90s)\n");
                    due = now + d.Value;
                }
                else if (atArg.Length > 0)
                {
                    var t = TimeParse.At(atArg, now);
                    if (t == null) return (400, "text/plain", "could not read 'at' (try 17:30, 5pm, or an ISO time)\n");
                    due = t.Value;
                }
                else if (!TimeParse.Reminder(text, now, out text, out due, out every))
                    return (400, "text/plain", "say when: text=\"stand up in 30m\", or in=30m / at=17:30 / every=1h\n");
                if (text.Trim().Length == 0) return (400, "text/plain", "text is required\n");
                var r = await _ui.InvokeAsync(() => _hub.AddReminder(text, due, every));
                return (200, "application/json", Store.Serialize(new { r.Id, r.Text, r.Due, r.EverySeconds }));
            }

            case "/statusline":
            {
                // Claude Code's status line: feeds the plan-limit and context gauges, prints a short line back.
                if (json.ValueKind != JsonValueKind.Object) return (400, "text/plain", "expected the status line JSON\n");
                var line = await _ui.InvokeAsync(() => { _hub.Gauges.StatusLine(json); return _hub.Gauges.StatusText(); });
                return (200, "text/plain", line + "\n");
            }

            case "/gauge":
            {
                var name = A("name", "gauge");
                var value = A("value", "state", "status", "message");
                if (name.Length == 0 || value.Length == 0) return (400, "text/plain", "name and value are required (value: 0-100 or green/yellow/red)\n");
                var ok = await _ui.InvokeAsync(() => _hub.Gauges.Push(name, value));
                return ok ? (200, "text/plain", "ok\n") : (400, "text/plain", "value must be a number or green/yellow/red\n");
            }

            case "/profile":
            {
                var name = A("name", "profile");
                var result = await _ui.InvokeAsync(() =>
                {
                    var s = _hub.Settings;
                    _ = _hub.Gauges.Active;
                    if (name.Length > 0)
                    {
                        var p = s.Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (p == null) return (false, $"no profile called {name}; have: {string.Join(", ", s.Profiles.Select(x => x.Name))}");
                        s.Profile = p.Name;
                        _hub.Gauges.Invalidate();
                        ProfileChanged?.Invoke();
                    }
                    return (true, $"{s.Profile} (profiles: {string.Join(", ", s.Profiles.Select(x => x.Name))})");
                });
                return (result.Item1 ? 200 : 400, "text/plain", result.Item2 + "\n");
            }

            case "/show":
                await _ui.InvokeAsync(() =>
                {
                    ShowMain?.Invoke();
                    if (int.TryParse(A("tab"), out var tab)) ShowTab?.Invoke(tab);
                });
                return (200, "text/plain", "ok\n");

            case "/mini":
                await _ui.InvokeAsync(() => ShowMini?.Invoke());
                return (200, "text/plain", "ok\n");

            case "/perf":
                if (Perf == null) return (404, "text/plain", "not ready\n");
                return (200, "application/json", await _ui.InvokeAsync(() => Store.Serialize(Perf())));

            default:
                return (404, "text/plain", "unknown endpoint; GET / for help\n");
        }
    }

    private static Dictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var k = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var v = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    private string Help() => $$"""
        DOOMGUY API  (http://127.0.0.1:{{Port}})
        Every endpoint takes JSON, a form body, or query-string parameters. GET works as well as POST.

        /notify    message, [title], [level=info|success|warn|error], [sound=ITEMUP|none|...]
                   curl "http://127.0.0.1:{{Port}}/notify?message=Build+finished&level=success"
        /progress  id, [title], [percent=0-100], [state=running|done|failed|remove], [detail], [source]
                   curl -d "{"id":"bake","title":"Light bake","percent":40}" http://127.0.0.1:{{Port}}/progress
        /remind    text="stand up in 30m"  or  text + in=30m | at=17:30 | every=1h
        /claude    Claude Code hook payload (installed by: DoomGuy.exe --install-hooks)
        /state     JSON snapshot
        /gauge     name, value (0-100 or green/yellow/red): feeds a slot wired to PUSHED with that name
        /profile   [name=WORK]: switch the bar's wiring (no name: say which is active)
        /statusline  Claude Code status line JSON (plan limits, context); prints a short line back
        /show /mini  bring up the window ([tab=0-4]: tasks, remind, hud, log, setup) or the status bar
        /perf        memory, caches and frame cost since the last /perf (JSON)

        CLI: DoomGuy.exe notify "text" [--level success] [--title T]
             DoomGuy.exe remind "stand up in 30m"
             DoomGuy.exe progress <id> <percent|done|failed> [title]
        """;
}
