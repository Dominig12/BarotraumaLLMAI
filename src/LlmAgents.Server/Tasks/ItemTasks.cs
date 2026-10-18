using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// Another item of the same kind when the chosen one was taken by someone else or could not be reached.
/// tried = items that already failed.
/// </summary>
internal delegate Item? FindReplacement(Item failed, ICollection<Item> tried);

internal sealed class EquipTask : BotTask
{
    const int MaxSwitches = 2;
    Item item;
    readonly FindReplacement? replace;
    readonly List<Item> tried = new();
    public override string Name => $"equip {item.Name}";
    protected override double TimeoutSeconds => 120;
    public override IEnumerable<Item> Claimed => new[] { item };

    public EquipTask(Character bot, HumanAIController ai, Item item, FindReplacement? replace = null) : base(bot, ai)
    {
        this.item = item;
        this.replace = replace;
    }

    protected override string? Update()
    {
        if (Bot.HasEquippedItem(item)) return $"ok: {item.Name} equipped" + (tried.Count > 0 ? " (took another one: the first was taken or out of reach)" : "");
        bool lost = item.Removed || (item.GetRootInventoryOwner() is Character owner && owner != Bot);
        if (lost || ObjectiveFailed)
        {
            // someone was faster (several bots ordered to suit up at once) or it is out of reach: try another one
            var why = lost ? (item.Removed ? "is gone" : "was taken by someone else") : "could not be reached";
            if (tried.Count >= MaxSwitches || replace?.Invoke(item, tried.Append(item).ToList()) is not { } next)
                return $"failed: could not get {item.Name} ({why})";
            tried.Add(item);
            ClearObjective();
            item = next;
        }
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
        if (ObjectiveDone && !Bot.HasEquippedItem(item))
            return Bot.Inventory.Contains(item) ? $"ok: {item.Name} is in your inventory (could not equip it)" : $"failed: could not get {item.Name}";
        return null;
    }
}

// ------------------------------------------------------------------------------------------- take

/// <summary>Picks items up into the bot's own inventory (not equipped): medicine to carry, spare tools, ammo.</summary>
internal sealed class TakeItemsTask : BotTask
{
    readonly Queue<Item> queue;
    readonly FindReplacement? replace;
    readonly HashSet<Item> got = new();
    readonly List<Item> tried = new();
    readonly string label;
    readonly int wanted;
    Item? current;
    bool full;
    public override string Name => $"take {label}";
    protected override double TimeoutSeconds => 60 + 30 * wanted;
    public override IEnumerable<Item> Claimed => current != null ? queue.Append(current) : queue;

    public TakeItemsTask(Character bot, HumanAIController ai, List<Item> items, string label, FindReplacement? replace = null) : base(bot, ai)
    {
        var unique = items.Distinct().ToList();
        queue = new Queue<Item>(unique);
        wanted = unique.Count;
        this.label = label;
        this.replace = replace;
    }

    protected override string? Update()
    {
        if (current != null && current.GetRootInventoryOwner() == Bot)
        {
            got.Add(current);
            current = null;
            ClearObjective();
        }
        if (current != null)
        {
            bool lost = current.Removed || (current.GetRootInventoryOwner() is Character owner && owner != Bot);
            // at the cabinet, but the objective does not take it (stuck, or it fits no pocket): take it by hand
            if (!lost && (Stalled(8) || ObjectiveFailed) && IsNear(current, 250f) && PickUp(current)) { got.Add(current); current = null; ClearObjective(); return null; }
            if (lost || ObjectiveFailed || Stalled(20) || (ObjectiveDone && current.GetRootInventoryOwner() != Bot))
            {
                ClearObjective();
                tried.Add(current);
                if (!lost && !HasRoom(current)) { full = true; return Report(); }
                current = tried.Count <= wanted + 2 ? Replace(current) : null; // not every cabinet on the sub
                if (current != null) Run(Get(current), $"getting {current.Name} from {ItemQueries.Where(current, Bot)}");
                return null;
            }
            return null;
        }
        while (queue.Count > 0)
        {
            Item? next = queue.Dequeue();
            if (next.Removed || (next.GetRootInventoryOwner() is Character other && other != Bot))
            {
                tried.Add(next);
                next = tried.Count <= wanted + 2 ? Replace(next) : null;
                if (next == null) continue;
            }
            if (next.GetRootInventoryOwner() == Bot) { got.Add(next); continue; }
            // the rest of a stack in the cabinet it is standing at: straight into the pockets, no walk per item
            if (IsNear(next, 150f))
            {
                if (PickUp(next)) { got.Add(next); continue; }
                full = true;
                return Report();
            }
            current = next;
            Run(Get(current), $"getting {current.Name} from {ItemQueries.Where(current, Bot)} ({got.Count + 1}/{wanted})");
            return null;
        }
        return Report();
    }

    AIObjectiveGetItem Get(Item i) => new(Bot, i, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false };

    Item? Replace(Item failed) => replace?.Invoke(failed, InventoryTaskRules.ReplacementExclusions(failed, tried, queue, got));

    bool HasRoom(Item i) =>
        Bot.Inventory.CanBePut(i, InvSlotType.Any) || Bot.Inventory.CanBePut(i, InvSlotType.RightHand) || Bot.Inventory.CanBePut(i, InvSlotType.LeftHand);

    string Report()
    {
        var left = wanted - got.Count;
        var why = full ? "your inventory is full" : "could not get them (taken or out of reach)";
        if (got.Count == 0) return $"failed: took nothing: {why}";
        var list = string.Join(", ", got.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"));
        return (left > 0 ? "failed: partially took " : "ok: took ") + $"{list} (in your inventory now)" + (left > 0 ? $"; {left} more not taken: {why}" : "");
    }
}

/// <summary>Eject one crafted product onto the floor beside its machine, even when both hands are occupied.</summary>
internal sealed class DropOutputTask : BotTask
{
    readonly Item item;
    public override string Name => $"drop {ItemQueries.InstanceLabel(item)} from machine output";
    public override IEnumerable<Item> Claimed => new[] { item };

    public DropOutputTask(Character bot, HumanAIController ai, Item item) : base(bot, ai) { this.item = item; }

    protected override string? Update()
    {
        if (item.Removed) return "failed: product is gone";
        if (item.GetRootInventoryOwner() is Character owner && owner != Bot) return $"failed: {owner.Name} took {item.Name}";
        if (!GameAdapter.InMachineOutput(item) && item.GetRootInventoryOwner() != Bot)
            return item.ParentInventory == null ? $"ok: {item.Name} is already on the floor" : "failed: product was moved out of the machine";
        var target = item.Container ?? item;
        if (!Bot.CanInteractWith(target))
        {
            if (ObjectiveFailed || ObjectiveDone || Stalled(20)) return $"failed: cannot reach {target.Name}";
            if (!HasObjective) Run(GoTo(target, 80f), $"going to the output of {target.Name}");
            return null;
        }
        ClearObjective();
        if (item.ParentInventory != null && GameAdapter.InventoryAccessProblem(Bot, item.ParentInventory) is { } accessError) return "failed: " + accessError;
        item.Drop(Bot);
        return item.ParentInventory == null ? $"ok: dropped {ItemQueries.InstanceLabel(item)} beside the machine" : $"failed: could not remove {item.Name} from the machine";
    }
}

/// <summary>An AI crewmate hands items over: walk up to them, then the items go from their pockets into the bot's.</summary>
internal sealed class ReceiveItemsTask : BotTask
{
    readonly Character holder;
    readonly List<Item> items;
    readonly List<Item> got = new();
    public override string Name => $"get {string.Join(", ", items.Select(i => i.Name).Distinct())} from {holder.Name}";
    protected override double TimeoutSeconds => 120;
    public override IEnumerable<Item> Claimed => items;

    public ReceiveItemsTask(Character bot, HumanAIController ai, Character holder, List<Item> items) : base(bot, ai)
    {
        this.holder = holder;
        this.items = items;
    }

    protected override string? Update()
    {
        if (holder.IsDead || holder.Removed) return $"failed: {holder.Name} is gone";
        if (!Bot.CanInteractWith(holder))
        {
            if (!Running) Run(GoTo(holder, 100f), $"going to {holder.Name}");
            else if (Stalled(15)) return $"failed: could not get to {holder.Name}";
            return null;
        }
        ClearObjective();
        foreach (var i in items.Where(i => !i.Removed && i.GetRootInventoryOwner() == holder))
            if (PickUp(i)) got.Add(i);
        if (got.Count == 0) return $"failed: took nothing from {holder.Name} (no room, or they no longer carry it)";
        var list = string.Join(", ", got.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"));
        return (got.Count == items.Count ? "ok: " : "failed: partially received; ") + $"{holder.Name} handed over {list} (in your inventory now)";
    }
}

/// <summary>
/// Empties a crate (or any container) and puts every item where it belongs (<see cref="StoragePlanner"/>: onto stacks
/// of the same items first, then next to similar items, then the usual places). A crate is carried along; from a fixed
/// container the bot takes what fits in its inventory, delivers it and comes back. Items with no fitting place stay in
/// the container. The empty crate goes to a crate shelf if there is one, else it is put down.
/// </summary>
internal sealed class UnpackTask : BotTask
{
    const int MaxDestinations = 12;
    readonly Item box;
    readonly ItemContainer boxContainer;
    readonly bool carry;
    readonly HashSet<Item> left = new();                // no fitting place: stays in the box
    readonly HashSet<Item> unreachable = new();         // containers the bot could not get to
    readonly List<(string What, string Where, string Why)> done = new();
    readonly List<Item> load = new();                   // items taken from the box (not carrying it) for the current trip
    ItemContainer? target;
    string targetWhy = "";
    int trips;
    enum Phase { Fetch, Choose, Walk, Return, Finish }
    Phase phase;
    bool carrying;
    readonly Vector2 origin;

    public override string Name => $"unpack {box.Name}";
    protected override double TimeoutSeconds => 420;
    public override IEnumerable<Item> Claimed => new[] { box };

    public UnpackTask(Character bot, HumanAIController ai, Item box, ItemContainer container, bool carry) : base(bot, ai)
    {
        this.box = box;
        boxContainer = container;
        this.carry = carry && box.GetComponent<Pickable>() != null;
        origin = box.WorldPosition;
        phase = this.carry ? Phase.Fetch : Phase.Choose;
    }

    List<Item> Contents() => boxContainer.Inventory.AllItems.Where(i => !left.Contains(i)).ToList();

    protected override string? Update()
    {
        if (box.Removed) return Report("the container is gone");
        carrying = box.GetRootInventoryOwner() == Bot;
        switch (phase)
        {
            case Phase.Fetch:
                if (carrying) { ClearObjective(); phase = Phase.Choose; return null; }
                if (box.GetRootInventoryOwner() is Character other) return Report($"{other.Name} has it");
                if (!HasObjective) Run(new AIObjectiveGetItem(Bot, box, Ai.ObjectiveManager, equip: true) { SpeakIfFails = false }, $"picking up {box.Name}");
                else if (ObjectiveFailed || ObjectiveDone)
                {
                    // could not carry it (hands full, too heavy...): unpack it where it is
                    ClearObjective();
                    phase = Phase.Choose;
                }
                return null;

            case Phase.Choose:
            {
                if (trips >= MaxDestinations) { phase = Phase.Finish; return null; }
                // what the bot already took out goes first
                var next = load.FirstOrDefault(i => !i.Removed && i.GetRootInventoryOwner() == Bot) ?? Contents().FirstOrDefault();
                if (next == null) { phase = Phase.Finish; return null; }
                var exclude = new HashSet<Item>(unreachable) { box };
                var choice = StoragePlanner.Choose(Bot, next, exclude, origin);
                if (choice == null)
                {
                    if (load.Remove(next)) PutBack(next);
                    left.Add(next);
                    return null;
                }
                target = choice.Container;
                targetWhy = choice.Why;
                if (!carrying && !load.Contains(next))
                {
                    // take what goes to the same place (and fits in the inventory) from the box first
                    if (!IsNear(box, 140f)) { if (!HasObjective || ObjectiveDone) Run(GoTo(box, 80f), $"going to {box.Name}"); else if (ObjectiveFailed) return Report($"cannot reach {box.Name}"); return null; }
                    ClearObjective();
                    foreach (var i in Contents().Where(i => StoragePlanner.Choose(Bot, i, exclude, origin)?.Container == target).Take(40))
                        if (InventoryView.TryStore(Bot, i, Bot.Inventory)) load.Add(i);
                    if (load.Count == 0) { left.Add(next); return null; }   // no room in the inventory for it
                }
                trips++;
                Run(GoTo(target.Item, 80f), $"taking things to {StoragePlanner.Describe(target)}");
                phase = Phase.Walk;
                return null;
            }

            case Phase.Walk:
                if (target == null || target.Item.Removed) { phase = Phase.Choose; return null; }
                if (IsNear(target.Item, 160f))
                {
                    ClearObjective();
                    if (PutInto(target) == 0) unreachable.Add(target.Item);   // full after all: not again
                    target = null;
                    phase = carrying || Contents().Count == 0 ? Phase.Choose : Phase.Return;
                    if (phase == Phase.Return && load.Count > 0) phase = Phase.Choose;   // deliver the rest first
                    return null;
                }
                if (ObjectiveFailed || ObjectiveDone)
                {
                    unreachable.Add(target.Item);
                    target = null;
                    ClearObjective();
                    phase = Phase.Choose;
                }
                return null;

            case Phase.Return:
                ClearObjective();
                phase = Phase.Choose;     // Choose walks back to the box when it needs to
                return null;

            case Phase.Finish:
            default:
                // what could not be delivered goes back into the box
                foreach (var i in load.Where(i => !i.Removed && i.GetRootInventoryOwner() == Bot).ToList()) PutBack(i);
                PlaceBox();
                return Report(null);
        }
    }

    /// <summary>Everything for this container: from the carried box, or what the bot took out of it.</summary>
    int PutInto(ItemContainer to)
    {
        int n = 0;
        var source = carrying ? Contents() : load.Where(i => !i.Removed && i.GetRootInventoryOwner() == Bot).ToList();
        var exclude = new HashSet<Item>(unreachable) { box };
        foreach (var i in source)
        {
            if (StoragePlanner.Choose(Bot, i, exclude, origin)?.Container != to) continue;
            if (!InventoryView.TryStore(Bot, i, to.Inventory)) continue;
            load.Remove(i);
            done.Add((i.Prefab.Name.Value, StoragePlanner.Describe(to), targetWhy));
            n++;
        }
        return n;
    }

    void PutBack(Item i)
    {
        // If access or capacity changed, keep the item rather than silently throwing it away.
        InventoryView.TryStore(Bot, i, boxContainer.Inventory);
    }

    protected override void OnFinish() => PlaceBox();

    /// <summary>The bot does not keep walking around with the box in its hands.</summary>
    void PlaceBox()
    {
        if (box.Removed || box.GetRootInventoryOwner() != Bot) return;
        var shelf = StoragePlanner.Choose(Bot, box, new HashSet<Item> { box });
        if (shelf != null && InventoryView.TryStore(Bot, box, shelf.Container.Inventory))
            boxPlace = StoragePlanner.Describe(shelf.Container);
        else
        {
            box.Drop(Bot);
            boxPlace = Bot.CurrentHull != null ? GameAdapter.RoomId(Bot.CurrentHull) : "here";
        }
    }

    string? boxPlace;

    string Report(string? problem)
    {
        var parts = done.GroupBy(d => (d.What, d.Where)).Select(g => $"{g.Key.What} x{g.Count()} -> {g.Key.Where}").ToList();
        var stayed = boxContainer.Inventory.AllItems.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}").ToList();
        var s = parts.Count == 0 ? $"failed: put nothing away from {box.Name}" : (problem == null && stayed.Count == 0 && load.All(i => i.GetRootInventoryOwner() != Bot) ? "ok: " : "failed: partially completed; ") + $"put away from {box.Name}: {string.Join("; ", parts.Take(10))}";
        if (problem != null) s += $" ({problem})";
        if (stayed.Count > 0) s += $"; still in it (no fitting place or no time): {string.Join(", ", stayed.Take(8))}";
        if (boxPlace != null) s += $"; {box.Name} left in {boxPlace}";
        return s;
    }
}

/// <summary>Carry an item into a container, to a room (dropped there) or into a crew member's inventory.</summary>
internal sealed class MoveItemTask : BotTask
{
    readonly List<Item> items;
    readonly ItemContainer? container;
    readonly Hull? room;
    readonly Character? receiver;
    readonly string destination;
    readonly FindReplacement? replace;
    readonly List<Item> tried = new();
    readonly HashSet<Item> failed = new();
    readonly List<Item> delivered = new();
    Item? fetching;
    bool full;
    int walkTries;
    string label => items.Count == 0 ? "items" : items.Count == 1 ? items[0].Name : $"{items[0].Name} x{items.Count}";
    public override string Name => $"move {label} to {destination}";
    protected override double TimeoutSeconds => 150 + 10 * items.Count;
    public override IEnumerable<Item> Claimed => items;

    /// <param name="items">One stack (or one item): picked up into the pockets together, delivered together.</param>
    public MoveItemTask(Character bot, HumanAIController ai, List<Item> items, ItemContainer? container, Hull? room, Character? receiver, string destination,
        FindReplacement? replace = null)
        : base(bot, ai)
    {
        this.items = items;
        this.container = container;
        this.room = room;
        this.receiver = receiver;
        this.destination = destination;
        // the bot's own item: that one, no other
        this.replace = items.Count == 1 && items[0].GetRootInventoryOwner() != bot ? replace : null;
    }

    bool Mine(Item i) => !i.Removed && i.GetRootInventoryOwner() == Bot;
    bool Lost(Item i) => i.Removed || (i.GetRootInventoryOwner() is Character owner && owner != Bot);

    protected override string? Update()
    {
        if (container != null && items.All(i => i.Removed || failed.Contains(i) || i.ParentInventory == container.Inventory))
            return Report();

        // 1. everything into the pockets first: one trip, not one per item (the vanilla objectives carry them one by one)
        while (!full)
        {
            if (fetching != null && Mine(fetching)) { fetching = null; ClearObjective(); }
            if (fetching != null && !Lost(fetching) && (ObjectiveFailed || ObjectiveDone || Stalled(6)) && IsNear(fetching, 250f))
            {
                if (PickUp(fetching)) { fetching = null; ClearObjective(); continue; }
                if (items.Any(Mine)) { fetching = null; ClearObjective(); full = true; break; }
            }
            if (fetching != null && (Lost(fetching) || ObjectiveFailed || Stalled(15)))
            {
                ClearObjective();
                var lostOne = fetching;
                fetching = null;
                if (replace != null && tried.Count < 2 && replace(lostOne, tried.Append(lostOne).ToList()) is { } next)
                {
                    tried.Add(lostOne);
                    items[items.IndexOf(lostOne)] = next;
                    continue;
                }
                failed.Add(lostOne);
                continue;
            }
            if (fetching != null)
            {
                // standing at the cabinet but the objective does not take it: reach in
                if ((Stalled(6) || ObjectiveFailed) && IsNear(fetching, 250f) && PickUp(fetching)) { fetching = null; ClearObjective(); continue; }
                if (ObjectiveDone) { ClearObjective(); failed.Add(fetching); fetching = null; continue; }   // done, but not in the pockets
                if (!HasObjective) Run(Get(fetching), Step);
                return null;
            }
            var pending = items.Where(i => !Mine(i) && !failed.Contains(i) && !delivered.Contains(i) && !(container != null && i.ParentInventory == container.Inventory)).ToList();
            if (pending.Count == 0) break;
            foreach (var p in pending.Where(Lost).ToList()) { failed.Add(p); pending.Remove(p); }
            if (pending.Count == 0) break;
            var near = pending.FirstOrDefault(i => IsNear(i, 150f));
            if (near != null)
            {
                if (PickUp(near)) continue;
                full = items.Any(Mine);       // no room left: deliver what it has
                if (!full) { failed.Add(near); continue; }
                break;
            }
            fetching = pending.OrderBy(i => Vector2.DistanceSquared(i.WorldPosition, Bot.WorldPosition)).First();
            Run(Get(fetching), $"getting {fetching.Name} from {ItemQueries.Where(fetching, Bot)} ({items.Count(Mine) + 1}/{items.Count})");
            return null;
        }

        var load = items.Where(i => Mine(i) && !failed.Contains(i) && !delivered.Contains(i)).ToList();
        if (load.Count == 0) return Report();

        // 2. to the destination, then everything at once
        if (container != null)
        {
            var arrived = Arrive(container.Item, 160f, $"carrying {label} to {destination}");
            if (arrived != null) return arrived.Length == 0 ? null : arrived;
            foreach (var i in load)
                if (InventoryView.TryStore(Bot, i, container.Inventory)) delivered.Add(i); else failed.Add(i);
            full = false;
            walkTries = 0;
            return null; // bulky products need more than one trip; fetch the rest after unloading
        }

        if (receiver != null)
        {
            if (receiver.Removed || receiver.IsDead) return $"failed: {receiver.Name} is dead";
            if (!Bot.CanInteractWith(receiver))
            {
                if (!HasObjective) Run(GoTo(receiver, 60f), $"bringing {label} to {receiver.Name}");
                else if (ObjectiveFailed) return $"failed: cannot reach {receiver.Name}";
                return null;
            }
            ClearObjective();
            foreach (var i in load)
            {
                if (InventoryView.TryStore(Bot, i, receiver.Inventory, allowGive: true)) delivered.Add(i);
                else failed.Add(i);
            }
            full = false;
            return null;
        }

        if (room != null)
        {
            if (Bot.CurrentHull != room)
            {
                if (!HasObjective) Run(GoTo(room, 80f), $"carrying {label} to {GameAdapter.RoomId(room)}");
                else if (ObjectiveFailed) return $"failed: cannot reach {GameAdapter.RoomId(room)}";
                else if (ObjectiveDone && Bot.CurrentHull != room) return $"failed: did not get into {GameAdapter.RoomId(room)}";
                return null;
            }
            ClearObjective();
            foreach (var i in load) { i.Drop(Bot); delivered.Add(i); }
            full = false;
            return null;
        }
        return "failed: no destination";
    }

    AIObjectiveGetItem Get(Item i) => new(Bot, i, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false };

    /// <summary>null = there; "" = walking; else the failure.</summary>
    string? Arrive(Item target, float near, string step)
    {
        if (IsNear(target, near) || Bot.CanInteractWith(target)) { ClearObjective(); return null; }
        if (!HasObjective) { Run(GoTo(target, 60f), step); return ""; }
        if (ObjectiveFailed || ObjectiveDone || Stalled(6))
        {
            if (IsNear(target, 400f)) { ClearObjective(); return null; }   // the path ends a little short of it
            ClearObjective();
            return ++walkTries <= 2 ? "" : $"failed: cannot reach {destination}";
        }
        return "";
    }

    string Report()
    {
        var where = receiver != null ? $"gave to {receiver.Name}" : container != null ? $"in {destination}" : $"left in {destination}";
        var inPlace = container != null ? items.Where(i => !i.Removed && i.ParentInventory == container.Inventory).ToList() : delivered;
        string Count(IEnumerable<Item> l) => string.Join(", ", l.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"));
        if (inPlace.Count == 0)
            return $"failed: could not move {label} ({(full ? "your inventory is full" : failed.Count > 0 ? "could not get it or it did not fit" : "nothing to move")})";
        var notDone = items.Where(i => !inPlace.Contains(i)).ToList();
        var prefix = notDone.Count == 0 ? "ok: " : "failed: partially moved; ";
        var r = receiver != null ? $"{prefix}gave {Count(inPlace)} to {receiver.Name}" : $"{prefix}{Count(inPlace)} {where}";
        if (notDone.Count > 0)
            r += $"; {notDone.Count} not moved ({(full ? "inventory full" : "out of reach or did not fit")}){(notDone.Any(Mine) ? ", still in your inventory" : "")}";
        return r;
    }
}
