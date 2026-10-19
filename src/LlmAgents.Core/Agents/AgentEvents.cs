using System;
using System.Collections.Generic;
using System.Linq;

namespace LlmAgents.Agents;

/// <summary>
/// Why a bot should think. The model is asked only when one of these happens (plus a slow heartbeat for bots
/// that must act on their own); every decision shows the events as 'why'.
/// </summary>
public enum EventKind
{
    /// <summary>A chat order from a player, or an order from an AI crew member in command.</summary>
    Order,
    /// <summary>Full AI control was given or taken back.</summary>
    Control,
    /// <summary>A player gave an order through the command menu.</summary>
    MenuOrder,
    /// <summary>A new dangerous or important situation that concerns this bot.</summary>
    Danger,
    /// <summary>A task or a plan step failed (the rest of the plan was cancelled).</summary>
    TaskFailed,
    /// <summary>A task finished / the plan is done: time to see what is still missing.</summary>
    TaskDone,
    /// <summary>An inspect() answer is ready.</summary>
    Lookup,
    /// <summary>The previous reply could not be used (no valid tool call) while an order was waiting.</summary>
    Retry,
    /// <summary>The plan has been running for a long time.</summary>
    PlanReview,
    /// <summary>First look around at the start of the round.</summary>
    RoundStart,
    /// <summary>A message from another AI crew member.</summary>
    Mail,
    /// <summary>The danger is over (time to take the suit off, go back to normal duties).</summary>
    Calm,
    /// <summary>Opted-in review of the bot's saved desired inventory layout.</summary>
    InventoryReview,
    /// <summary>Nothing happened for a while (only bots that act on their own: full control or autonomy "always").</summary>
    Heartbeat,
}

public sealed record AgentEvent(EventKind Kind, string Text, double At);

/// <summary>Priorities and delays of the events. Lower tier = more important.</summary>
public static class EventPolicy
{
    public static int Tier(EventKind k) => k switch
    {
        EventKind.Order or EventKind.Control or EventKind.MenuOrder => 0,
        EventKind.Danger or EventKind.TaskFailed => 1,
        EventKind.TaskDone or EventKind.Lookup or EventKind.Retry or EventKind.PlanReview or EventKind.RoundStart => 2,
        EventKind.Mail or EventKind.Calm => 3,
        _ => 4,
    };

    /// <summary>How long to wait after the event before asking, so related events land in one request
    /// (a player typing two lines, a burst of messages, a breach and the water that follows).</summary>
    public static double Delay(EventKind k, ModSettings s) => k switch
    {
        EventKind.Order or EventKind.Control => 0,
        EventKind.MenuOrder => 0.5,   // several clicks in the command menu land in one request
        EventKind.Danger => 0,   // the situation check itself runs only every urgencyCheckSeconds
        EventKind.TaskDone => 0.3,
        EventKind.Retry => Math.Max(0.5, s.Global.RetrySeconds),
        EventKind.Mail => Math.Max(0, s.Global.MailDelaySeconds),
        EventKind.Calm => 5,
        _ => 0,
    };

    /// <summary>Events that may interrupt a plan in progress. The rest waits until the plan is done.</summary>
    public static bool InterruptsPlan(EventKind k) => Tier(k) <= 1 || k == EventKind.PlanReview;

    public static string Label(EventKind k) => k switch
    {
        EventKind.Order => "order",
        EventKind.Control => "control",
        EventKind.MenuOrder => "menu order",
        EventKind.Danger => "danger",
        EventKind.TaskFailed => "failed",
        EventKind.TaskDone => "done",
        EventKind.Lookup => "lookup",
        EventKind.Retry => "retry",
        EventKind.PlanReview => "plan review",
        EventKind.RoundStart => "round start",
        EventKind.Mail => "message",
        EventKind.Calm => "calm",
        EventKind.InventoryReview => "inventory review",
        _ => "heartbeat",
    };
}

/// <summary>One condition of the bot's situation, e.g. "fire" = "fire in your room Engine [h2]".</summary>
/// <param name="Danger">Counts as danger (its end is reported as "the danger is over").</param>
public sealed record SituationFlag(string Key, string Text, bool Danger);

/// <summary>
/// What in an observation is worth waking a bot for, by job: a breach matters to mechanics and whoever is in charge,
/// low fuel to engineers, a badly hurt crewmate to medics; fire or flooding in your own room or a monster aboard
/// matters to everyone. The vanilla AI already reacts to all of it; the model is asked only where it adds something.
/// Only flags that newly appear wake the bot (see AgentManager), so a long fight is one event, not one per second.
/// </summary>
public static class Situation
{
    static readonly System.Text.RegularExpressions.Regex SuitTank = new(@"oxygen (\d+)%\)");

    public static List<SituationFlag> Assess(Observation o, string job, bool aiControl)
    {
        bool captain = job == "captain";
        bool medic = job == "medicaldoctor";
        bool command = captain || aiControl;
        var flags = new List<SituationFlag>();

        if (o.HealthPercent < 50) flags.Add(new("hurt", $"you are badly hurt (health {o.HealthPercent}%)", false));
        if (o.OxygenPercent < 40) flags.Add(new("air", $"you are running out of air (oxygen {o.OxygenPercent}%)", false));
        if (o.RoomWaterPercent > 60) flags.Add(new("flood", $"your room {o.Room} is flooding (water {o.RoomWaterPercent}%)", true));
        if (o.RoomFires > 0) flags.Add(new("fire", $"fire in your room {o.Room}", true));
        if (o.Threats.Count > 0)
        {
            var t = o.Threats[0];
            flags.Add(new("threat", $"hostile {t.Kind} {t.DistanceMeters} m away{(t.Room != null ? " in " + t.Room : " outside")}" +
                                    (o.Threats.Count > 1 ? $" (+{o.Threats.Count - 1} more)" : ""), true));
        }
        // the diving suit you wear is out of air: a full tank (refill) before it matters
        if (o.Gear != null && (o.Gear.Contains("NO oxygen tank") || SuitTank.Match(o.Gear) is { Success: true } m && int.Parse(m.Groups[1].Value) < 15))
            flags.Add(new("tank", "your worn diving gear is almost out of oxygen; refill() replaces depleted supplies in your actual carried gear", o.RoomWaterPercent > 50));
        // ballast flora: the mechanics, whoever is in charge, and anyone it grows next to (the vanilla AI does not fight it)
        if (o.Infestations.Count > 0)
        {
            bool here = o.Room != null && o.Infestations.Any(i => i.Contains(o.Room, StringComparison.OrdinalIgnoreCase));
            if (here || job is "mechanic" or "engineer" or "assistant" || command)
                flags.Add(new("flora", (here ? "ballast flora right here: " : "") + o.Infestations[0], here));
        }
        // no suit with monsters around or water coming in where you are (a far-away breach is the mechanics' business)
        bool ownRoomBreached = o.Room != null && o.Leaks.Any(l => l.StartsWith(o.Room, StringComparison.OrdinalIgnoreCase));
        if (o.DangerWithoutSuit && (o.Threats.Count > 0 || o.RoomWaterPercent >= 10 || ownRoomBreached))
            flags.Add(new("suit", "danger and you wear no diving gear", true));
        // the vanilla AI already welds, extinguishes and pumps on its own: only the people in charge are woken
        if (o.Leaks.Count > 0 && (job == "mechanic" || command))
            flags.Add(new("breach", "hull breach: " + string.Join(", ", o.Leaks.Take(3)) + (o.Leaks.Count > 3 ? ", ..." : ""), true));
        var otherFires = o.Rooms.Where(r => r.Fires > 0 && r.Id != o.Room).Select(r => r.Id).ToList();
        if (otherFires.Count > 0 && command)
            flags.Add(new("subfire", "fire in " + string.Join(", ", otherFires.Take(3)), true));
        if (o.Submarine?.ReactorFuelPercent is int fuel && fuel < 10 && (job == "engineer" || command))
            flags.Add(new("fuel", $"reactor fuel is low ({fuel}%)", false));
        if (o.Submarine?.OxygenPercent is int air && air < 50 && (job == "engineer" || command))
            flags.Add(new("subair", $"oxygen aboard is low ({air}%)", false));
        if (medic || aiControl)
        {
            var hurt = o.Crew.Where(c => c.HealthPercent < 50).OrderBy(c => c.HealthPercent).FirstOrDefault();
            if (hurt != null)
                flags.Add(new("injured", $"{hurt.Name} is badly hurt (health {hurt.HealthPercent}%){(hurt.Room != null ? " in " + hurt.Room : "")}", false));
        }
        return flags;
    }
}
