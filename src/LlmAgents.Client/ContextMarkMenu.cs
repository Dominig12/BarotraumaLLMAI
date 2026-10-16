using System;
using System.Linq;
using Barotrauma;
using HarmonyLib;

namespace LlmAgents.Client;

/// <summary>
/// Client side of "Mark for the AI" (middle mouse button on an item or a machine): the game shows a context order only
/// for items its 'targetitems' tags match, so the AI mark is offered for every item here, and only the fitting one
/// of "mark" / "unmark". The mark icon stays on the item until the mark is taken off (the server keeps the marks;
/// without this client part the order still works for the tagged items, only both entries are shown).
/// </summary>
internal static class ContextMarkMenu
{
    static readonly Identifier MarkId = "llmmark".ToIdentifier();
    static readonly Identifier UnmarkId = "llmunmark".ToIdentifier();
    static Harmony? harmony;

    public static void Patch()
    {
        if (harmony != null) return;
        harmony = new Harmony("llmagents.contextmarkmenu");
        var match = AccessTools.Method(typeof(OrderPrefab), nameof(OrderPrefab.TargetItemsMatchItem), new[] { typeof(Item), typeof(Identifier) });
        if (match != null) harmony.Patch(match, postfix: new HarmonyMethod(typeof(ContextMarkMenu), nameof(MatchPostfix)));
        var add = AccessTools.Method(typeof(CrewManager), nameof(CrewManager.AddOrder), new[] { typeof(Order), typeof(float?) });
        if (add != null) harmony.Patch(add, prefix: new HarmonyMethod(typeof(ContextMarkMenu), nameof(AddOrderPrefix)));
    }

    public static void Unpatch()
    {
        harmony?.UnpatchSelf();
        harmony = null;
    }

    static bool IsMarked(Item item) =>
        GameMain.GameSession?.CrewManager?.ActiveOrders.Any(o => o.Order.Identifier == MarkId && o.Order.TargetEntity == item) == true;

    static void MatchPostfix(OrderPrefab __instance, Item item, ref bool __result)
    {
        try
        {
            if (item == null) return;
            if (__instance.Identifier == MarkId) __result = !IsMarked(item);
            else if (__instance.Identifier == UnmarkId) __result = IsMarked(item);
        }
        catch { /* keep the game's answer */ }
    }

    static bool AddOrderPrefix(CrewManager __instance, Order order, ref float? fadeOutTime)
    {
        try
        {
            if (order == null || (order.Identifier != MarkId && order.Identifier != UnmarkId)) return true;
            if (order.TargetEntity is not Item item) return false;
            __instance.ActiveOrders.RemoveAll(o => o.Order.Identifier == MarkId && o.Order.TargetEntity == item);
            if (order.Identifier == UnmarkId) return false;
            fadeOutTime = null;
            return true;
        }
        catch
        {
            return true;
        }
    }
}
