using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmAgents.Agents;

/// <summary>Plain, engine-independent snapshot of what one bot knows right now. Serialized to JSON for the LLM.</summary>
public sealed class Observation
{
    public string Name { get; set; } = "";
    public string Job { get; set; } = "";
    public int HealthPercent { get; set; } = 100;
    public int OxygenPercent { get; set; } = 100;
    /// <summary>null = outside the submarine.</summary>
    public string? Room { get; set; }
    public int RoomWaterPercent { get; set; }
    public int RoomFires { get; set; }
    public string CurrentOrder { get; set; } = "none";
    /// <summary>Multi-step task in progress (equip, refuel_reactor, craft, deconstruct, treat), if any.</summary>
    public string? CurrentTask { get; set; }
    /// <summary>Own injuries, e.g. "burn 30 (left arm), bleeding 12".</summary>
    public string? Injuries { get; set; }
    public List<string> Inventory { get; set; } = new();
    /// <summary>Medical items available on the sub (own inventory first), with counts. Only filled when someone is hurt or for medics.</summary>
    public List<string>? MedicalSupplies { get; set; }
    public SubInfo? Submarine { get; set; }
    public List<RoomInfo> Rooms { get; set; } = new();
    public List<CrewInfo> Crew { get; set; } = new();
    public List<ThreatInfo> Threats { get; set; } = new();
    public List<string> Leaks { get; set; } = new();

    // Filled in by the agent layer, not by the game adapter:
    /// <summary>Orders from human players routed to this bot (highest priority).</summary>
    public List<string> PlayerOrders { get; set; } = new();
    public List<string> Inbox { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public List<string> LastActionResults { get; set; } = new();

    [JsonIgnore]
    public bool Urgent =>
        HealthPercent < 50 || OxygenPercent < 40 || RoomWaterPercent > 60 || RoomFires > 0 ||
        Threats.Count > 0 || Leaks.Count > 0 ||
        (Submarine?.ReactorFuelPercent is int fuel && fuel < 10) || (Submarine?.OxygenPercent is int o2 && o2 < 50);

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep non-latin names readable (and cheaper in tokens)
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);
}

public sealed record RoomInfo(string Id, int WaterPercent, int Fires);

public sealed record CrewInfo(string Name, string Job, string? Room, int HealthPercent, bool IsPlayer, string Order, string? Injuries = null);

public sealed record ThreatInfo(string Kind, int DistanceMeters, string? Room);

public sealed record SubInfo(int? ReactorPercent, int? DepthMeters, int HullBreaches,
    int? ReactorFuelPercent = null, int? ReactorTemperaturePercent = null, int? FuelRodsAboard = null,
    int? OxygenPercent = null, string? Power = null);
