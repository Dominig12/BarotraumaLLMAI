using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

public sealed partial class GameAdapter
{
    internal static bool IsBody(Character c) => !c.Removed && (c.IsDead || c.IsIncapacitated || c.Stun > 0);
    internal static string BodyLabel(Character c) => $"{c.Name} ({c.SpeciesName.Value}) [c{c.ID}]";

    static IEnumerable<Character> NearbyBodies(Character bot) => Character.CharacterList.Where(c => c != bot && IsBody(c)
        && (bot.Submarine != null && c.Submarine == bot.Submarine
            || Vector2.DistanceSquared(c.WorldPosition, bot.WorldPosition) <= ThreatRadiusMeters * ThreatRadiusMeters * UnitsPerMeter * UnitsPerMeter));

    string InspectBodies(Character bot, string query)
    {
        var bodies = NearbyBodies(bot).Where(b => BodyRules.MatchScore(query, b.ID, b.Name, b.SpeciesName.Value) > 0)
            .OrderBy(b => Vector2.DistanceSquared(bot.WorldPosition, b.WorldPosition)).Take(12).ToList();
        if (bodies.Count == 0) return "no matching corpses or incapacitated characters nearby";
        return string.Join("; ", bodies.Select(b => DescribeBody(bot, b, contents: true)))
            + ". Use drag_body(target='[cID]', to=<room or crew member>) or loot_body(target='[cID]', item=<optional filter>).";
    }

    static string DescribeBody(Character bot, Character body, bool contents)
    {
        var description = BodyLabel(body) + (body.IsDead ? " dead" : " incapacitated")
            + (body.CurrentHull is { } h ? " in " + RoomId(h) : $" outside ({Vector2.Distance(bot.WorldPosition, body.WorldPosition) / UnitsPerMeter:0}m)");
        if (!contents) return description;
        if (body.Inventory == null) return description + "; loot: no inventory";
        var items = string.Join(", ", body.Inventory.AllItems.Distinct().GroupBy(i => i.Prefab).Take(10)
            .Select(g => $"{ItemQueries.Label(g.Key)} x{g.Count()}"));
        return description + "; loot: " + (items.Length > 0 ? items : "empty");
    }

    Character? FindBody(Character bot, string query) => NearbyBodies(bot)
        .Select(b => (Body: b, Score: BodyRules.MatchScore(query, b.ID, b.Name, b.SpeciesName.Value)))
        .Where(x => x.Score >= 3 || query.Trim() == "nearest").OrderByDescending(x => x.Score)
        .ThenBy(x => Vector2.DistanceSquared(bot.WorldPosition, x.Body.WorldPosition)).Select(x => x.Body).FirstOrDefault();

    string? BodyBusy(Character bot, Character body) =>
        body.SelectedBy is { Removed: false } other && other != bot ? $"error: {BodyLabel(body)} is being held by {other.Name}"
        : tasks.Values.OfType<BodyTask>().Any(t => t.Bot != bot && t.Target == body) ? $"error: another bot is already working on {BodyLabel(body)}" : null;

    public string DragBody(int botId, string target, string to)
    {
        if (Find(botId) is not { } bot || !Ai(bot, out var ai)) return "error: character gone";
        var body = FindBody(bot, target);
        if (body == null) return $"error: no body '{target}'; inspect(kind='bodies') lists nearby bodies";
        if (BodyBusy(bot, body) is { } busy) return busy;
        if (!body.CanBeDraggedBy(bot)) return $"error: {BodyLabel(body)} cannot be dragged";
        ISpatialEntity? destination = ResolveRoom(to.Trim());
        destination ??= FindCrew(to.Trim(), bot, includeSelf: false);
        if (destination == null) return $"error: no destination '{to}'; use a room id or crew member name";
        return StartTask(bot, new DragBodyTask(bot, ai, body, destination, to));
    }

    public string LootBody(int botId, string target, string? item = null, int count = 0, ItemFilter? filter = null)
    {
        if (Find(botId) is not { } bot || !Ai(bot, out var ai)) return "error: character gone";
        var body = FindBody(bot, target);
        if (body == null) return $"error: no body '{target}'; inspect(kind='bodies') lists nearby bodies";
        if (BodyBusy(bot, body) is { } busy) return busy;
        if (body.Inventory == null) return $"error: {BodyLabel(body)} has no loot inventory";
        return StartTask(bot, new LootBodyTask(bot, ai, body, item, Math.Clamp(count == 0 ? Tools.MaxMoveCount : count, 1, Tools.MaxMoveCount), filter));
    }
}
