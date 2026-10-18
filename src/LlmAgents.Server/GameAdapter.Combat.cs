using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>Threat detection that needs game types (ballast flora) and attacking with tools.</summary>
public sealed partial class GameAdapter
{
    /// <summary>"ballast flora: 34 branches in Балласт [h5] (root), 6 in Насосная [h6]" for each plant that broke out.</summary>
    static List<string> DescribeFlora(Character c)
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Barotrauma.MapCreatures.Behavior.BallastFloraBehavior.EntityList.Where(f => f.Parent?.Submarine == c.Submarine && c.Submarine != null))
            {
                var live = f.Branches.Where(b => !b.Removed && b.Health > 0 && b.CurrentHull != null).ToList();
                if (!f.HasBrokenThrough || live.Count == 0) continue;
                var root = live.FirstOrDefault(b => b.IsRoot)?.CurrentHull;
                var rooms = live.GroupBy(b => b.CurrentHull!).OrderByDescending(g => g.Count()).Take(4)
                    .Select(g => $"{g.Count()} in {RoomId(g.Key)}{(g.Key == root ? " (root)" : "")}");
                list.Add($"ballast flora: {live.Count} branches - {string.Join(", ", rooms)}. It spreads and attacks; burn it: attack(target='ballast flora') with a welding tool or a flamer");
            }
        }
        catch (Exception e) { Log.Warn("flora: " + e.Message); }
        return list;
    }

    /// <summary>Tools that burn (ballast flora): welding tools, flamers... best first, carried ones first.</summary>
    static List<Item> FireTools(Character c, string? query) =>
        Carried(c).Concat(ItemQueries.Available(c)).Distinct()
            .Where(i => i.GetComponent<RepairTool>() is { FireDamage: > 0 })
            .Where(i => query == null || ItemQueries.MatchScore(i.Prefab, query) > 0)
            .OrderByDescending(i => i.GetRootInventoryOwner() == c ? 1 : 0)
            .ThenByDescending(i => i.GetComponent<RepairTool>()!.FireDamage)
            .ThenBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition))
            .ToList();

    /// <summary>Attack with a chosen tool or weapon: ballast flora (burnt with a fire tool) or a creature / intruder. Multi-step task.</summary>
    public string Attack(int botId, string target, string? weapon)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var t = target.Trim().ToLowerInvariant();
        bool flora = ItemRules.MeansFlora(t);
        var hostiles = Character.CharacterList.Where(x => !x.IsDead && !x.Removed && x != c && IsHostileTo(x, c) && x.Submarine == c.Submarine)
            .OrderBy(x => Vector2.DistanceSquared(x.WorldPosition, c.WorldPosition)).ToList();
        var floraLeft = AIObjectiveBurnFlora.LiveBranches(c.Submarine).Count();
        if (!flora && t is "" or "nearest" or "any" or "all" or "ближайший" or "все" or "угроза" or "threat")
            flora = hostiles.Count == 0 && floraLeft > 0;

        if (flora)
        {
            if (floraLeft == 0) return "ok (no ballast flora aboard)";
            var hull = IsRoomRef(target) ? ResolveRoom(target) : null;
            if (hull != null && !AIObjectiveBurnFlora.LiveBranches(c.Submarine, hull).Any()) return $"error: no ballast flora in {RoomId(hull)}";
            var tools = FireTools(c, weapon);
            if (tools.Count == 0)
                return weapon != null && FireTools(c, null).Any()
                    ? $"error: '{weapon}' does not burn; tools that do: {string.Join(", ", FireTools(c, null).Select(i => i.Name).Distinct().Take(4))}"
                    : "error: no welding tool or flamer aboard to burn the flora";
            return StartTask(c, new AttackTask(c, ai, tools[0], null, hull), evenOverPlayerOrders: true) + $" (with {tools[0].Name})";
        }

        if (hostiles.Count == 0) return floraLeft > 0 ? "error: no creature or intruder aboard; for the ballast flora use target='ballast flora'" : "ok (nothing hostile aboard)";
        var enemy = hostiles.FirstOrDefault(x => x.DisplayName.Contains(target.Trim(), StringComparison.OrdinalIgnoreCase)
                                                || x.SpeciesName.Value.Contains(target.Trim(), StringComparison.OrdinalIgnoreCase)
                                                || target.Contains(x.SpeciesName.Value, StringComparison.OrdinalIgnoreCase))
                    ?? hostiles[0];
        Item? gun = null;
        if (weapon != null)
        {
            gun = FindCarried(c, weapon) ?? ItemQueries.Find(c, weapon, i => i.GetComponent<RangedWeapon>() != null || i.GetComponent<MeleeWeapon>() != null
                                                                            || i.GetComponent<RepairTool>() != null, ClaimedByOthers(c)).FirstOrDefault();
            if (gun == null) return NotFound(c, weapon);
        }
        return StartTask(c, new AttackTask(c, ai, gun, enemy, null), evenOverPlayerOrders: true) + (gun != null ? $" (with {gun.Name})" : "");
    }

    /// <summary>What players marked with the middle mouse button: for the AI, for deconstruction, to be left alone.</summary>
}
