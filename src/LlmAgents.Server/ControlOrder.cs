using System;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace LlmAgents;

/// <summary>
/// The mod's "Full AI control" order (Content/Orders.xml, identifier <see cref="Id"/>). Players give it from the
/// command menu like any other order. The game has no objective for it, so it never reaches the vanilla order
/// machinery: a prefix on Character.SetOrder hands it to the mod instead, and the order chat message is not sent
/// to clients (their vanilla code would report "failed to create an objective" and pop up the console).
///
/// Giving the order to a bot that is not under AI control grants control; giving it again takes control back.
/// Dismissing all of a bot's orders ("dismiss") also takes control back.
///
/// The same patch also reports every other order a human player gives a bot through the command menu
/// (<see cref="MenuHandler"/>), so the mod can cancel the bot's own plan and task: the player's order always wins.
/// Orders the mod itself issues are not reported (<see cref="Internal"/>).
/// </summary>
internal static class ControlOrder
{
    public static readonly Identifier Id = "llmcontrol".ToIdentifier();

    /// <summary>(bot, order giver, kind). Set by the plugin to the current game adapter.</summary>
    public static Action<Character, Character?, Kind>? Handler;

    /// <summary>(bot, order) for ordinary orders given by human players. Set by the plugin to the current game adapter.</summary>
    public static Action<Character, Order, bool>? MenuHandler;

    /// <summary>True while the mod itself calls Character.SetOrder (its own orders are not "player menu orders").</summary>
    [ThreadStatic] public static bool Internal;

    public enum Kind { Toggle, Revoke, DismissAll }

    static Harmony? harmony;
    public static bool Patched { get; private set; }

    public static void Patch()
    {
        if (Patched) return;
        harmony ??= new Harmony("llmagents.controlorder");
        var setOrder = AccessTools.Method(typeof(Character), nameof(Character.SetOrder),
            new[] { typeof(Order), typeof(bool), typeof(bool), typeof(bool) })
            ?? throw new MissingMethodException("Character.SetOrder(Order, bool, bool, bool) not found");
        harmony.Patch(setOrder, prefix: new HarmonyMethod(typeof(ControlOrder), nameof(SetOrderPrefix)),
            postfix: new HarmonyMethod(typeof(ControlOrder), nameof(SetOrderPostfix)));

        var send = AccessTools.Method(typeof(GameServer), nameof(GameServer.SendOrderChatMessage), new[] { typeof(OrderChatMessage) });
        if (send != null) harmony.Patch(send, prefix: new HarmonyMethod(typeof(ControlOrder), nameof(SendPrefix)));
        else Log.Warn("GameServer.SendOrderChatMessage not found: clients may see an error when the AI control order is given");
        Patched = true;
    }

    public static void Unpatch()
    {
        if (!Patched) return;
        harmony?.UnpatchSelf();
        Patched = false;
    }

    public static bool IsControlOrder(Order? o) =>
        o != null && (o.Identifier == Id || (o.IsDismissal && IsControlOption(o.Option)));

    static bool IsControlOption(Identifier option) =>
        !option.IsEmpty && option.Value.Split('.')[0].Equals(Id.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Return false = skip the vanilla method.</summary>
    static bool SetOrderPrefix(Character __instance, Order order)
    {
        try
        {
            if (order == null) return true;
            if (order.Identifier == Id)
            {
                Handler?.Invoke(__instance, order.OrderGiver, Kind.Toggle);
                return false;
            }
            if (order.IsDismissal)
            {
                if (IsControlOption(order.Option))
                {
                    Handler?.Invoke(__instance, order.OrderGiver, Kind.Revoke);
                    return false;
                }
                if (order.Option.IsEmpty && order.OrderGiver is { IsPlayer: true })
                    Handler?.Invoke(__instance, order.OrderGiver, Kind.DismissAll); // and let vanilla clear the orders
            }
            return true;
        }
        catch (Exception e)
        {
            Log.Warn("control order: " + e.Message);
            return order?.Identifier != Id;
        }
    }

    /// <summary>After vanilla applied an order: tell the mod if a human player gave it (not the mod, not the control order).</summary>
    static void SetOrderPostfix(Character __instance, Order order, bool isNewOrder)
    {
        try
        {
            if (Internal || order == null || __instance == null || IsControlOrder(order)) return;
            if (order.OrderGiver is not { IsPlayer: true } giver || giver == __instance) return;
            MenuHandler?.Invoke(__instance, order, isNewOrder);
        }
        catch (Exception e)
        {
            Log.Warn("menu order hook: " + e.Message);
        }
    }

    static bool SendPrefix(OrderChatMessage message)
    {
        try { return !IsControlOrder(message?.Order); }
        catch { return true; }
    }
}
