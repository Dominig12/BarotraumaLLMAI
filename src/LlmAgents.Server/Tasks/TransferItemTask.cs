using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>Explicit instance transfers. Never swaps, combines condition, drops contents or substitutes another item.</summary>
internal sealed class TransferItemTask : BotTask
{
    readonly Queue<Item> pending;
    readonly Inventory destination;
    readonly ItemContainer? container;
    readonly int? slot;
    readonly bool allowGearRemoval;
    readonly int wanted;
    readonly List<string> moved = new();
    readonly List<string> already = new();
    readonly List<string> failures = new();
    readonly HashSet<Item> originallyInPlace;
    Item? current;
    Entity? walkingTo;
    public override string Name => $"transfer {wanted} item(s) to {GameAdapter.InventoryOwnerRef(destination.Owner)}" + (slot is { } s ? $" slot {s}" : "");
    protected override double TimeoutSeconds => 90 + wanted * 90;
    public override IEnumerable<Item> Claimed => pending.Concat(current != null ? new[] { current } : Array.Empty<Item>())
        .Concat(destination.Owner is Item holder ? new[] { holder } : Array.Empty<Item>());

    public TransferItemTask(Character bot, HumanAIController ai, IEnumerable<Item> items, Inventory destination, ItemContainer? container, int? slot, bool allowGearRemoval = false) : base(bot, ai)
    {
        pending = new Queue<Item>(items.Distinct());
        wanted = pending.Count;
        originallyInPlace = pending.Where(i => i.ParentInventory == destination && (slot == null || destination.GetItemsAt(slot.Value).Contains(i))).ToHashSet();
        this.destination = destination;
        this.container = container;
        this.slot = slot;
        this.allowGearRemoval = allowGearRemoval;
    }

    protected override string? Update()
    {
        if (Timing.TotalTime - StartedAt > TimeoutSeconds - 5) return Report("time limit reached");
        if (destination.Owner.Removed) return Report("destination was removed");
        if (GameAdapter.InventoryDestinationProblem(Bot, destination, slot, requireRange: false) is { } destinationError) return Report(destinationError);
        if (current == null)
        {
            if (pending.Count == 0) return Report();
            current = pending.Dequeue();
            ClearObjective();
            walkingTo = null;
        }
        var item = current;
        if (item.Removed) { Fail("selected item was removed"); return null; }
        if (ItemQueries.IsIgnored(item)) { Fail("item was ordered ignored"); return null; }
        if (InventoryTransferRules.WouldCreateCycle(item, destination.Owner as Item, i => i.Container)) { Fail("containment cycle is forbidden"); return null; }
        if (item.ParentInventory == destination && (slot == null || destination.GetItemsAt(slot.Value).Contains(item)))
        {
            if (originallyInPlace.Contains(item)) already.Add($"#{item.ID}");
            else moved.Add($"#{item.ID} {item.Name}");
            Next();
            return null;
        }
        if (!allowGearRemoval && item.HasTag(Tags.HeavyDivingGear) && Bot.HasEquippedItem(item) && GearLock.IsLocked(Bot)
            && !(destination == Bot.Inventory && slot is { } gearSlot && Bot.Inventory.SlotTypes[gearSlot].HasFlag(InvSlotType.OuterClothes)))
        {
            // Authority was checked when the action started; newly detected danger must still prevent removing protection.
            Fail("diving gear is locked by current danger or keep_gear");
            return null;
        }
        if (item.ParentInventory != null && GameAdapter.InventoryAccessProblem(Bot, item.ParentInventory, requireRange: false, requireDragAndDrop: true) is { } sourceError)
        { Fail(sourceError); return null; }

        bool sourceAccessible = item.ParentInventory != null
            ? GameAdapter.InventoryAccessProblem(Bot, item.ParentInventory, requireDragAndDrop: true) == null : Bot.CanInteractWith(item);
        bool destinationAccessible = GameAdapter.InventoryDestinationProblem(Bot, destination, slot) == null;
        if (sourceAccessible && destinationAccessible)
        {
            ClearObjective();
            if (TryPlace(item, out var error)) moved.Add($"#{item.ID} {item.Name}");
            else failures.Add($"#{item.ID}: {error}");
            Next();
            return null;
        }

        if (item.GetRootInventoryOwner() != Bot)
        {
            if (!sourceAccessible)
            {
                var sourceTarget = item.GetRootInventoryOwner() ?? item;
                var reached = Approach(sourceTarget);
                if (reached == "ready")
                {
                    if (item.ParentInventory != null && GameAdapter.InventoryAccessProblem(Bot, item.ParentInventory, requireDragAndDrop: true) is { } access)
                        Fail(access);
                    else if (!PickUp(item)) Fail("cannot carry item; no compatible free pocket/hand slots");
                    else { ClearObjective(); walkingTo = null; }
                }
                else if (reached != null) Fail(reached);
                return null;
            }
            if (!PickUp(item)) { Fail("cannot carry item; no compatible free pocket/hand slots"); return null; }
            ClearObjective();
            walkingTo = null;
            return null;
        }

        var arrival = Approach(destination.Owner);
        if (arrival == "ready")
        {
            if (GameAdapter.InventoryDestinationProblem(Bot, destination, slot) is { } access) Fail(access);
        }
        else if (arrival != null) Fail(arrival);
        return null;
    }

    bool TryPlace(Item item, out string? error)
    {
        error = null;
        if (container != null && (slot is { } s ? !container.CanBeContained(item, s) : !container.CanBeContained(item)))
        { error = "item is incompatible with the selected compartment/slot"; return false; }
        if (slot is { } index)
        {
            if (!destination.CanBePutInSlot(item, index)) { error = "selected slot is occupied, full, or incompatible; existing contents were preserved"; return false; }
            bool put = destination.TryPutItem(item, index, allowSwapping: false, allowCombine: false, user: Bot);
            if (put && item.ParentInventory == destination && destination.GetItemsAt(index).Contains(item)) return true;
        }
        else
        {
            // Use the non-swapping, non-combining overload for every slot, including automatic placement.
            // Pockets first, then empty compatible hands for bulky items. Clothing slots require an explicit slot.
            foreach (int candidateSlot in InventoryView.AutomaticSlots(destination, InventoryView.GivingOnly(Bot, destination)))
            {
                if (!destination.CanBePutInSlot(item, candidateSlot)) continue;
                if (destination.TryPutItem(item, candidateSlot, allowSwapping: false, allowCombine: false, user: Bot) && item.ParentInventory == destination) return true;
            }
        }
        error = "item did not enter the requested destination; no replacement or dropping was attempted";
        return false;
    }

    string? Approach(Entity target)
    {
        bool ready = target == Bot || target is Item item && Bot.CanInteractWith(item)
            || target is Character character && Bot.CanInteractWith(character);
        if (ready) { ClearObjective(); walkingTo = null; return "ready"; }
        if (walkingTo != target || !HasObjective)
        {
            if (target is not ISpatialEntity spatial) return "target has no navigable position";
            walkingTo = target;
            Run(new AIObjectiveGoTo(spatial, Bot, Ai.ObjectiveManager, repeat: false, getDivingGearIfNeeded: true, closeEnough: 40f)
                { IgnoreIfTargetDead = false, AllowGoingOutside = true }, $"approaching {GameAdapter.InventoryOwnerRef(target)}");
        }
        else if (ObjectiveFailed || ObjectiveDone || Stalled(18)) return "cannot reach interaction range of " + GameAdapter.InventoryOwnerRef(target);
        return null;
    }

    void Fail(string error)
    {
        failures.Add($"#{current?.ID}: {error}");
        Next();
    }
    void Next() { current = null; ClearObjective(); walkingTo = null; }

    string Report(string? interrupted = null)
    {
        var unresolved = pending.Count + (current != null ? 1 : 0);
        bool complete = failures.Count == 0 && unresolved == 0 && interrupted == null;
        var result = (complete ? "ok: " : "failed: ")
            + $"transferred {moved.Count}/{wanted} to {GameAdapter.InventoryOwnerRef(destination.Owner)}"
            + (slot is { } s ? $" slot {s}" : "")
            + (moved.Count > 0 ? "; moved: " + string.Join(", ", moved) : "")
            + (already.Count > 0 ? "; already there: " + string.Join(", ", already) : "")
            + (failures.Count > 0 ? "; not moved: " + string.Join("; ", failures) : "")
            + (unresolved > 0 ? $"; {unresolved} unprocessed" : "")
            + (interrupted != null ? "; " + interrupted : "");
        return result;
    }
}

internal sealed class ApproachObjectTask : BotTask
{
    readonly Entity target;
    public override string Name => "approach " + GameAdapter.InventoryOwnerRef(target);
    protected override double TimeoutSeconds => 180;
    public ApproachObjectTask(Character bot, HumanAIController ai, Entity target) : base(bot, ai) => this.target = target;

    protected override string? Update()
    {
        if (target.Removed) return "failed: target was removed";
        bool reached = target == Bot || target is Item item && Bot.CanInteractWith(item)
            || target is Character character && Bot.CanInteractWith(character)
            || target is Hull hull && Bot.CurrentHull == hull
            || target is Submarine submarine && Bot.Submarine == submarine
            || target is not (Item or Character or Hull or Submarine) && Vector2.DistanceSquared(Bot.WorldPosition, target.WorldPosition) <= 120f * 120f;
        if (reached) return "ok: reached interaction range of " + GameAdapter.InventoryOwnerRef(target) + "; inspect_inventory can now verify compartment access";
        if (ObjectiveFailed || ObjectiveDone || Stalled(20)) return "failed: could not reach interaction range of " + GameAdapter.InventoryOwnerRef(target);
        if (!HasObjective && target is ISpatialEntity spatial)
            Run(new AIObjectiveGoTo(spatial, Bot, Ai.ObjectiveManager, repeat: false, getDivingGearIfNeeded: true, closeEnough: 40f)
                { IgnoreIfTargetDead = false, AllowGoingOutside = true }, "walking to " + GameAdapter.InventoryOwnerRef(target));
        return null;
    }
}
