using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmAgents.Agents;

/// <summary>Plain, engine-independent snapshot of what one bot knows right now. Serialized to JSON for the LLM.</summary>
public sealed class Observation
{
    /// <summary>Why the bot is asked now (the events since its last decision), most important first.</summary>
    [JsonPropertyOrder(-2)]
    public List<string> Why { get; set; } = new();
    /// <summary>Orders from human players routed to this bot (highest priority). First in the JSON on purpose:
    /// small models pay most attention to the start.</summary>
    [JsonPropertyOrder(-1)]
    public List<string> PlayerOrders { get; set; } = new();
    /// <summary>What was said around this bot lately (players and crew, newest last): what a message without a name
    /// continues, what "there" and "it" refer to.</summary>
    [JsonPropertyOrder(-1)]
    public List<string> Conversation { get; set; } = new();
    /// <summary>What this bot set out to do (set_goal or a player's order), e.g. "craft 3 bandages for Ivan (set 2 min ago)".</summary>
    [JsonPropertyOrder(-1)]
    public string? Goal { get; set; }
    /// <summary>Steps of the bot's previous reply still waiting to be carried out (only shown when it is asked mid-plan).</summary>
    [JsonPropertyOrder(-1)]
    public string? Plan { get; set; }
    public string Name { get; set; } = "";
    public string Job { get; set; } = "";
    public int HealthPercent { get; set; } = 100;
    public int OxygenPercent { get; set; } = 100;
    /// <summary>null = outside the submarine.</summary>
    public string? Room { get; set; }
    public int RoomWaterPercent { get; set; }
    public int RoomFires { get; set; }
    public string CurrentOrder { get; set; } = "none";
    /// <summary>What the game's AI is actually doing right now for this bot, e.g. "fix leaks: Ballast [h5]", "idle".</summary>
    public string? Doing { get; set; }
    /// <summary>Multi-step task in progress (equip, refuel_reactor, craft, deconstruct, treat), if any.</summary>
    public string? CurrentTask { get; set; }
    /// <summary>Own injuries, e.g. "burn 30 (left arm), bleeding 12".</summary>
    public string? Injuries { get; set; }
    /// <summary>Items worn or held (equipped).</summary>
    public List<string> Equipped { get; set; } = new();
    /// <summary>Other items carried.</summary>
    public List<string> Inventory { get; set; } = new();
    /// <summary>Diving gear state, e.g. "diving suit on (oxygen 80%); gear locked (danger)".</summary>
    public string? Gear { get; set; }
    /// <summary>"3 free slots; hands: right Welding Tool, left empty".</summary>
    public string? InventorySpace { get; set; }
    /// <summary>Medical items available on the sub (own inventory first), with counts. Only filled when someone is hurt or for medics.</summary>
    public List<string>? MedicalSupplies { get; set; }
    public SubInfo? Submarine { get; set; }
    public List<RoomInfo> Rooms { get; set; } = new();
    public List<CrewInfo> Crew { get; set; } = new();
    public List<ThreatInfo> Threats { get; set; } = new();
    public List<string> Leaks { get; set; } = new();
    /// <summary>What players marked with the middle mouse button: for the AI, for deconstruction, to be ignored.</summary>
    public List<string> Marks { get; set; } = new();
    /// <summary>Ballast flora that broke out: branches per room, where the root is.</summary>
    public List<string> Infestations { get; set; } = new();

    // Filled in by the agent layer, not by the game adapter:
    public List<string> Inbox { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    /// <summary>Earlier orders with their age, e.g. "(3 min ago) Daniil: all to me".</summary>
    public List<string> EarlierOrders { get; set; } = new();
    public List<string> LastActionResults { get; set; } = new();
    /// <summary>"FULL AI CONTROL (given by X)" while a player has handed this bot full control.</summary>
    public string? Control { get; set; }
    /// <summary>Long-term memory from previous rounds.</summary>
    public MemoryView? Memory { get; set; }

    [JsonIgnore]
    public bool Urgent =>
        HealthPercent < 50 || OxygenPercent < 40 || RoomWaterPercent > 60 || RoomFires > 0 ||
        Threats.Count > 0 || Leaks.Count > 0 || Infestations.Count > 0 ||
        (Submarine?.ReactorFuelPercent is int fuel && fuel < 10) || (Submarine?.OxygenPercent is int o2 && o2 < 50) ||
        DangerWithoutSuit;

    /// <summary>The sub is in danger and this bot wears no diving gear: think now, put a suit on.</summary>
    [JsonIgnore]
    public bool DangerWithoutSuit => Gear != null && Gear.Contains("DANGER") && Gear.StartsWith("no diving gear");

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep non-latin names readable (and cheaper in tokens)
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    /// <summary>One line standing for this observation in the history of later requests.</summary>
    public string HistorySummary()
    {
        var parts = new List<string>();
        if (Why.Count > 0) parts.Add("why: " + string.Join(" | ", Why.Select(w => Tools.Clip(w, 140))));
        parts.Add($"you were in {Room ?? "outside"}, health {HealthPercent}%, order: {CurrentOrder}" + (Doing != null ? $", doing: {Doing}" : ""));
        if (Goal != null) parts.Add("goal: " + Goal);
        if (CurrentTask != null) parts.Add("task: " + CurrentTask);
        if (PlayerOrders.Count > 0) parts.Add("player orders: " + string.Join(" | ", PlayerOrders));
        if (Inbox.Count > 0) parts.Add("messages: " + string.Join(" | ", Inbox));
        if (LastActionResults.Count > 0) parts.Add("results: " + string.Join(" | ", LastActionResults.Select(r => Tools.Clip(r, 160))));
        var alerts = Threats.Count + Leaks.Count + Rooms.Count(r => r.Fires > 0 || r.WaterPercent >= 20);
        if (alerts > 0) parts.Add($"alerts: {Threats.Count} threats, {Leaks.Count} leaking rooms");
        return Tools.Clip(string.Join("; ", parts), 1000);
    }

    /// <summary>
    /// Shorter form for the LLM (about half the size): no empty lists or zero values, crew/threats as one line each,
    /// room ids as plain strings with a separate list of rooms that have water or fire. Small local models read
    /// it faster and lose fewer details (orders!) in it.
    /// </summary>
    public string ToCompactJson()
    {
        var o = JsonSerializer.SerializeToNode(this, Opts)!.AsObject();

        if (RoomWaterPercent == 0) o.Remove("roomWaterPercent");
        if (RoomFires == 0) o.Remove("roomFires");
        if (OxygenPercent >= 100) o.Remove("oxygenPercent");

        o["rooms"] = new JsonArray(Rooms.Select(r => (JsonNode)JsonValue.Create(r.Id)!).ToArray());
        var alerts = Rooms.Where(r => r.WaterPercent >= 5 || r.Fires > 0)
            .Select(r => r.Id + ": " + string.Join(", ", new[]
            {
                r.WaterPercent >= 5 ? $"water {r.WaterPercent}%" : null,
                r.Fires > 0 ? $"fire x{r.Fires}" : null,
            }.Where(x => x != null)))
            .ToList();
        if (alerts.Count > 0) o["roomAlerts"] = new JsonArray(alerts.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());

        o["crew"] = new JsonArray(Crew.Select(c => (JsonNode)JsonValue.Create(
            $"{c.Name} ({c.Job}{(c.IsPlayer ? ", HUMAN PLAYER" : "")})" +
            (c.Room != null ? $" in {c.Room}" : " outside") +
            (c.HealthPercent < 100 ? $", health {c.HealthPercent}%" : "") +
            (c.Order != "none" ? $", order: {c.Order}" : "") +
            (c.Injuries != null ? $", injuries: {c.Injuries}" : ""))!).ToArray());

        o["threats"] = new JsonArray(Threats.Select(t => (JsonNode)JsonValue.Create(
            $"{t.Kind} {t.DistanceMeters}m" + (t.Room != null ? $" in {t.Room}" : " outside"))!).ToArray());

        // drop everything empty
        foreach (var key in o.Select(kv => kv.Key).ToList())
        {
            var v = o[key];
            if (v == null || (v is JsonArray arr && arr.Count == 0) || (v is JsonValue jv && jv.TryGetValue<string>(out var str) && str.Length == 0))
                o.Remove(key);
        }
        return o.ToJsonString(Opts);
    }
}

public sealed class MemoryView
{
    public List<string>? Facts { get; set; }
    /// <summary>Summaries of the latest rounds, oldest first.</summary>
    public List<string>? PastRounds { get; set; }
    /// <summary>"Name: feeling - note".</summary>
    public List<string>? Relationships { get; set; }
    /// <summary>"Player: preference", for players aboard.</summary>
    public List<string>? PlayerPreferences { get; set; }

    [JsonIgnore]
    public bool IsEmpty => (Facts?.Count ?? 0) + (PastRounds?.Count ?? 0) + (Relationships?.Count ?? 0) + (PlayerPreferences?.Count ?? 0) == 0;
}

public sealed record RoomInfo(string Id, int WaterPercent, int Fires);

public sealed record CrewInfo(string Name, string Job, string? Room, int HealthPercent, bool IsPlayer, string Order, string? Injuries = null);

public sealed record ThreatInfo(string Kind, int DistanceMeters, string? Room);

public sealed record SubInfo(int? ReactorOutputPercent, int? DepthMeters, int HullBreaches,
    int? ReactorFuelPercent = null, int? ReactorTemperaturePercent = null, int? FuelRodsAboard = null,
    int? OxygenPercent = null, string? Power = null);
