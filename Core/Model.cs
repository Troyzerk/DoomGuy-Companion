using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoomGuy.Core;

public enum TaskState { Idle, Working, NeedsYou, Done, Failed }

/// <summary>One line in the TASKS list: a Claude Code session or a job reported over the API.</summary>
public sealed class TaskItem
{
    public string Id { get; set; } = "";
    public bool IsClaude { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Cwd { get; set; } = "";
    public TaskState State { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    public int ToolCount { get; set; }
    public int Percent { get; set; } = -1;
    public bool Unseen { get; set; }
}

public sealed class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    public DateTime Due { get; set; }
    public long EverySeconds { get; set; }
    public bool Firing { get; set; }
    [JsonIgnore] public DateTime NextNag { get; set; }
}

public sealed class LogLine
{
    public DateTime At { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    public string Level { get; set; } = "info";
}

public sealed class Settings
{
    public string? WadPath { get; set; }
    public int Port { get; set; } = 6666;
    public bool Sound { get; set; } = true;
    public int Volume { get; set; } = 6;
    public string MiniMode { get; set; } = "bar";
    public int MiniScale { get; set; } = 2;
    public int FaceScale { get; set; } = 4;
    public int WindowScale { get; set; } = 3;
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    public double? MainLeft { get; set; }
    public double? MainTop { get; set; }
    /// <summary>The main window's width (DIPs) after the last resize; unset = WindowScale.</summary>
    public double? MainWidth { get; set; }
    public bool StartMinimized { get; set; }
    public bool HideOverFullscreen { get; set; }
    public bool God { get; set; }
    public bool HdFace { get; set; } = true;
    public bool TitleScreen { get; set; } = true;
    /// <summary>Gauge wiring per profile (HOME, WORK, ...) and which one is active.</summary>
    public List<Profile> Profiles { get; set; } = [];
    public string Profile { get; set; } = "HOME";
}

/// <summary>What survives a restart, besides settings.</summary>
public sealed class SavedState
{
    public List<TaskItem> Tasks { get; set; } = [];
    public List<Reminder> Reminders { get; set; } = [];
    public List<LogLine> Log { get; set; } = [];
    public DateTime Day { get; set; } = DateTime.Today;
    public int DoneToday { get; set; }
    public int StartedToday { get; set; }
    public int MessagesToday { get; set; }
    public int ErrorsToday { get; set; }
    /// <summary>Values pushed to /gauge, by name, so a restart doesn't blank them.</summary>
    public Dictionary<string, int> Pushed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class Store
{
    public static string Dir { get; } = Environment.GetEnvironmentVariable("DOOMGUY_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoomGuy");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string file) where T : new()
    {
        try
        {
            var p = Path.Combine(Dir, file);
            if (File.Exists(p)) return JsonSerializer.Deserialize<T>(File.ReadAllText(p), Json) ?? new T();
        }
        catch { }
        return new T();
    }

    public static void Save<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var p = Path.Combine(Dir, file);
            File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(value, Json));
            File.Move(p + ".tmp", p, true);
        }
        catch { }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
