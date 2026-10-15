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
/// </summary>
internal static class ControlOrder
{
    public static readonly Identifier Id = "llmcontrol".ToIdentifier();

    /// <summary>(bot, order giver, kind). Set by the plugin to the current game adapter.</summary>
    public static Action<Character, Character?, Kind>? Handler;

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
        harmony.Patch(setOrder, prefix: new HarmonyMethod(typeof(ControlOrder), nameof(SetOrderPrefix)));

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

    static bool SendPrefix(OrderChatMessage message)
    {
        try { return !IsControlOrder(message?.Order); }
        catch { return true; }
    }
}
