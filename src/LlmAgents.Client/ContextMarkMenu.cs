using System;
using System.Linq;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

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
        var draw = AccessTools.Method(typeof(Inventory), nameof(Inventory.DrawSlot),
            new[] { typeof(SpriteBatch), typeof(Inventory), typeof(VisualSlot), typeof(Item), typeof(int), typeof(bool), typeof(InvSlotType) });
        if (draw != null) harmony.Patch(draw, postfix: new HarmonyMethod(typeof(ContextMarkMenu), nameof(DrawSlotPostfix)));
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
            if (order?.IsDeconstructOrder == true && order.TargetEntity is Item deconstructItem)
            {
                // Match the server: native AddOrder otherwise drops rapid marker toggles as duplicates.
                var stack = deconstructItem.GetStackedItems().ToHashSet();
                stack.Add(deconstructItem);
                __instance.ActiveOrders.RemoveAll(o => o.Order.IsDeconstructOrder && o.Order.TargetEntity is Item target && stack.Contains(target));
                fadeOutTime = order.FadeOutTime;
                return true;
            }
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

    /// <summary>
    /// The AI mark on the item in an inventory slot, like the game's "ignore" and "deconstruct" icons: the order's
    /// symbol at the slot's lower left corner (the game's icons sit at the right; the "stolen" one also uses the left,
    /// so the mark goes above it then). Hovering shows a tooltip, and the middle mouse button there removes the mark.
    /// </summary>
    static void DrawSlotPostfix(SpriteBatch spriteBatch, Inventory inventory, VisualSlot slot, Item item, bool drawItem)
    {
        try
        {
            if (item == null || !drawItem || slot == null || !IsMarked(item)) return;
            if (!OrderPrefab.Prefabs.TryGet(MarkId, out var prefab) || prefab.SymbolSprite == null) return;
            var icon = prefab.SymbolSprite;

            var rect = slot.Rect;
            rect.Location += slot.DrawOffset.ToPoint();
            float size = 25f * GUI.Scale;
            float y = rect.Bottom - size * 1.2f;
            if (item.Illegitimate) y -= size;   // the game's "stolen" icon is there
            var pos = new Vector2(rect.X + size * 0.2f, y);

            var color = prefab.Color;
            if (Vector2.Distance(PlayerInput.MousePosition, pos) < size / 2f)
            {
                Inventory.slotIconTooltip = TextManager.Get("tooltip.llmmarked").Fallback("Marked for the AI crew");
                Inventory.availableContextualOrder = (item, UnmarkId);
                color = Color.Lerp(color, Color.White, 0.5f);
            }
            icon.Draw(spriteBatch, pos, color, 0f, size / icon.size.X, SpriteEffects.None);
        }
        catch { /* drawing must never break the inventory */ }
    }
}
