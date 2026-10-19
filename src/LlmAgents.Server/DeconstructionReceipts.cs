using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using LlmAgents.Agents;

namespace LlmAgents;

/// <summary>Bounded, task-owned proof that the native deconstructor actually queued this exact input for removal.</summary>
internal static class DeconstructionReceipts
{
    internal sealed class Receipt : IDisposable
    {
        public readonly Item Item;
        public readonly Character Bot;
        public Deconstructor? Machine { get; internal set; }
        public bool Confirmed { get; internal set; }
        internal Receipt(Item item, Character bot) { Item = item; Bot = bot; }
        public void Dispose() => active.Remove(this);
    }

    sealed record Attempt(Item Item, Deconstructor Machine, Receipt[] Receipts, bool RemovedBefore, bool QueuedBefore);
    static readonly HashSet<Receipt> active = new();
    static Harmony? harmony;
    public static bool Patched { get; private set; }

    public static Receipt Watch(Item item, Character bot)
    {
        var receipt = new Receipt(item, bot);
        active.Add(receipt);
        return receipt;
    }

    public static void Patch()
    {
        if (Patched) return;
        var method = AccessTools.Method(typeof(Deconstructor), "ProcessItem",
            new[] { typeof(Item), typeof(IEnumerable<Item>), typeof(List<DeconstructItem>), typeof(bool) });
        if (method == null) throw new MissingMethodException("native Deconstructor.ProcessItem signature is unavailable");
        harmony = new Harmony("llmagents.deconstruction-receipts");
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(DeconstructionReceipts), nameof(BeforeProcess)),
            postfix: new HarmonyMethod(typeof(DeconstructionReceipts), nameof(AfterProcess)));
        Patched = true;
    }

    static void BeforeProcess(Deconstructor __instance, Item targetItem, bool allowRemove, out Attempt? __state)
    {
        __state = null;
        try
        {
            if (active.Count == 0 || !allowRemove || !targetItem.AllowDeconstruct
                || targetItem.ParentInventory != __instance.InputContainer.Inventory) return;
            bool removedBefore = targetItem.Removed;
            bool queuedBefore = Entity.Spawner?.IsInRemoveQueue(targetItem) == true;
            var receipts = active.Where(receipt => !receipt.Confirmed && receipt.Item == targetItem
                && receipt.Bot == __instance.user && (receipt.Machine == null || receipt.Machine == __instance)).ToArray();
            if (receipts.Length > 0) __state = new Attempt(targetItem, __instance, receipts, removedBefore, queuedBefore);
        }
        catch (Exception e) { Log.ErrorThrottled("deconstruction receipt prefix", e.Message); }
    }

    static void AfterProcess(Deconstructor __instance, Attempt? __state)
    {
        if (__state == null) return;
        try
        {
            // ProcessItem removes the input from its inventory and queues removal. Item.Removed is
            // normally set later, so checking only Removed here would miss fast modded recipes.
            if (__state.Machine != __instance || !NativeConsumptionProof.ConfirmsRemoval(__state.RemovedBefore, __state.QueuedBefore,
                __state.Item.Removed, Entity.Spawner?.IsInRemoveQueue(__state.Item) == true)) return;
            foreach (var receipt in __state.Receipts)
                if (active.Contains(receipt)) { receipt.Machine = __instance; receipt.Confirmed = true; }
        }
        catch (Exception e) { Log.ErrorThrottled("deconstruction receipt postfix", e.Message); }
    }

    public static void Reset() => active.Clear();
    public static void Unpatch()
    {
        Reset();
        harmony?.UnpatchSelf();
        harmony = null;
        Patched = false;
    }
}
