using System.Text.Json;
using DoomGuy.Core;
using Xunit;

namespace DoomGuy.Tests;

/// <summary>The hub's rules, driven the way the API drives it. Nothing here ticks, so nothing is saved to disk.</summary>
public class HubTests
{
    private static Hub NewHub() => new(new Settings { Sound = false }, new SavedState());

    private static void Hook(Hub hub, string json) => hub.HandleClaude(JsonDocument.Parse(json).RootElement);

    private static void Ask(Hub hub, string session) => Hook(hub,
        $$$"""{"hook_event_name":"PermissionRequest","session_id":"{{{session}}}","cwd":"C:\\Work\\Game","tool_name":"Bash","tool_input":{"command":"git push"}}""");

    [Fact]
    public void A_permission_request_needs_you_and_costs_health()
    {
        var hub = NewHub();
        Assert.Equal(100, hub.Health);
        Ask(hub, "s1");
        var t = Assert.Single(hub.Tasks);
        Assert.Equal(TaskState.NeedsYou, t.State);
        Assert.Equal("GAME", t.Name);
        Assert.Equal(75, hub.Health);
    }

    [Fact]
    public void Something_waiting_on_you_cannot_be_dismissed()
    {
        var hub = NewHub();
        Ask(hub, "s1");
        hub.Dismiss(hub.Tasks[0]);
        hub.MarkAllSeen();
        hub.ClearFinished();
        Assert.Single(hub.Tasks);
        Assert.Equal(1, hub.NeedsYouCount);
    }

    [Fact]
    public void Answering_moves_the_session_on()
    {
        var hub = NewHub();
        Ask(hub, "s1");
        Hook(hub, """{"hook_event_name":"PostToolUse","session_id":"s1","cwd":"C:\\Work\\Game","tool_name":"Bash"}""");
        Assert.Equal(TaskState.Working, hub.Tasks[0].State);
        Assert.Equal(100, hub.Health);

        // Once it isn't waiting, it can go.
        Hook(hub, """{"hook_event_name":"Stop","session_id":"s1"}""");
        hub.Dismiss(hub.Tasks[0]);
        Assert.Empty(hub.Tasks);
    }

    [Fact]
    public void A_finished_task_is_announced()
    {
        var hub = NewHub();
        Hook(hub, """{"hook_event_name":"UserPromptSubmit","session_id":"s1","cwd":"C:\\Work\\Game","prompt":"fix the build"}""");
        Hook(hub, """{"hook_event_name":"Stop","session_id":"s1"}""");
        var a = Assert.Single(hub.Announcements);
        Assert.Equal("GAME DONE!", a.Headline);
        Assert.True(hub.Tasks[0].Unseen);
        Assert.Equal(1, hub.State.DoneToday);
    }

    [Fact]
    public void Jobs_report_progress_and_finish()
    {
        var hub = NewHub();
        hub.Progress("bake", "Light bake", 40, "running", "deck 3", "");
        var t = Assert.Single(hub.Tasks);
        Assert.Equal((TaskState.Working, 40), (t.State, t.Percent));
        hub.Progress("bake", "", 100, "done", "", "");
        Assert.Equal(TaskState.Done, t.State);
        hub.ClearFinished();
        Assert.Empty(hub.Tasks);
    }

    [Theory]
    [InlineData(0, false, 5, 100)]
    [InlineData(1, false, 5, 80)]
    [InlineData(5, false, 5, 0)]
    [InlineData(9, false, 5, 0)]
    [InlineData(3, false, 10, 70)]
    [InlineData(70, true, 5, 30)]
    public void Invert_turns_a_count_or_percent_into_what_is_left(int value, bool percent, int emptyAt, int expected) =>
        Assert.Equal(expected, Gauges.Invert(value, percent, emptyAt));

    [Fact]
    public void Health_on_working_tasks_inverted_drops_a_fifth_per_job()
    {
        var hub = NewHub();
        var p = hub.Gauges.Active;
        p.Slots["health"] = new SlotBinding { Source = "working", Args = new(StringComparer.OrdinalIgnoreCase) { ["INVERT"] = "ON" } };
        Assert.Equal(100, hub.Health);
        hub.Progress("a", "A", 10, "running", "", "");
        hub.Progress("b", "B", 10, "running", "", "");
        Assert.Equal(60, hub.Health);
    }
}
