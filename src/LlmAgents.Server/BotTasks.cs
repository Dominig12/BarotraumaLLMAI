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
    readonly float fuelBefore;
    public override string Name => "refuel reactor";

    public RefuelReactorTask(Character bot, HumanAIController ai, Reactor reactor, ItemContainer container) : base(bot, ai)
    {
        this.reactor = reactor;
        this.container = container;
        fuelBefore = GameAdapter.FuelInReactor(container);
    }

    protected override string? Update()
    {
        if (!HasObjective)
        {
            var contain = new AIObjectiveContainItem(Bot, ImmutableHashSet.Create(Tags.ReactorFuel), container, Ai.ObjectiveManager)
            {
                ItemCount = 1,
                RemoveEmpty = true,
                Equip = true,
                ConditionLevel = 1,
            };
            Run(contain, "bringing a fuel rod to the reactor");
            return null;
        }
        var fuelNow = GameAdapter.FuelInReactor(container);
        if (ObjectiveDone || fuelNow > fuelBefore + 1)
        {
            var hint = reactor.FissionRate < 1 ? " The reactor is not running: operate it (role=reactor, option=powerup) or ask an engineer." : "";
            return $"ok: fuel rod loaded, reactor fuel {fuelNow:0}%.{hint}";
        }
        if (ObjectiveFailed) return "failed: could not load a fuel rod (none reachable?)";
        return null;
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

        // 1. bring every ingredient into the fabricator's input
        while (ingredientIndex < recipe.RequiredItems.Length)
        {
            var required = recipe.RequiredItems[ingredientIndex];
            int have = fabricator.InputContainer.Inventory.AllItems.Count(required.MatchesItem);
            if (have >= required.Amount) { ingredientIndex++; ClearObjective(); continue; }

            if (!HasObjective)
            {
                var ids = required.ItemPrefabs.Select(p => p.Identifier).ToImmutableHashSet();
                var name = required.FirstMatchingPrefab?.Name.Value ?? "ingredient";
                bool anyAvailable = ItemQueries.Available(Bot).Any(i => required.MatchesItem(i) && i.ParentInventory != fabricator.InputContainer.Inventory);
                if (!anyAvailable) return $"failed: no {name} available on the sub (need {required.Amount - have} more)";
                var contain = new AIObjectiveContainItem(Bot, ids, fabricator.InputContainer, Ai.ObjectiveManager)
                {
                    ItemCount = required.Amount - have,
                    RemoveEmpty = false,
                };
                Run(contain, $"bringing {name} ({have}/{required.Amount}) to the fabricator");
                return null;
            }
            if (ObjectiveFailed) return $"failed: could not bring {required.FirstMatchingPrefab?.Name.Value ?? "an ingredient"}";
            if (ObjectiveDone) { ClearObjective(); continue; }  // re-check the count
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
    public override string Name => $"deconstruct {item.Name}";

    public DeconstructTask(Character bot, HumanAIController ai, Item item) : base(bot, ai) { this.item = item; }

    protected override string? Update()
    {
        if (!HasObjective)
        {
            Run(new AIObjectiveDeconstructItem(item, Bot, Ai.ObjectiveManager), $"taking {item.Name} to the deconstructor");
            return null;
        }
        if (item.Removed || ObjectiveDone) return $"ok: {item.Name} deconstructed (materials are in the deconstructor's output)";
        if (ObjectiveFailed) return $"failed: could not deconstruct {item.Name}";
        return null;
    }
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

        if (container != null)
        {
            if (item.ParentInventory == container.Inventory) return $"ok: {item.Name} is in {destination}";
            if (!HasObjective)
            {
                Run(new AIObjectiveContainItem(Bot, item, container, Ai.ObjectiveManager) { RemoveEmpty = false }, $"carrying {item.Name} to {destination}");
                return null;
            }
            if (ObjectiveFailed) return $"failed: could not put {item.Name} into {destination} (full or unreachable?)";
            if (ObjectiveDone) return item.ParentInventory == container.Inventory ? $"ok: {item.Name} is in {destination}" : $"failed: {item.Name} did not fit into {destination}";
            return null;
        }

        // pick it up first
        if (item.GetRootInventoryOwner() != Bot)
        {
            if (!HasObjective) Run(new AIObjectiveGetItem(Bot, item, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false }, $"getting {item.Name}");
            else if (ObjectiveFailed) return $"failed: could not get {item.Name}";
            else if (ObjectiveDone) ClearObjective();
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
