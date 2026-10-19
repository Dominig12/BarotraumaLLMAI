using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

internal sealed class RefuelReactorTask : BotTask
{
    readonly Reactor reactor;
    readonly ItemContainer container;
    readonly Item rod;
    readonly List<string> removed = new();
    int walkTries;
    public override string Name => "refuel reactor";

    /// <param name="rod">The spare rod to load (the adapter picks the fullest one).</param>
    public RefuelReactorTask(Character bot, HumanAIController ai, Reactor reactor, ItemContainer container, Item rod) : base(bot, ai)
    {
        this.reactor = reactor;
        this.container = container;
        this.rod = rod;
    }

    protected override string? Update()
    {
        if (rod.Removed) return "failed: the fuel rod is gone";
        if (rod.ParentInventory == container.Inventory) return Loaded();

        // 1. fetch that exact rod (the vanilla "contain item" objective would count rods already in the reactor
        //    as done, even spent ones, and report success without loading anything)
        if (rod.GetRootInventoryOwner() != Bot)
        {
            if (rod.GetRootInventoryOwner() is Character other) return $"failed: {other.Name} took the fuel rod";
            if (!HasObjective) Run(new AIObjectiveGetItem(Bot, rod, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false }, $"getting a fuel rod ({rod.ConditionPercentage:0}%)");
            else if ((ObjectiveFailed || Stalled(8)) && IsNear(rod, 250f) && PickUp(rod)) ClearObjective();
            else if (ObjectiveFailed) return "failed: could not get the fuel rod (unreachable?)";
            else if (ObjectiveDone) ClearObjective();
            return null;
        }

        // 2. to the reactor
        if (!IsNear(reactor.Item, 200f) && !Bot.CanInteractWith(reactor.Item))
        {
            if (!HasObjective) { Run(GoTo(reactor.Item, 80f), "carrying the fuel rod to the reactor"); return null; }
            if (ObjectiveFailed || ObjectiveDone || Stalled(6))
            {
                ClearObjective();
                if (IsNear(reactor.Item, 450f)) { }                       // the path ends a little short: close enough
                else if (++walkTries > 2) return "failed: cannot reach the reactor";
                else return null;
            }
            else return null;
        }
        ClearObjective();

        // 3. the spent rods come out first. The reactor then runs on the fresh rod alone: with a second rod in it, the
        //    vanilla operator finds it has "too much fuel" and throws a rod out (often the fresh one), and the crew
        //    refuels again and again.
        float cond = rod.Condition;
        foreach (var old in container.Inventory.AllItems.Where(i => i.HasTag(Tags.ReactorFuel) && i.Condition < cond).ToList())
        {
            removed.Add($"{old.ConditionPercentage:0}%");
            if (!Pocket(old)) old.Drop(Bot);
        }
        if (!container.Inventory.TryPutItem(rod, Bot))
            return container.Inventory.IsFull()
                ? "failed: the reactor is full of fuller rods; no need to refuel"
                : "failed: the fuel rod did not go in";
        return Loaded();
    }

    string Loaded()
    {
        if (rod.Removed || rod.ParentInventory != container.Inventory)
            return "failed: the fuel rod is not in the selected reactor after loading; inspect its current contents";
        var hint = reactor.FissionRate < 1 ? " The reactor is not running: operate it (role=reactor, option=powerup) or ask an engineer." : "";
        var outText = removed.Count > 0 ? $" Took out the spent rod{(removed.Count > 1 ? "s" : "")} ({string.Join(", ", removed)}): it is in your inventory." : "";
        return $"ok: fuel rod ({rod.ConditionPercentage:0}%) loaded, reactor fuel now {GameAdapter.FuelInReactor(container):0}%.{outText}{hint}";
    }
}
