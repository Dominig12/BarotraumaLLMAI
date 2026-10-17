using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// Attack with a chosen tool or weapon. Ballast flora: burn it (<see cref="AIObjectiveBurnFlora"/>) with a welding tool,
/// a flamer or any other tool that burns. A creature or an intruder: take the weapon first (if one was named), then the
/// vanilla combat objective fights with the best weapon carried.
/// </summary>
internal sealed class AttackTask : BotTask
{
    readonly Item? weapon;
    readonly Character? enemy;
    readonly Hull? floraArea;
    readonly int branchesAtStart;
    AIObjectiveBurnFlora? burn;
    public override string Name => enemy != null ? $"attack {enemy.DisplayName}" : "burn the ballast flora";
    protected override double TimeoutSeconds => enemy != null ? 240 : 420;
    public override IEnumerable<Item> Claimed => weapon != null ? new[] { weapon } : Enumerable.Empty<Item>();

    public AttackTask(Character bot, HumanAIController ai, Item? weapon, Character? enemy, Hull? floraArea) : base(bot, ai)
    {
        this.weapon = weapon;
        this.enemy = enemy;
        this.floraArea = floraArea;
        branchesAtStart = AIObjectiveBurnFlora.LiveBranches(bot.Submarine, floraArea).Count();
    }

    protected override string? Update()
    {
        if (enemy == null)
        {
            int left = AIObjectiveBurnFlora.LiveBranches(Bot.Submarine, floraArea).Count();
            if (left == 0) return $"ok: the ballast flora{(floraArea != null ? " in " + GameAdapter.RoomId(floraArea) : "")} is gone (burnt {branchesAtStart} branches)";
            if (!HasObjective) { Run(burn = new AIObjectiveBurnFlora(Bot, Ai.ObjectiveManager, weapon!, floraArea), "burning the ballast flora"); return null; }
            if (burn != null) Step = burn.Step;
            if (ObjectiveFailed || ObjectiveDone)
                return branchesAtStart - left > 0
                    ? $"ok: burnt {branchesAtStart - left} of {branchesAtStart} branches, {left} left ({burn?.Problem ?? "stopped"})"
                    : $"failed: {burn?.Problem ?? "could not burn the flora"}";
            return null;
        }

        if (enemy.IsDead || enemy.Removed) return $"ok: {enemy.DisplayName} is dead";
        if (weapon != null && !weapon.Removed && weapon.GetRootInventoryOwner() != Bot)
        {
            if (!HasObjective) Run(new AIObjectiveGetItem(Bot, weapon, Ai.ObjectiveManager, equip: true) { SpeakIfFails = false }, $"getting {weapon.Name}");
            else if (ObjectiveFailed) { ClearObjective(); return $"failed: could not get {weapon.Name}"; }
            else if (ObjectiveDone) ClearObjective();
            return null;
        }
        if (!HasObjective || ObjectiveDone)
        {
            Run(new AIObjectiveCombat(Bot, enemy, AIObjectiveCombat.CombatMode.Offensive, Ai.ObjectiveManager), $"fighting {enemy.DisplayName}");
            return null;
        }
        if (ObjectiveFailed) return enemy.IsDead ? $"ok: {enemy.DisplayName} is dead" : $"failed: could not fight {enemy.DisplayName} (out of reach or no usable weapon)";
        return null;
    }
}
