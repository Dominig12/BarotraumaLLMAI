using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

internal sealed class CraftTask : BotTask
{
    readonly Fabricator fabricator;
    readonly FabricationRecipe recipe;
    readonly int amount;
    int made, ingredientIndex;
    bool started;
    Item? fetching;
    readonly HashSet<Item> unreachable = new();
    readonly List<string> unreachableWhere = new();
    double startedFabricatingAt;
    readonly List<string> clearedOutput = new();
    protected override double TimeoutSeconds => 120 + 120 * amount;
    int Yield => Crafting.Yield(recipe);
    public override string Name => $"craft {recipe.DisplayName.Value} x{amount * Yield} ({made * Yield} done)";

    public CraftTask(Character bot, HumanAIController ai, Fabricator fabricator, FabricationRecipe recipe, int amount) : base(bot, ai)
    {
        this.fabricator = fabricator;
        this.recipe = recipe;
        this.amount = amount;
    }

    int walkTries;

    /// <summary>null = at the fabricator; "" = still walking; anything else = the failure report.</summary>
    string? ReachFabricator(string step)
    {
        if (IsNear(fabricator.Item, 180f) || Bot.CanInteractWith(fabricator.Item)) { ClearObjective(); walkTries = 0; return null; }
        if (!HasObjective) { Run(GoTo(fabricator.Item, 50f), step); return ""; }
        if (ObjectiveFailed || ObjectiveDone || Stalled(6))
        {
            // the path ends a few metres away (the machine sits behind a railing or above the last waypoint): close enough
            if (IsNear(fabricator.Item, 450f)) { ClearObjective(); walkTries = 0; return null; }
            ClearObjective();
            if (++walkTries <= 2) return "";          // once more from where it stands now
            return $"failed: cannot reach the fabricator (stuck in {(Bot.CurrentHull != null ? GameAdapter.RoomId(Bot.CurrentHull) : "?")})";
        }
        return "";
    }

    protected override string? Update()
    {
        if (fabricator.Item.Removed) return "failed: the fabricator is gone";

        if (started)
        {
            Step = $"fabricating {recipe.DisplayName.Value} (craft {made + 1}/{amount})";
            if (fabricator.IsActive)
            {
                if (Timing.TotalTime - startedFabricatingAt > recipe.RequiredTime * 4 + 30)
                    return "failed: the fabricator does not progress (no power?)";
                return null;
            }
            started = false;
            made++;
            ingredientIndex = 0;
            if (made >= amount)
            {
                int inOutput = fabricator.OutputContainer?.Inventory.AllItems.Count(i => i.Prefab == recipe.TargetItem) ?? 0;
                return $"ok: made {made * Yield}x {recipe.DisplayName.Value} ({made} craft{(made > 1 ? "s" : "")}); {inOutput} in the output of {fabricator.Item.Name}" +
                       (clearedOutput.Count > 0 ? $" (what was left in the output is in your inventory now: {string.Join(", ", clearedOutput.Distinct())})" : "") +
                       $": take them with take_item(item=\"{recipe.TargetItem.Name.Value}\", count={Math.Max(1, inOutput)}, from=\"{fabricator.Item.Name}\") or move_item (moves the whole stack)";
            }
        }

        // 1. bring every ingredient into the fabricator's input, one concrete item at a time:
        //    pick it up first (also out of cabinets and the deconstructor's output), then put it in.
        //    "Contain item by identifier" alone gave up on items in machine outputs ("could not bring steel bar").
        var input = fabricator.InputContainer.Inventory;
        while (ingredientIndex < recipe.RequiredItems.Length)
        {
            var required = recipe.RequiredItems[ingredientIndex];
            var name = required.FirstMatchingPrefab?.Name.Value ?? "ingredient";
            int have = input.AllItems.Count(required.MatchesItem);
            if (have >= required.Amount) { ingredientIndex++; fetching = null; ClearObjective(); continue; }

            if (fetching != null && (fetching.Removed || fetching.ParentInventory == input))
            {
                fetching = null;
                ClearObjective();
                continue;
            }
            if (fetching == null)
            {
                fetching = ItemQueries.Available(Bot)
                    .Where(i => required.MatchesItem(i) && i.ParentInventory != input && !unreachable.Contains(i))
                    .OrderBy(i => i.GetRootInventoryOwner() == Bot ? 0 : 1)
                    .ThenBy(i => Vector2.DistanceSquared(i.WorldPosition, Bot.WorldPosition))
                    .FirstOrDefault();
                if (fetching == null)
                    return unreachable.Count > 0
                        ? $"failed: could not get {name} ({string.Join("; ", unreachableWhere.Take(3))})"
                        : $"failed: no {name} available on the sub (need {required.Amount - have} more)";
                ClearObjective();
            }

            if (fetching.GetRootInventoryOwner() != Bot)
            {
                if (!HasObjective)
                {
                    Run(new AIObjectiveGetItem(Bot, fetching, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false },
                        $"getting {name} ({ItemQueries.Where(fetching, Bot)})");
                    return null;
                }
                // standing at the cabinet but the objective does not take it: reach in
                if (Stalled(8) && IsNear(fetching, 250f) && Pocket(fetching)) { ClearObjective(); continue; }
                if (ObjectiveFailed || Stalled(15))
                {
                    // try the next one (another cabinet, another room) before giving up
                    unreachable.Add(fetching);
                    unreachableWhere.Add("unreachable in " + ItemQueries.Where(fetching, Bot));
                    fetching = null;
                    ClearObjective();
                    return null;
                }
                if (ObjectiveDone) ClearObjective();
                return null;
            }

            // carry it there and put it in by hand: the vanilla "contain item" objective could stop a few metres short
            // (next to a cabinet) and wait there for ever
            var reach = ReachFabricator($"bringing {name} ({have}/{required.Amount}) to the fabricator");
            if (reach != null) return reach.Length == 0 ? null : reach;
            if (!input.TryPutItem(fetching, Bot)) return $"failed: could not put {name} into the fabricator (input full?)";
            fetching = null;
            continue;   // re-check the count
        }

        // 2. stand at the fabricator and start it
        var at = ReachFabricator("walking to the fabricator");
        if (at != null) return at.Length == 0 ? null : at;
        // the game only starts a craft whose product fits the output: what an earlier craft left there goes into the pockets
        var output = fabricator.OutputContainer?.Inventory;
        if (output != null && !output.CanProbablyBePut(recipe.TargetItem, recipe.OutCondition * recipe.TargetItem.Health))
        {
            foreach (var left in output.AllItems.ToList())
                if (Pocket(left)) clearedOutput.Add(left.Name);
            if (!output.CanProbablyBePut(recipe.TargetItem, recipe.OutCondition * recipe.TargetItem.Health))
                return $"failed: the output of {fabricator.Item.Name} is full ({string.Join(", ", output.AllItems.Select(i => i.Name).Distinct().Take(4))}) and your inventory too: take them out first";
        }
        fabricator.AmountToFabricate = 1;
        fabricator.amountRemaining = 1;
        fabricator.StartFabricating(recipe, Bot);
        fabricator.Item.CreateServerEvent(fabricator);
        if (!fabricator.IsActive)
        {
            var missingNow = recipe.RequiredItems.Where(r => input.AllItems.Count(r.MatchesItem) < r.Amount).Select(r => r.FirstMatchingPrefab?.Name.Value ?? "?").ToList();
            return missingNow.Count > 0
                ? $"failed: ingredients went missing from the fabricator's input ({string.Join(", ", missingNow)})"
                : "failed: the fabricator refused to start (no power, or a recipe/talent it needs)";
        }
        started = true;
        startedFabricatingAt = Timing.TotalTime;
        return null;
    }
}

// ------------------------------------------------------------------------------------------- deconstruct

internal sealed class DeconstructTask : BotTask
{
    readonly Item item;
    readonly ItemPrefab prefab;
    double waitingSince = -1;
    public override string Name => $"deconstruct {prefab.Name.Value}";

    public DeconstructTask(Character bot, HumanAIController ai, Item item) : base(bot, ai)
    {
        this.item = item;
        prefab = item.Prefab;
    }

    string Done()
    {
        var y = Crafting.Yields(prefab, Crafting.Deconstructor(Bot));
        return $"ok: {prefab.Name.Value} deconstructed" +
               (y.Count > 0 ? $" into {string.Join(", ", y.Select(t => $"{t.Product.Name.Value} x{t.Amount}"))}{(prefab.RandomDeconstructionOutput ? " (random part of these)" : "")}" : "") +
               " (in the deconstructor's output)";
    }

    protected override string? Update()
    {
        if (item.Removed) return Done(); // the deconstructor consumed it
        if (!HasObjective)
        {
            Run(new AIObjectiveDeconstructItem(item, Bot, Ai.ObjectiveManager), $"taking {item.Name} to the deconstructor");
            return null;
        }
        if (ObjectiveFailed) return $"failed: could not deconstruct {item.Name}";
        if (!ObjectiveDone) return null;

        // the objective ends once the item is in and the machine is switched on; wait for the result
        if (waitingSince < 0) waitingSince = Timing.TotalTime;
        Step = "waiting for the deconstructor";
        var machine = (item.ParentInventory?.Owner as Item)?.GetComponent<Deconstructor>();
        if (machine == null) return $"failed: {item.Name} is not in a deconstructor any more";
        if (!machine.IsActive && Timing.TotalTime - waitingSince > 3) return "failed: the deconstructor stopped (no power?)";
        return null;
    }
}

/// <summary>deconstruct(item='all', from=...): several items, one after another.</summary>
internal sealed class DeconstructManyTask : BotTask
{
    readonly Queue<Item> items;
    readonly string where;
    readonly int total;
    DeconstructTask? current;
    readonly List<string> done = new();
    readonly List<string> failed = new();
    protected override double TimeoutSeconds => 120 + 90 * total;
    public override string Name => $"deconstruct {total} item(s) from {where}";

    public DeconstructManyTask(Character bot, HumanAIController ai, List<Item> items, string where) : base(bot, ai)
    {
        this.items = new Queue<Item>(items);
        total = items.Count;
        this.where = where;
    }

    protected override string? Update()
    {
        while (current == null)
        {
            if (items.Count == 0)
                return (done.Count > 0 ? $"ok: deconstructed {done.Count}/{total}: {string.Join("; ", done)}" : "failed: nothing was deconstructed") +
                       (failed.Count > 0 ? $". Problems: {string.Join("; ", failed.Distinct().Take(3))}" : "");
            var next = items.Dequeue();
            if (!next.Removed) current = new DeconstructTask(Bot, Ai, next);
        }
        var r = current.Tick();
        Step = $"{done.Count + failed.Count + 1}/{total}: {current.Step}";
        if (r == null) return null;
        current = null;
        if (r.StartsWith("ok")) done.Add(r.Replace("ok: ", "").Replace(" (in the deconstructor's output)", ""));
        else
        {
            failed.Add(r);
            if (r.Contains("no power") || r.Contains("stopped")) { items.Clear(); }  // no point going on
        }
        return null;
    }

    protected override void OnFinish() => current?.Cancel();
}

/// <summary>craft(salvage=true): break down the planned scrap first, then craft.</summary>
internal sealed class SalvageAndCraftTask : BotTask
{
    readonly Queue<Item> toSalvage;
    readonly Func<CraftTask> craft;
    readonly string target;
    BotTask? current;
    bool crafting;
    readonly List<string> salvaged = new();
    protected override double TimeoutSeconds => 900;
    public override string Name => $"salvage + craft {target}";

    public SalvageAndCraftTask(Character bot, HumanAIController ai, IEnumerable<Item> salvage, Func<CraftTask> craft, string target) : base(bot, ai)
    {
        toSalvage = new Queue<Item>(salvage);
        this.craft = craft;
        this.target = target;
    }

    protected override string? Update()
    {
        if (current == null)
        {
            if (toSalvage.Count > 0)
            {
                var next = toSalvage.Dequeue();
                if (next.Removed) return null;
                current = new DeconstructTask(Bot, Ai, next);
            }
            else
            {
                current = craft();
                crafting = true;
            }
        }
        var r = current.Tick();
        Step = (crafting ? "" : $"salvaging ({salvaged.Count + 1}/{salvaged.Count + 1 + toSalvage.Count}): ") + current.Step;
        if (r == null) return null;
        current = null;
        if (crafting) return (salvaged.Count > 0 ? $"salvaged {string.Join(", ", salvaged)}; " : "") + r;
        if (r.StartsWith("failed")) return $"{r} (while salvaging for {target})";
        salvaged.Add(r.Replace("ok: ", "").Replace(" (in the deconstructor's output)", ""));
        return null;
    }

    protected override void OnFinish() => current?.Cancel();
}
