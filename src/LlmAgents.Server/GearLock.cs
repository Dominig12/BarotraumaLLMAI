using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// Keeps bots from taking their diving gear off. Vanilla bots remove the suit as soon as their room is dry
/// (HumanAIController.UnequipUnnecessaryItems); in combat that gets them killed when the hull is breached
/// a moment later. While a bot is locked, that vanilla method is skipped, unless the bot is running out of
/// air (then vanilla logic must be free to take an empty suit off).
/// Locked = the LLM asked for it (keep_gear), or the sub is in danger and keepDivingGearInDanger is on.
/// </summary>
internal static class GearLock
{
    static readonly HashSet<ushort> manual = new();
    static readonly HashSet<Submarine> subsInDanger = new();
    static bool auto = true;
    static Harmony? harmony;

    public static bool Patched { get; private set; }
    internal static Func<Character, bool>? PreserveLoadoutEquipment;

    public static void SetManual(Character c, bool on)
    {
        if (on) manual.Add(c.ID); else manual.Remove(c.ID);
    }

    public static bool IsManual(Character c) => manual.Contains(c.ID);
    public static bool InDanger(Character c) => c.Submarine != null && subsInDanger.Contains(c.Submarine);
    public static bool IsLocked(Character c) => IsManual(c) || (auto && InDanger(c));

    public static void Reset()
    {
        manual.Clear();
        subsInDanger.Clear();
    }

    /// <summary>Recomputes which subs are in danger. Cheap enough to run about once a second.</summary>
    public static void UpdateDanger(ModSettings settings)
    {
        auto = settings.Global.KeepDivingGearInDanger;
        subsInDanger.Clear();
        if (!auto) return;
        float radius = (float)settings.Global.DangerMonsterRadiusMeters * 100f;

        foreach (var sub in Submarine.Loaded)
        {
            if (sub == null || sub.Info == null || sub.Info.IsOutpost) continue;
            if (IsInDanger(sub, radius)) subsInDanger.Add(sub);
        }
    }

    static bool IsInDanger(Submarine sub, float radius)
    {
        // breaches
        if (Gap.GapList.Any(g => g.Submarine == sub && g.ConnectedWall != null && !g.IsRoomToRoom && g.Open > 0.01f)) return true;
        // water aboard
        var hulls = Hull.HullList.Where(h => h.Submarine == sub && !h.IsWetRoom).ToList();
        float volume = hulls.Sum(h => h.Volume);
        if (volume > 0 && hulls.Sum(h => h.WaterVolume) / volume > 0.05f) return true;

        float subRadius = Math.Max(sub.Borders.Width, sub.Borders.Height) / 2f;
        foreach (var x in Character.CharacterList)
        {
            if (x.IsDead || x.Removed) continue;
            bool monster = x.AIController is EnemyAIController;
            bool intruder = x.IsHuman && x.Submarine == sub && x.TeamID != sub.TeamID && x.TeamID != CharacterTeamType.FriendlyNPC;
            if (intruder) return true;
            if (monster && Vector2.Distance(x.WorldPosition, sub.WorldPosition) < subRadius + radius) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ patch

    public static void Patch()
    {
        if (Patched) return;
        var target = AccessTools.Method(typeof(HumanAIController), "UnequipUnnecessaryItems");
        if (target == null) throw new MissingMethodException("HumanAIController.UnequipUnnecessaryItems not found");
        harmony ??= new Harmony("llmagents.gearlock");
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(GearLock), nameof(Prefix)));
        Patched = true;
    }

    public static void Unpatch()
    {
        PreserveLoadoutEquipment = null;
        if (!Patched) return;
        harmony?.UnpatchSelf();
        Patched = false;
    }

    /// <summary>Return false = skip the vanilla method.</summary>
    static bool Prefix(HumanAIController __instance)
    {
        try
        {
            var c = __instance.Character;
            if (c == null) return true;
            if (c.IsLowInOxygen) return true; // suffocating: let vanilla deal with an empty suit
            return !IsLocked(c) && PreserveLoadoutEquipment?.Invoke(c) != true;
        }
        catch
        {
            return true;
        }
    }
}
