using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

internal sealed class CraftTask : BotTask
{
    static readonly Dictionary<Fabricator, CraftTask> reservations = new();
    readonly Fabricator fabricator;
    readonly FabricationRecipe recipe;
    readonly int amount;
    int made;
    bool started;
    bool reserved;
    FabricationCycle? cycle;
    Item? fetching;
    int fetchingRequirement;
    readonly HashSet<Item> unreachable = new();
    readonly List<string> unreachableWhere = new();
    readonly List<string> clearedOutput = new();
    readonly List<string> droppedOutput = new();
    readonly List<Item> products = new();
    readonly double runTimeout;
    protected override double TimeoutSeconds => 120 + runTimeout * amount;
    int Yield => Crafting.Yield(recipe);
    public override string Name => $"craft {recipe.DisplayName.Value} x{amount * Yield} ({products.Count} made)";
    public override IEnumerable<Item> Claimed => new[] { fabricator.Item }.Concat(fetching != null ? new[] { fetching } : Enumerable.Empty<Item>());

    public CraftTask(Character bot, HumanAIController ai, Fabricator fabricator, FabricationRecipe recipe, int amount) : base(bot, ai)
    {
        this.fabricator = fabricator;
        this.recipe = recipe;
        this.amount = amount;
        runTimeout = Math.Max(120, fabricator.GetRequiredTime(recipe, bot) + 90);
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
        if (!reserved)
        {
            // Two bots must not overwrite each other's recipe, counters, or locked input.
            if (reservations.TryGetValue(fabricator, out var owner) && owner != this || fabricator.IsActive)
            {
                Step = "waiting for the fabricator to become free";
                return null;
            }
            reservations[fabricator] = this;
            reserved = true;
            fabricator.OnItemFabricated += ProductCreated;
        }

        if (started)
        {
            Step = $"fabricating {recipe.DisplayName.Value} (craft {made + 1}/{amount})";
            if (fabricator.IsActive && (fabricator.fabricatedItem != recipe || fabricator.user != Bot))
                return Failure("someone changed the fabricator's recipe or took over the machine");
            switch (cycle!.Observe(fabricator.IsActive, fabricator.timeUntilReady, Timing.TotalTime))
            {
                case FabricationCycleState.Running:
                case FabricationCycleState.AwaitingProducts: return null;
                case FabricationCycleState.Stalled: return Failure("the fabricator has made no progress for 45s (no power?)");
                case FabricationCycleState.StoppedWithoutProducts: return Failure("the fabricator stopped without making the expected products (cancelled or ingredients unavailable)");
            }
            started = false;
            made++;
            cycle = null;
            if (made >= amount) return Report();
        }
        else if (fabricator.IsActive)
            return Failure("someone started using the fabricator while ingredients were being collected");

        // 1. Vanilla moves ingredients from the input and actual linked inventories itself.
        // Carry only ingredients that the station cannot already reach through its native links.
        var input = fabricator.InputContainer.Inventory;
        while (true)
        {
            var native = Crafting.NativeStock(Bot, fabricator).ToHashSet();
            var assigned = Crafting.Allocate(recipe, native.ToList());
            int ingredientIndex = Enumerable.Range(0, recipe.RequiredItems.Length)
                .FirstOrDefault(index => assigned[index].Count < recipe.RequiredItems[index].Amount, -1);
            if (ingredientIndex < 0) break;

            if (fetching != null && (fetching.Removed || native.Contains(fetching) || !Crafting.Matches(recipe.RequiredItems[fetchingRequirement], fetching)))
            {
                fetching = null;
                ClearObjective();
                continue;
            }
            if (fetching != null && !ItemQueries.IsAvailableTo(fetching, Bot))
            {
                unreachable.Add(fetching);
                unreachableWhere.Add("no longer available in " + ItemQueries.Where(fetching, Bot));
                fetching = null;
                ClearObjective();
                continue;
            }
            if (fetching == null)
            {
                var available = Crafting.Stock(Bot, fabricator)
                    .Where(i => !unreachable.Contains(i))
                    .OrderBy(i => native.Contains(i) ? 0 : 1)
                    .ThenBy(i => i.GetRootInventoryOwner() == Bot ? 0 : 1)
                    .ThenBy(i => Vector2.DistanceSquared(i.WorldPosition, Bot.WorldPosition))
                    .ToList();
                var next = CraftingRules.NextIngredient(Crafting.Allocate(recipe, available), native.Contains);
                if (next == null)
                {
                    var missing = Crafting.Missing(recipe, available);
                    string needed = string.Join(", ", missing.Select(m => $"{Crafting.IngredientName(m.Req)} x{m.Missing} (condition {m.Req.MinCondition * 100:0}-{m.Req.MaxCondition * 100:0}%)"));
                    return unreachable.Count > 0
                        ? Failure($"could not get the missing ingredients {needed} ({string.Join("; ", unreachableWhere.Take(3))})")
                        : Failure($"no suitable ingredients available on the sub: {needed}");
                }
                fetching = next.Value.Item;
                fetchingRequirement = next.Value.Requirement;
                ClearObjective();
            }
            var required = recipe.RequiredItems[fetchingRequirement];
            var name = required.FirstMatchingPrefab?.Name.Value ?? "ingredient";
            int have = assigned[fetchingRequirement].Count;

            if (fetching.GetRootInventoryOwner() != Bot)
            {
                if (!HasObjective)
                {
                    Run(new AIObjectiveGetItem(Bot, fetching, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false },
                        $"getting {name} ({ItemQueries.Where(fetching, Bot)})");
                    return null;
                }
                // standing at the cabinet but the objective does not take it: reach in
                if ((Stalled(8) || ObjectiveFailed) && IsNear(fetching, 250f) && PickUp(fetching)) { ClearObjective(); continue; }
                if (ObjectiveFailed || Stalled(15) || ObjectiveDone && fetching.GetRootInventoryOwner() != Bot)
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
            if (!input.CanBePut(fetching))
            {
                var needed = assigned.SelectMany(items => items).ToHashSet();
                foreach (var extra in input.AllItems.Distinct().Where(i => !needed.Contains(i)).ToList())
                {
                    if (!PickUp(extra)) extra.Drop(Bot);
                    if (input.CanBePut(fetching)) break;
                }
            }
            if (!input.TryPutItem(fetching, Bot)) return Failure($"could not put {name} into the fabricator (input full or locked)");
            fetching = null;
            continue;   // re-check the count
        }

        // 2. stand at the fabricator and start it
        var at = ReachFabricator("walking to the fabricator");
        if (at != null) return at.Length == 0 ? null : at;
        // The next shell must fit even when the last shell is too large for pockets or both hands are occupied.
        // Only the inactive machine's output is cleared; products remain recoverable beside the fabricator.
        var output = fabricator.OutputContainer?.Inventory;
        if (output != null && !output.CanProbablyBePut(recipe.TargetItem, recipe.OutCondition * recipe.TargetItem.Health))
        {
            var cleared = CraftingRules.ClearOutput(output.AllItems.Distinct(),
                () => output.CanProbablyBePut(recipe.TargetItem, recipe.OutCondition * recipe.TargetItem.Health), PickUp,
                left =>
                {
                    left.Drop(Bot);
                    return left.ParentInventory != output;
                });
            clearedOutput.AddRange(cleared.Carried.Select(i => i.Name));
            droppedOutput.AddRange(cleared.Dropped.Select(i => i.Name));
            if (!cleared.HasRoom)
                return Failure($"could not clear the output of {fabricator.Item.Name} ({string.Join(", ", output.AllItems.Select(i => i.Name).Distinct().Take(4))})");
        }
        // Refresh must check linked container access for this bot, rather than the machine's last user.
        fabricator.user = Bot;
        fabricator.RefreshAvailableIngredients();
        if (!fabricator.CanBeFabricated(recipe, fabricator.availableIngredients, Bot))
            return Failure("the recipe cannot be fabricated with the current ingredients, recipe access, money, or fabrication limit");
        fabricator.AmountToFabricate = 1;
        fabricator.amountRemaining = 1;
        fabricator.StartFabricating(recipe, Bot);
        fabricator.Item.CreateServerEvent(fabricator);
        if (!fabricator.IsActive)
        {
            var missingNow = Crafting.Missing(recipe, Crafting.NativeStock(Bot, fabricator)).Select(r => r.Req.FirstMatchingPrefab?.Name.Value ?? "?").ToList();
            return missingNow.Count > 0
                ? Failure($"ingredients went missing from the input or linked sources ({string.Join(", ", missingNow)})")
                : Failure("the fabricator refused to start");
        }
        started = true;
        cycle = new FabricationCycle(Yield, fabricator.timeUntilReady, Timing.TotalTime);
        return null;
    }

    void ProductCreated(Item item, Character user)
    {
        if (!started || user != Bot || item.Prefab != recipe.TargetItem || products.Contains(item)) return;
        products.Add(item);
        cycle?.ProductCreated();
    }

    string Failure(string reason) => $"failed: {reason}; {products.Count}x {recipe.DisplayName.Value} made before this stopped";

    string Report()
    {
        var outputProducts = products.Where(i => !i.Removed && i.ParentInventory == fabricator.OutputContainer?.Inventory).ToList();
        int inOutput = outputProducts.Count;
        var locations = products.Where(i => !i.Removed).GroupBy(i => ItemQueries.Where(i, Bot))
            .Select(g => $"{g.Count()} in/at {g.Key} ({string.Join(", ", g.Select(i => $"#{i.ID}"))})");
        string room = fabricator.Item.CurrentHull != null ? GameAdapter.RoomId(fabricator.Item.CurrentHull) : fabricator.Item.Name;
        return $"ok: made {products.Count}x {recipe.DisplayName.Value} ({made} craft{(made > 1 ? "s" : "")}); {inOutput} newly made in the output of {fabricator.Item.Name} in {room}; products now: {string.Join("; ", locations)}" +
               (clearedOutput.Count > 0 ? $"; during crafting, cleared output into your inventory: {string.Join(", ", clearedOutput.Distinct())}" : "") +
               (droppedOutput.Count > 0 ? $"; during crafting, cleared output onto the floor by the fabricator in {room}: {string.Join(", ", droppedOutput.Distinct())}" : "") +
               OutputGuidance(outputProducts) + ". Locations are the current snapshot; if an item is no longer carried, inspect its exact #ID to locate it before issuing drop or move";
    }

    string OutputGuidance(List<Item> outputProducts)
    {
        if (outputProducts.Count == 0) return "";
        var next = outputProducts[0];
        var take = GameAdapter.CanCarryWithoutDropping(Bot, next)
            ? $". Take the next exact output with take_item(item=\"#{next.ID}\", count=1, from=\"#{fabricator.Item.ID}\"); recheck carrying capacity before another"
            : ". No compatible free carrying slot for the next output; store or drop a carried item before picking it up";
        return take + $". To leave an output beside this fabricator without carrying it, use drop(item=\"#{next.ID}\"). Output #IDs: {string.Join(", ", outputProducts.Select(item => $"#{item.ID}"))}";
    }

    protected override void OnFinish()
    {
        if (!reserved) return;
        fabricator.OnItemFabricated -= ProductCreated;
        if (reservations.TryGetValue(fabricator, out var owner) && owner == this) reservations.Remove(fabricator);
        if (started && !fabricator.Item.Removed && fabricator.IsActive && fabricator.user == Bot && fabricator.fabricatedItem == recipe)
        {
            fabricator.CancelFabricating(Bot);
            fabricator.Item.CreateServerEvent(fabricator);
        }
        reserved = false;
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

/// <summary>deconstruct(item='*', from=...): several items, one after another.</summary>
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

    protected override void ClearOwnedWork()
    {
        try { current?.Cancel(); }
        finally { base.ClearOwnedWork(); }
    }
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
        // Keep the child's ok/failed prefix first: task reports expose that outcome separately from the details.
        if (crafting) return r + (salvaged.Count > 0 ? $"; salvaged {string.Join(", ", salvaged)}" : "");
        if (r.StartsWith("failed")) return $"{r} (while salvaging for {target})";
        salvaged.Add(r.Replace("ok: ", "").Replace(" (in the deconstructor's output)", ""));
        return null;
    }

    protected override void ClearOwnedWork()
    {
        try { current?.Cancel(); }
        finally { base.ClearOwnedWork(); }
    }
}
