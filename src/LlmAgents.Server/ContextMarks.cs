using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using HarmonyLib;

namespace LlmAgents;

/// <summary>
/// "Mark for the AI" (Content/Orders.xml: llmmark / llmunmark): a player middle-clicks an item or a machine and picks the
/// order from the game's context wheel. Like the vanilla "ignore" order it goes to the whole crew, so the server runs it
/// through CrewManager.AddOrder; a prefix there keeps the mark here (who, when) and keeps the icon on the item until the
/// mark is taken off. Bots see the marks in their observation ('marks'), together with the vanilla marks
/// ("mark for deconstruction", "ignore"), and tools select them with the explicit mark argument.
/// </summary>
internal static class ContextMarks
{
    public static readonly Identifier MarkId = "llmmark".ToIdentifier();
    public static readonly Identifier UnmarkId = "llmunmark".ToIdentifier();

    public sealed record Mark(Item Item, string By, double At, string? Note = null);

    static readonly Dictionary<ushort, Mark> marks = new();
    static readonly object gate = new();

    /// <summary>A player marked (true) or unmarked (false) an item. Set by the plugin to the game adapter.</summary>
    public static Action<Item, string, bool>? Changed;

    static Harmony? harmony;
    public static bool Patched { get; private set; }

    public static void Patch()
    {
        if (Patched) return;
        harmony ??= new Harmony("llmagents.contextmarks");
        var add = AccessTools.Method(typeof(CrewManager), nameof(CrewManager.AddOrder), new[] { typeof(Order), typeof(float?) })
                  ?? throw new MissingMethodException("CrewManager.AddOrder(Order, float?) not found");
        harmony.Patch(add, prefix: new HarmonyMethod(typeof(ContextMarks), nameof(AddOrderPrefix)));
        Patched = true;
    }

    public static void Unpatch()
    {
        if (!Patched) return;
        harmony?.UnpatchSelf();
        Patched = false;
    }

    /// <summary>Return false = skip the vanilla method.</summary>
    static bool AddOrderPrefix(CrewManager __instance, Order order, ref float? fadeOutTime)
    {
        try
        {
            if (order?.IsDeconstructOrder == true && order.TargetEntity is Item deconstructItem)
            {
                // Vanilla returns early for a matching active order, before changing DeconstructItems.
                // A quick set/remove/set, or a stack whose members changed, must still apply the new state.
                var stack = deconstructItem.GetStackedItems().ToHashSet();
                stack.Add(deconstructItem);
                __instance.ActiveOrders.RemoveAll(o => o.Order.IsDeconstructOrder && o.Order.TargetEntity is Item target && stack.Contains(target));
                fadeOutTime = order.FadeOutTime;
                return true;
            }
            if (order == null || (order.Identifier != MarkId && order.Identifier != UnmarkId)) return true;
            if (order.TargetEntity is not Item item) return false;
            var by = order.OrderGiver?.Name ?? "a player";
            // the old icon goes either way: a mark replaces it, an unmark removes it
            __instance.ActiveOrders.RemoveAll(o => o.Order.Identifier == MarkId && o.Order.TargetEntity == item);
            if (order.Identifier == UnmarkId)
            {
                Remove(item);
                Changed?.Invoke(item, by, false);
                return false;
            }
            lock (gate)
            {
                // A repeat mark refreshes its author/time but keeps the explanation unless the bot supplies a new one.
                var note = marks.TryGetValue(item.ID, out var previous) && previous.Item == item ? previous.Note : null;
                marks[item.ID] = new Mark(item, by, Timing.TotalTime, note);
            }
            Changed?.Invoke(item, by, true);
            fadeOutTime = null;   // the icon stays until the mark is taken off
            return true;
        }
        catch (Exception e)
        {
            Log.Warn("context mark: " + e.Message);
            return true;
        }
    }

    public static void Remove(Item item)
    {
        lock (gate) marks.Remove(item.ID);
        try
        {
            GameMain.GameSession?.CrewManager?.ActiveOrders.RemoveAll(o => o.Order.Identifier == MarkId && o.Order.TargetEntity == item);
        }
        catch { /* no session */ }
    }

    public static bool IsMarked(Item item) => All().Any(m => m.Item == item);

    /// <summary>Optional bot explanation; the visual order remains the same native crew marker.</summary>
    public static void SetNote(Item item, string? note)
    {
        lock (gate)
            if (marks.TryGetValue(item.ID, out var mark) && mark.Item == item)
                marks[item.ID] = mark with { Note = note };
    }

    /// <summary>Live marks, newest first (removed items are dropped).</summary>
    public static List<Mark> All()
    {
        lock (gate)
        {
            // marks saved with the campaign come back as active orders when the next round loads
            try
            {
                var active = GameMain.GameSession?.CrewManager?.ActiveOrders;
                if (active != null)
                    foreach (var o in active)
                        if (o.Order.Identifier == MarkId && o.Order.TargetEntity is Item i && !i.Removed && !marks.ContainsKey(i.ID))
                            marks[i.ID] = new Mark(i, o.Order.OrderGiver?.Name ?? "a player", Timing.TotalTime);
            }
            catch { /* no session */ }
            foreach (var dead in marks.Where(kv => kv.Value.Item.Removed || Entity.FindEntityByID(kv.Key) != kv.Value.Item).Select(kv => kv.Key).ToList())
                marks.Remove(dead);
            return marks.Values.OrderByDescending(m => m.At).ToList();
        }
    }

    public static void Reset()
    {
        lock (gate) marks.Clear();
    }
}
