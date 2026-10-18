using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using LlmAgents.Agents;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>The bot's inventory in words: slots, hands, what is inside tools and bags, how full things are.</summary>
internal static class InventoryView
{
    public static IEnumerable<int> AutomaticSlots(Inventory inventory, bool pocketsOnly = false) => Enumerable.Range(0, inventory.Capacity)
        .Where(index => inventory is not CharacterInventory c || index < c.SlotTypes.Length &&
            (c.SlotTypes[index] == InvSlotType.Any || !pocketsOnly && (c.SlotTypes[index] == InvSlotType.LeftHand || c.SlotTypes[index] == InvSlotType.RightHand)))
        .OrderBy(index => inventory is CharacterInventory c && c.SlotTypes[index] != InvSlotType.Any ? 1 : 0);

    /// <summary>Native drag-and-drop giving permits insertion into teammates' pockets without exposing their inventory.</summary>
    public static bool AllowsGiving(Character giver, Inventory inventory, int? slot = null, bool requireRange = true) =>
        inventory is CharacterInventory character && inventory.Owner is Character receiver && receiver != giver
        && receiver.TeamID == giver.TeamID && !receiver.Removed && !receiver.IsDead && !inventory.Locked && Inventory.IsDragAndDropGiveAllowed
        && (slot == null || slot >= 0 && slot < character.SlotTypes.Length && character.SlotTypes[slot.Value] == InvSlotType.Any)
        && (!requireRange || giver.CanInteractWith(receiver));

    public static bool GivingOnly(Character giver, Inventory inventory) =>
        GameAdapter.InventoryAccessProblem(giver, inventory, requireRange: false) != null && AllowsGiving(giver, inventory, requireRange: false);

    /// <summary>Ordinary storage placement preserves existing stacks/condition and enforces source and target access.</summary>
    public static bool TryStore(Character c, Item item, Inventory inventory, bool allowGive = false)
    {
        if (item.Removed || inventory.Owner.Removed) return false;
        if (item.ParentInventory == inventory) return true;
        if (InventoryTransferRules.WouldCreateCycle(item, inventory.Owner as Item, i => i.Container)) return false;
        bool giving = allowGive && AllowsGiving(c, inventory);
        if (!giving && GameAdapter.InventoryAccessProblem(c, inventory) != null) return false;
        if (item.ParentInventory != null && GameAdapter.InventoryAccessProblem(c, item.ParentInventory) != null) return false;
        if (item.ParentInventory == null && !c.CanInteractWith(item)) return false;
        foreach (int slot in AutomaticSlots(inventory))
        {
            if (giving && inventory is CharacterInventory given && given.SlotTypes[slot] != InvSlotType.Any) continue;
            if (inventory.CanBePutInSlot(item, slot) && inventory.TryPutItem(item, slot, allowSwapping: false, allowCombine: false, user: c)
                && item.ParentInventory == inventory) return true;
        }
        return false;
    }

    public static string? SlotName(Character c, Item i)
    {
        int idx = c.Inventory?.FindIndex(i) ?? -1;
        if (idx < 0 || idx >= c.Inventory!.SlotTypes.Length) return null;
        return c.Inventory.SlotTypes[idx] switch
        {
            InvSlotType.RightHand => "right hand",
            InvSlotType.LeftHand => "left hand",
            InvSlotType.Head => "head",
            InvSlotType.InnerClothes => "clothes",
            InvSlotType.OuterClothes => "suit",
            InvSlotType.Headset => "headset",
            InvSlotType.Card => "id card",
            InvSlotType.Bag => "bag",
            InvSlotType.HealthInterface => "health",
            _ => null,
        };
    }

    static float Fraction(Item i) => i.MaxCondition > 0 ? i.Condition / i.MaxCondition : 1f;

    /// <summary>" 40%" when the item is worn down or used up (tanks, batteries, tools).</summary>
    public static string Condition(Item i) => i.MaxCondition > 0 && Fraction(i) < 0.995f ? $" {Fraction(i) * 100:0}%" : "";

    /// <summary>" [in it: Бак со сварочным топливом 40%]" for tools, guns, suits and bags.</summary>
    public static string Contents(Item i, int max = 6)
    {
        var inside = i.GetComponents<ItemContainer>().SelectMany(ic => ic.Inventory.AllItems).Distinct().ToList();
        if (inside.Count == 0)
            return i.GetComponent<ItemContainer>() is { Capacity: <= 4 } && !GameAdapter.IsStorage(i) ? " [empty]" : "";
        var groups = inside.GroupBy(x => x.Prefab).Take(max)
            .Select(g => g.Count() > 1 ? $"{g.Key.Name.Value} x{g.Count()}" : g.Key.Name.Value + Condition(g.First()));
        return $" [in it: {string.Join(", ", groups)}]";
    }

    public static string Describe(Item i) => ItemQueries.InstanceLabel(i) + Condition(i) + Contents(i);

    public static bool IsBoring(Item i) => i.HasTag("identitycard".ToIdentifier());

    /// <summary>"3 free slots; hands: right Сварочный аппарат, left empty".</summary>
    public static string Space(Character c)
    {
        var inv = c.Inventory;
        if (inv == null) return "no inventory";
        int free = 0;
        for (int i = 0; i < inv.Capacity && i < inv.SlotTypes.Length; i++)
            if (inv.SlotTypes[i] == InvSlotType.Any && inv.IsSlotEmpty(i)) free++;
        var r = inv.GetItemInLimbSlot(InvSlotType.RightHand);
        var l = inv.GetItemInLimbSlot(InvSlotType.LeftHand);
        var left = l == null ? "empty" : l == r ? "the same (two-handed)" : l.Name;
        return $"{free} free slot{(free == 1 ? "" : "s")}; hands: right {r?.Name ?? "empty"}, left {left}";
    }

    /// <summary>Tools, guns, suits... the bot carries whose fuel, tank, battery or ammo is used up or missing.</summary>
    public static List<(Item Holder, ItemContainer Ic, Item? Old)> NeedRefill(Character c, Item? only = null)
    {
        var list = new List<(Item, ItemContainer, Item?)>();
        foreach (var holder in GameAdapter.Carried(c).Where(i => only == null || i == only))
        {
            foreach (var ic in holder.GetComponents<ItemContainer>())
            {
                if (!ItemRules.TakesRefills(ic.Capacity, GameAdapter.IsStorage(holder), ic.ContainableItems?.Count ?? 0)) continue;
                if (!ItemRules.IsRefillSlot(ic.ContainableItems!.SelectMany(r => r.Identifiers).Select(id => id.Value))) continue;
                if (GameAdapter.InventoryAccessProblem(c, ic.Inventory, requireRange: false) != null) continue;
                var inside = ic.Inventory.AllItems.Distinct().ToList();
                var spent = inside.Where(x => x.MaxCondition > 0 && ItemRules.IsSpent(Fraction(x))).OrderBy(Fraction).FirstOrDefault();
                if (spent != null) list.Add((holder, ic, spent));
                else if (inside.Count == 0) list.Add((holder, ic, null));
            }
        }
        return list;
    }

    /// <summary>The best item on the sub (or in the inventory) to load into that slot: the same kind as before, fullest, nearest.</summary>
    public static Item? BestRefill(Character c, Item holder, ItemContainer ic, Item? old, ICollection<Item> skip) =>
        ItemQueries.Available(c)
            .Where(i => i != holder && i != old && !skip.Contains(i) && ItemRules.IsUsableRefill(Fraction(i)) && ic.CanBeContained(i))
            // loose, in the inventory or in a cabinet: not out of a machine or another tool
            .Where(i => i.Container == null || GameAdapter.IsStorage(i.Container))
            .OrderByDescending(i => old != null && i.Prefab == old.Prefab ? 1 : 0)
            .ThenByDescending(i => i.GetRootInventoryOwner() == c ? 1 : 0)
            .ThenByDescending(Fraction)
            .ThenBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition))
            .FirstOrDefault();

    /// <summary>Loads a container using normal access checks. A displaced refill is kept safely; failed insertion restores it.</summary>
    /// <returns>null = done, text = why not</returns>
    public static string? PutInto(Character c, Item item, ItemContainer ic, out Item? removed)
    {
        removed = null;
        if (item.ParentInventory == ic.Inventory) return null;
        if (InventoryTransferRules.WouldCreateCycle(item, ic.Item, i => i.Container)) return "cannot put a container into itself or its descendants";
        if (GameAdapter.InventoryAccessProblem(c, ic.Inventory) is { } destinationError) return destinationError;
        if (item.ParentInventory != null && GameAdapter.InventoryAccessProblem(c, item.ParentInventory) is { } sourceError) return sourceError;
        if (item.ParentInventory == null && !c.CanInteractWith(item)) return $"{item.Name} is out of interaction range";
        if (!ic.CanBeContained(item)) return $"{item.Name} does not fit into {ic.Item.Name}";
        int oldSlot = -1;
        Item? displaced = null;
        if (!ic.Inventory.CanBePut(item))
        {
            if (GameAdapter.IsStorage(ic.Item)) return $"{ic.Item.Name} is full; make room first";
            var old = ic.Inventory.AllItems.Where(x => x.Prefab != item.Prefab || ItemRules.IsSpent(Fraction(x))).OrderBy(Fraction).FirstOrDefault()
                      ?? ic.Inventory.AllItems.OrderBy(Fraction).FirstOrDefault();
            if (old == null) return $"{ic.Item.Name} is full";
            oldSlot = ic.Inventory.FindIndex(old);
            bool unloaded = AutomaticSlots(c.Inventory, pocketsOnly: true).Any(index => c.Inventory.CanBePutInSlot(old, index)
                && c.Inventory.TryPutItem(old, index, allowSwapping: false, allowCombine: false, user: c) && old.ParentInventory == c.Inventory);
            if (!unloaded)
                return $"no free pocket slot to unload {old.Name}; existing contents were preserved (store something first)";
            displaced = old;
        }
        if (TryStore(c, item, ic.Inventory)) { removed = displaced; return null; }
        if (displaced != null)
        {
            bool restored = oldSlot >= 0 && ic.Inventory.TryPutItem(displaced, oldSlot, allowSwapping: false, allowCombine: false, user: c)
                && displaced.ParentInventory == ic.Inventory;
            if (!restored) return $"could not put {item.Name} into {ic.Item.Name}; unloaded #{displaced.ID} {displaced.Name} remains safely in your inventory";
        }
        return $"could not put {item.Name} into {ic.Item.Name}; previous contents were preserved";
    }
}

// ------------------------------------------------------------------------------------------- put in / refill

/// <summary>Loads items into carried tools, guns, suits or bags (fuel into the welding tool, a tank into the suit...),
/// getting them from the sub first.</summary>
internal sealed class PutInTask : BotTask
{
    readonly Queue<(Item Item, ItemContainer Into)> todo;
    readonly List<string> done = new();
    readonly List<string> problems = new();
    readonly string label;
    readonly int wanted;
    bool walkingToDestination;
    (Item Item, ItemContainer Into)? current;
    public override string Name => label;
    protected override double TimeoutSeconds => 60 + 60 * Math.Max(1, wanted);
    public override IEnumerable<Item> Claimed => todo.SelectMany(t => new[] { t.Item, t.Into.Item })
        .Concat(current is { } c ? new[] { c.Item, c.Into.Item } : Array.Empty<Item>());

    public PutInTask(Character bot, HumanAIController ai, List<(Item, ItemContainer)> pairs, string label) : base(bot, ai)
    {
        todo = new Queue<(Item, ItemContainer)>(pairs);
        wanted = pairs.Count;
        this.label = label;
    }

    protected override string? Update()
    {
        if (current == null)
        {
            if (todo.Count == 0) return Report();
            current = todo.Dequeue();
            ClearObjective();
            walkingToDestination = false;
        }
        var (item, into) = current.Value;
        if (item.Removed || into.Item.Removed || into.Item.GetRootInventoryOwner() is Character holder && holder != Bot)
        {
            problems.Add($"{item.Name}: item or destination gone or carried by someone else");
            ClearObjective();
            current = null;
            return null;
        }
        if (item.ParentInventory == into.Inventory) { done.Add($"{item.Name} -> {into.Item.Name}"); ClearObjective(); current = null; return null; }
        if (item.GetRootInventoryOwner() is Character owner && owner != Bot) { problems.Add($"{owner.Name} took {item.Name}"); ClearObjective(); current = null; return null; }
        bool atDestination = GameAdapter.InventoryAccessProblem(Bot, into.Inventory) == null;
        if (item.GetRootInventoryOwner() != Bot && !(atDestination && IsNear(item, 180f)))
        {
            if (walkingToDestination) { ClearObjective(); walkingToDestination = false; }
            if ((ObjectiveDone || ObjectiveFailed || Stalled(6)) && Bot.CanInteractWith(item)
                && (item.ParentInventory == null || GameAdapter.InventoryAccessProblem(Bot, item.ParentInventory) == null) && PickUp(item)) { ClearObjective(); return null; }
            if (!HasObjective) Run(new AIObjectiveGetItem(Bot, item, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false }, $"getting {item.Name} from {ItemQueries.Where(item, Bot)}");
            else if (ObjectiveFailed || ObjectiveDone || Stalled(20)) { problems.Add($"could not get {item.Name} (no room or out of reach)"); ClearObjective(); current = null; }
            return null;
        }
        if (!atDestination)
        {
            if (!walkingToDestination)
            {
                ClearObjective(); // the completed get-item objective belongs to the previous stage
                Run(GoTo(into.Item, 80f), $"loading {item.Name} into {ItemQueries.InstanceLabel(into.Item)}");
                walkingToDestination = true;
                return null;
            }
            if (ObjectiveDone || ObjectiveFailed || Stalled(20)) { problems.Add($"could not reach {into.Item.Name}"); ClearObjective(); current = null; return null; }
            return null;
        }
        ClearObjective();
        var err = InventoryView.PutInto(Bot, item, into, out var removed);
        if (err != null) problems.Add(err);
        else done.Add($"{item.Name}{InventoryView.Condition(item)} -> {into.Item.Name}" + (removed != null ? $" (took out {removed.Name}{InventoryView.Condition(removed)})" : ""));
        current = null;
        return null;
    }

    string Report()
    {
        if (done.Count == 0) return "failed: " + (problems.Count > 0 ? string.Join("; ", problems.Take(4)) : "nothing loaded");
        return (problems.Count == 0 ? "ok: " : "failed: partially loaded; ") + string.Join("; ", done) + (problems.Count > 0 ? "; not done: " + string.Join("; ", problems.Take(4)) : "");
    }
}

// ------------------------------------------------------------------------------------------- tidy up

/// <summary>Puts what the bot carries but does not need back where it belongs (<see cref="StoragePlanner"/>).</summary>
internal sealed class TidyInventoryTask : BotTask
{
    readonly List<Item> items;
    readonly HashSet<Item> left = new();
    readonly HashSet<Item> unreachable = new();
    readonly List<(string What, string Where)> done = new();
    ItemContainer? target;
    int trips;
    public override string Name => "put away carried items";
    protected override double TimeoutSeconds => 300;

    public TidyInventoryTask(Character bot, HumanAIController ai, List<Item> items) : base(bot, ai) { this.items = items; }

    List<Item> Pending() => items.Where(i => !i.Removed && !left.Contains(i) && i.GetRootInventoryOwner() == Bot && !Bot.HasEquippedItem(i)).ToList();

    protected override string? Update()
    {
        if (target == null)
        {
            var next = Pending().FirstOrDefault();
            if (next == null || trips >= 10) return Report();
            var choice = StoragePlanner.Choose(Bot, next, unreachable);
            if (choice == null) { left.Add(next); return null; }
            target = choice.Container;
            trips++;
            Run(GoTo(target.Item, 80f), $"taking things to {StoragePlanner.Describe(target)}");
            return null;
        }
        if (target.Item.Removed) { target = null; return null; }
        if (IsNear(target.Item, 160f))
        {
            ClearObjective();
            int n = 0;
            foreach (var i in Pending())
            {
                if (StoragePlanner.Choose(Bot, i, unreachable)?.Container != target) continue;
                if (InventoryView.TryStore(Bot, i, target.Inventory)) { done.Add((i.Prefab.Name.Value, StoragePlanner.Describe(target))); n++; }
            }
            if (n == 0) unreachable.Add(target.Item);
            target = null;
            return null;
        }
        if (ObjectiveFailed || ObjectiveDone)
        {
            unreachable.Add(target.Item);
            target = null;
            ClearObjective();
        }
        return null;
    }

    string Report()
    {
        var parts = done.GroupBy(d => d).Select(g => $"{g.Key.What} x{g.Count()} -> {g.Key.Where}").ToList();
        var kept = items.Where(i => !i.Removed && i.GetRootInventoryOwner() == Bot && !Bot.HasEquippedItem(i)).GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}").ToList();
        var s = parts.Count == 0 ? "failed: put nothing away (no fitting place)" : (kept.Count == 0 ? "ok: put away " : "failed: partially put away ") + string.Join("; ", parts.Take(10));
        if (kept.Count > 0) s += "; still carrying: " + string.Join(", ", kept.Take(8));
        return s;
    }
}
