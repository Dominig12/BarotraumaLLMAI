using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// Multi-step actions built from vanilla AI objectives. Each step is handed to the bot as a forced objective
/// (get item, go to, contain item...); the final step that vanilla bots never do on their own (apply a chosen
/// medicine, start the fabricator) is done here. Ticked on the game thread.
/// </summary>
internal abstract class BotTask
{
    public readonly Character Bot;
    public readonly HumanAIController Ai;
    public readonly double StartedAt;
    public abstract string Name { get; }
    public string Step { get; protected set; } = "starting";
    protected virtual double TimeoutSeconds => 180;

    AIObjective? objective;
    bool objectiveDone, objectiveFailed;

    protected BotTask(Character bot, HumanAIController ai)
    {
        Bot = bot;
        Ai = ai;
        StartedAt = Timing.TotalTime;
    }

    public string Describe() => $"{Name}: {Step}";

    /// <summary>null = still running, otherwise the final report ("ok ..." / "failed ...").</summary>
    public string? Tick()
    {
        if (Bot.IsDead || Bot.Removed) return Finish("failed: you died");
        if (Bot.IsIncapacitated) return null;
        if (Timing.TotalTime - StartedAt > TimeoutSeconds) return Finish($"failed: took too long ({Step})");
        try
        {
            var r = Update();
            return r == null ? null : Finish(r);
        }
        catch (Exception e)
        {
            return Finish("failed: " + e.Message);
        }
    }

    protected abstract string? Update();

    public void Cancel() => Finish("cancelled");

    string Finish(string report)
    {
        ClearObjective();
        try { OnFinish(); } catch { /* best effort */ }
        return report;
    }

    protected virtual void OnFinish() { }

    /// <summary>Runs a vanilla objective as the bot's top priority until it completes or is abandoned.</summary>
    protected void Run(AIObjective o, string step)
    {
        ClearObjective();
        objective = o;
        objectiveDone = objectiveFailed = false;
        o.Completed += () => objectiveDone = true;
        o.Abandoned += () => objectiveFailed = true;
        Ai.ObjectiveManager.SetForcedOrder(o);
        Step = step;
    }

    protected bool Running => objective != null && !ObjectiveDone && !ObjectiveFailed;
    protected bool ObjectiveDone => objective != null && (objectiveDone || objective.IsCompleted);
    protected bool ObjectiveFailed => objective != null && (objectiveFailed || objective.Abandon);
    protected bool HasObjective => objective != null;

    protected void ClearObjective()
    {
        if (objective != null && Ai.ObjectiveManager.ForcedOrder == objective) Ai.ObjectiveManager.ClearForcedOrder();
        objective = null;
    }

    protected AIObjectiveGoTo GoTo(ISpatialEntity target, float closeEnough = 100f) =>
        new(target, Bot, Ai.ObjectiveManager, repeat: false, getDivingGearIfNeeded: true, closeEnough: closeEnough);

    protected bool IsNear(Entity target, float distance = 150f) =>
        Vector2.Distance(Bot.WorldPosition, target.WorldPosition) < distance;
}

// ------------------------------------------------------------------------------------------- come to

/// <summary>
/// "Come to me": walk to where the person is NOW (keeps up if they move), stay next to them for a while, then the order
/// is over. A go_to_room order instead pinned bots to the room the player had already left, for the rest of the round.
/// </summary>
internal sealed class ComeToTask : BotTask
{
    readonly Character target;
    readonly double hold;
    double arrivedAt = -1;
    public override string Name => $"come to {target.Name}";
    protected override double TimeoutSeconds => 150 + hold;

    public ComeToTask(Character bot, HumanAIController ai, Character target, double holdSeconds) : base(bot, ai)
    {
        this.target = target;
        hold = holdSeconds;
    }

    protected override string? Update()
    {
        if (target.Removed || target.IsDead) return $"failed: {target.Name} is dead";
        // repeat = keeps following, like the vanilla follow order, so the bot stays with them until the hold is over
        if (!HasObjective || ObjectiveDone)
            Run(new AIObjectiveGoTo(target, Bot, Ai.ObjectiveManager, repeat: true, getDivingGearIfNeeded: true, closeEnough: 120f),
                $"walking to {target.Name}");
        else if (ObjectiveFailed) return $"failed: cannot reach {target.Name}";

        bool near = IsNear(target, 250f);
        if (near && arrivedAt < 0)
        {
            arrivedAt = Timing.TotalTime;
            Step = $"next to {target.Name}, waiting for orders";
        }
        if (arrivedAt >= 0 && Timing.TotalTime - arrivedAt >= hold)
            return $"ok: you came to {target.Name} and stayed {hold:0}s. This order is DONE: set_goal(done=true) and go back to your duties, unless {target.Name} gave you a new order";
        return null;
    }
}

// ------------------------------------------------------------------------------------------- equip

internal sealed class EquipTask : BotTask
{
    readonly Item item;
    public override string Name => $"equip {item.Name}";
    protected override double TimeoutSeconds => 120;

    public EquipTask(Character bot, HumanAIController ai, Item item) : base(bot, ai) { this.item = item; }

    protected override string? Update()
    {
        if (Bot.HasEquippedItem(item)) return $"ok: {item.Name} equipped";
        if (item.Removed) return $"failed: {item.Name} is gone";
        if (!HasObjective)
        {
            var get = new AIObjectiveGetItem(Bot, item, Ai.ObjectiveManager, equip: true)
            {
                Wear = item.GetComponent<Wearable>() != null,
                SpeakIfFails = false,
            };
            Run(get, $"getting {item.Name} from {ItemQueries.Where(item, Bot)}");
            return null;
        }
        if (ObjectiveFailed) return $"failed: could not get {item.Name}";
        if (ObjectiveDone && !Bot.HasEquippedItem(item))
            return Bot.Inventory.Contains(item) ? $"ok: {item.Name} is in your inventory (could not equip it)" : $"failed: could not get {item.Name}";
        return null;
    }
}

// ------------------------------------------------------------------------------------------- reactor

internal sealed class RefuelReactorTask : BotTask
{
    readonly Reactor reactor;
    readonly ItemContainer container;
    readonly Item rod;
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
            if (!HasObjective) Run(new AIObjectiveGetItem(Bot, rod, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false }, $"getting a fuel rod ({rod.ConditionPercentage:0}%)");
            else if (ObjectiveFailed) return "failed: could not get the fuel rod (unreachable?)";
            else if (ObjectiveDone) ClearObjective();
            return null;
        }

        // 2. load it; if all slots are taken, the most spent rod comes out
        if (!HasObjective)
        {
            float cond = rod.ConditionPercentage;
            Run(new AIObjectiveContainItem(Bot, rod, container, Ai.ObjectiveManager)
            {
                RemoveEmpty = true,
                RemoveExistingWhenNecessary = true,
                RemoveExistingPredicate = i => i.HasTag(Tags.ReactorFuel) && i.ConditionPercentage < cond,
                RemoveMax = 1,
            }, "loading the fuel rod into the reactor");
            return null;
        }
        if (ObjectiveFailed) return "failed: could not load the fuel rod (reactor full of fuller rods, or unreachable)";
        if (ObjectiveDone) return rod.ParentInventory == container.Inventory ? Loaded() : "failed: the fuel rod did not go in";
        return null;
    }

    string Loaded()
    {
        var hint = reactor.FissionRate < 1 ? " The reactor is not running: operate it (role=reactor, option=powerup) or ask an engineer." : "";
        return $"ok: fuel rod ({rod.ConditionPercentage:0}%) loaded, reactor fuel now {GameAdapter.FuelInReactor(container):0}%.{hint}";
    }
}

// ------------------------------------------------------------------------------------------- crafting

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
    protected override double TimeoutSeconds => 120 + 120 * amount;
    public override string Name => $"craft {recipe.DisplayName.Value} x{amount} ({made} done)";

    public CraftTask(Character bot, HumanAIController ai, Fabricator fabricator, FabricationRecipe recipe, int amount) : base(bot, ai)
    {
        this.fabricator = fabricator;
        this.recipe = recipe;
        this.amount = amount;
    }

    protected override string? Update()
    {
        if (fabricator.Item.Removed) return "failed: the fabricator is gone";

        if (started)
        {
            Step = $"fabricating {recipe.DisplayName.Value} ({made + 1}/{amount})";
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
                return $"ok: made {made}x {recipe.DisplayName.Value}; it is in the fabricator's output";
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
                if (ObjectiveFailed)
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

            if (!HasObjective)
            {
                Run(new AIObjectiveContainItem(Bot, fetching, fabricator.InputContainer, Ai.ObjectiveManager) { RemoveEmpty = false },
                    $"bringing {name} ({have}/{required.Amount}) to the fabricator");
                return null;
            }
            if (ObjectiveFailed) return $"failed: could not put {name} into the fabricator (input full or unreachable?)";
            if (ObjectiveDone) { fetching = null; ClearObjective(); continue; }  // re-check the count
            return null;
        }

        // 2. stand at the fabricator and start it
        if (!IsNear(fabricator.Item, 180f))
        {
            if (!HasObjective) Run(GoTo(fabricator.Item, 80f), "walking to the fabricator");
            else if (ObjectiveFailed) return "failed: cannot reach the fabricator";
            return null;
        }
        ClearObjective();
        fabricator.AmountToFabricate = 1;
        fabricator.amountRemaining = 1;
        fabricator.StartFabricating(recipe, Bot);
        fabricator.Item.CreateServerEvent(fabricator);
        if (!fabricator.IsActive) return "failed: the fabricator refused to start (missing ingredients, skill or recipe?)";
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

// ------------------------------------------------------------------------------------------- medicine

internal sealed class TreatTask : BotTask
{
    readonly Character patient;
    readonly string? onlyItemQuery;
    ItemQueries.TreatmentChoice? choice;
    double waitUntil;
    int applied;
    readonly List<string> used = new();
    const int MaxApplications = 8;
    public override string Name => $"treat {patient.Name}";
    protected override double TimeoutSeconds => 150;

    public TreatTask(Character bot, HumanAIController ai, Character patient, string? onlyItemQuery) : base(bot, ai)
    {
        this.patient = patient;
        this.onlyItemQuery = onlyItemQuery;
    }

    protected override void OnFinish()
    {
        if (Bot.SelectedCharacter == patient) Bot.SelectedCharacter = null;
    }

    string Summary() => used.Count == 0 ? "nothing used" : "used " + string.Join(", ", used.GroupBy(u => u).Select(g => $"{g.Key} x{g.Count()}"));

    protected override string? Update()
    {
        if (patient.Removed || patient.IsDead) return $"failed: {patient.Name} is dead ({Summary()})";
        if (Timing.TotalTime < waitUntil) return null;   // let the last treatment take effect

        if (!ItemQueries.TreatableAfflictions(patient).Any())
            return $"ok: {patient.Name} needs no more treatment ({Summary()}), health {GameAdapter.HealthPercent(patient)}%";
        if (applied >= MaxApplications)
            return $"ok: stopped after {applied} treatments ({Summary()}); remaining: {ItemQueries.DescribeInjuries(patient)}";

        if (choice == null || choice.Item.Removed || !CanUse(choice.Item))
        {
            ClearObjective();
            choice = Choose();
            if (choice == null)
                return applied > 0
                    ? $"ok: nothing suitable left ({Summary()}); remaining: {ItemQueries.DescribeInjuries(patient)}"
                    : onlyItemQuery != null
                        ? $"failed: no '{onlyItemQuery}' that helps with {ItemQueries.DescribeInjuries(patient)}"
                        : $"failed: no suitable medicine available for {ItemQueries.DescribeInjuries(patient)}; craft or find some";
        }

        // 1. get the medicine
        if (!InHands(choice.Item))
        {
            if (!HasObjective)
                Run(new AIObjectiveGetItem(Bot, choice.Item, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false },
                    $"getting {choice.Item.Name} from {ItemQueries.Where(choice.Item, Bot)}");
            else if (ObjectiveFailed) { choice = null; ClearObjective(); }
            else if (ObjectiveDone) ClearObjective();
            return null;
        }

        // 2. go to the patient
        if (patient != Bot && !IsNear(patient, 120f))
        {
            if (!HasObjective) Run(GoTo(patient, 60f), $"going to {patient.Name} with {choice.Item.Name}");
            else if (ObjectiveFailed) return $"failed: cannot reach {patient.Name} ({Summary()})";
            return null;
        }

        // 3. apply it to the limb with the affliction it was chosen for
        ClearObjective();
        if (patient != Bot) Bot.SelectCharacter(patient);
        var limb = patient.CharacterHealth.GetAfflictionLimb(choice.Affliction);
        var name = choice.Item.Name;
        choice.Item.ApplyTreatment(Bot, patient, limb);
        used.Add(name);
        applied++;
        Step = $"applied {name} for {choice.Affliction.Prefab.Name.Value}";
        choice = null;
        waitUntil = Timing.TotalTime + 2.0;
        return null;
    }

    bool InHands(Item i) => i.GetRootInventoryOwner() == Bot || (patient.IsIncapacitated && i.GetRootInventoryOwner() == patient);
    bool CanUse(Item i) => InHands(i) || ItemQueries.IsAvailableTo(i, Bot);

    ItemQueries.TreatmentChoice? Choose()
    {
        if (onlyItemQuery == null) return ItemQueries.ChooseTreatment(Bot, patient);
        foreach (var candidate in ItemQueries.Find(Bot, onlyItemQuery, ItemQueries.IsMedical).Take(5))
        {
            var c = ItemQueries.ChooseTreatment(Bot, patient, candidate);
            if (c != null) return c;
        }
        return null;
    }
}

// ------------------------------------------------------------------------------------------- carrying

/// <summary>Carry an item into a container, to a room (dropped there) or into a crew member's inventory.</summary>
internal sealed class MoveItemTask : BotTask
{
    readonly Item item;
    readonly ItemContainer? container;
    readonly Hull? room;
    readonly Character? receiver;
    readonly string destination;
    public override string Name => $"move {item.Name} to {destination}";

    public MoveItemTask(Character bot, HumanAIController ai, Item item, ItemContainer? container, Hull? room, Character? receiver, string destination)
        : base(bot, ai)
    {
        this.item = item;
        this.container = container;
        this.room = room;
        this.receiver = receiver;
        this.destination = destination;
    }

    protected override string? Update()
    {
        if (item.Removed) return $"failed: {item.Name} is gone";

        if (container != null && item.ParentInventory == container.Inventory) return $"ok: {item.Name} is in {destination}";

        // pick it up first (the "contain item" objective gives up on items the bot cannot reach from where it stands)
        if (item.GetRootInventoryOwner() != Bot)
        {
            if (!HasObjective) Run(new AIObjectiveGetItem(Bot, item, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false }, $"getting {item.Name}");
            else if (ObjectiveFailed) return $"failed: could not get {item.Name}";
            else if (ObjectiveDone) ClearObjective();
            return null;
        }

        if (container != null)
        {
            if (!HasObjective)
            {
                Run(new AIObjectiveContainItem(Bot, item, container, Ai.ObjectiveManager) { RemoveEmpty = false }, $"carrying {item.Name} to {destination}");
                return null;
            }
            if (ObjectiveFailed) return $"failed: could not put {item.Name} into {destination} (full or unreachable?)";
            if (ObjectiveDone) return item.ParentInventory == container.Inventory ? $"ok: {item.Name} is in {destination}" : $"failed: {item.Name} did not fit into {destination}";
            return null;
        }

        if (receiver != null)
        {
            if (receiver.Removed || receiver.IsDead) return $"failed: {receiver.Name} is dead";
            if (!IsNear(receiver, 120f))
            {
                if (!HasObjective) Run(GoTo(receiver, 60f), $"bringing {item.Name} to {receiver.Name}");
                else if (ObjectiveFailed) return $"failed: cannot reach {receiver.Name}";
                return null;
            }
            ClearObjective();
            if (receiver.Inventory.TryPutItem(item, Bot, new List<InvSlotType> { InvSlotType.Any }))
                return $"ok: gave {item.Name} to {receiver.Name}";
            item.Drop(Bot);
            return $"ok: {receiver.Name}'s inventory is full, left {item.Name} at their feet";
        }

        if (room != null)
        {
            if (Bot.CurrentHull != room)
            {
                if (!HasObjective) Run(GoTo(room, 80f), $"carrying {item.Name} to {GameAdapter.RoomId(room)}");
                else if (ObjectiveFailed) return $"failed: cannot reach {GameAdapter.RoomId(room)}";
                else if (ObjectiveDone && Bot.CurrentHull != room) return $"failed: did not get into {GameAdapter.RoomId(room)}";
                return null;
            }
            ClearObjective();
            item.Drop(Bot);
            return $"ok: left {item.Name} in {GameAdapter.RoomId(room)}";
        }
        return "failed: no destination";
    }
}
