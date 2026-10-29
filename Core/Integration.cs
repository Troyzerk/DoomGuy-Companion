using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DoomGuy.Core;

/// <summary>Adds DoomGuy's async curl hooks to the user's Claude Code settings, or takes them out again.</summary>
public static class ClaudeHooks
{
    public static string SettingsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static readonly string[] ToolEvents = ["PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest"];
    private static readonly string[] OtherEvents = ["SessionStart", "UserPromptSubmit", "Notification", "Stop", "StopFailure", "SessionEnd"];

    public static string Command(int port) =>
        $"curl -s -m 2 -H \"Content-Type: application/json\" --data-binary @- http://127.0.0.1:{port}/claude >/dev/null 2>&1 || exit 0";

    private static bool IsOurs(JsonNode? hook) =>
        hook?["command"]?.GetValue<string>() is { } c && c.Contains("127.0.0.1:") && c.Contains("/claude");

    public static bool Installed()
    {
        try
        {
            var root = Load();
            var hooks = root?["hooks"] as JsonObject;
            return hooks != null && OtherEvents.Concat(ToolEvents).All(ev =>
                hooks[ev] is JsonArray groups && groups.Any(g => g?["hooks"] is JsonArray hs && hs.Any(IsOurs)));
        }
        catch { return false; }
    }

    public static void Install(int port)
    {
        var root = Load() ?? new JsonObject();
        Strip(root);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var ev in OtherEvents.Concat(ToolEvents))
        {
            var groups = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = groups;
            var group = new JsonObject();
            if (ToolEvents.Contains(ev)) group["matcher"] = "*";
            group["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = Command(port),
                ["async"] = true,
            });
            groups.Add(group);
        }
        Write(root);
    }

    // ---------------------------------------------------------------- status line (plan limits, exact context)

    public static string StatusCommand(int port) =>
        $"curl -s -m 2 -H \"Content-Type: application/json\" --data-binary @- http://127.0.0.1:{port}/statusline 2>/dev/null || true";

    private static bool IsOurStatus(JsonNode? s) =>
        s?["command"]?.GetValue<string>() is { } c && c.Contains("127.0.0.1:") && c.Contains("/statusline");

    public static bool StatusLineInstalled()
    {
        try { return IsOurStatus(Load()?["statusLine"]); }
        catch { return false; }
    }

    /// <summary>Someone else's status line is already set (we never replace it).</summary>
    public static bool OtherStatusLine()
    {
        try { return Load()?["statusLine"] is { } s && !IsOurStatus(s); }
        catch { return false; }
    }

    public static void InstallStatusLine(int port)
    {
        var root = Load() ?? new JsonObject();
        if (root["statusLine"] is { } existing && !IsOurStatus(existing))
            throw new InvalidOperationException("A status line is already set in Claude settings; DoomGuy won't replace it.");
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = StatusCommand(port), ["padding"] = 0 };
        Write(root);
    }

    public static void UninstallStatusLine()
    {
        var root = Load();
        if (root == null || !IsOurStatus(root["statusLine"])) return;
        root.Remove("statusLine");
        Write(root);
    }

    public static void Uninstall()
    {
        var root = Load();
        if (root == null) return;
        Strip(root);
        Write(root);
    }

    private static void Strip(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var ev in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[ev] is not JsonArray groups) continue;
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i]?["hooks"] is not JsonArray hs) continue;
                for (int j = hs.Count - 1; j >= 0; j--) if (IsOurs(hs[j])) hs.RemoveAt(j);
                if (hs.Count == 0) groups.RemoveAt(i);
            }
            if (groups.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static JsonObject? Load()
    {
        if (!File.Exists(SettingsPath)) return null;
        var text = File.ReadAllText(SettingsPath);
        if (text.Trim().Length == 0) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
               ?? throw new InvalidDataException("settings.json is not a JSON object.");
    }

    private static void Write(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".doomguy-backup", true);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(SettingsPath + ".tmp", json + Environment.NewLine);
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
}

/// <summary>The HKCU Run entry that starts DoomGuy (as the status bar) with Windows.</summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("DoomGuy") is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue("DoomGuy", $"\"{Environment.ProcessPath}\" --mini");
            else k.DeleteValue("DoomGuy", false);
        }
    }
}
