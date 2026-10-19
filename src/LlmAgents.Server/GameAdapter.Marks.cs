using System;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using LlmAgents.Agents;

namespace LlmAgents;

public sealed partial class GameAdapter
{
    /// <summary>Uses the same crew orders and network broadcast as the player's context wheel.</summary>
    public string MarkItem(int botId, string reference, string mark, bool on = true, string? note = null)
    {
        if (ItemMarkRules.Validate(reference, mark, on, note) is { } invalid) return "error: " + invalid;
        if (Find(botId) is not { Removed: false, IsDead: false } bot) return "error: character gone";
        if (ExactObject(reference) is not Item item || !InInventoryScope(bot, item))
            return "error: no current item at that exact #ID in your observable area";
        var crew = GameMain.GameSession?.CrewManager;
        if (crew == null) return "error: crew session unavailable; no mark changed";
        if (item.ParentInventory != null && InventoryAccessProblem(bot, item.ParentInventory, requireRange: false) is { } access)
            return "error: " + access;
        if (on && ItemQueries.IsIgnored(item)) return "error: item or its parent is ordered ignored; existing player restriction stays";

        var affected = mark == "deconstruct" ? item.GetStackedItems().Where(i => !i.Removed).Distinct().ToArray() : new[] { item };
        if (affected.Length == 0) affected = new[] { item };
        if (on && mark == "deconstruct" && affected.Any(i => !i.AllowDeconstruct || !i.Prefab.AllowDeconstruct || !i.Prefab.DeconstructItems.Any()))
            return "error: the selected stack contains an item that cannot be deconstructed; no mark changed";
        if (on && mark == "deconstruct" && affected.Any(i => ItemQueries.IsIgnored(i)))
            return "error: a stack member is ordered ignored; no mark changed";

        var identifier = mark == "attention" ? on ? ContextMarks.MarkId : ContextMarks.UnmarkId
            : on ? Tags.DeconstructThis : Tags.DontDeconstructThis;
        if (!OrderPrefab.Prefabs.TryGet(identifier, out var prefab)) return $"error: marker order '{identifier}' is not loaded";
        var order = new Order(prefab, Identifier.Empty, item, null, orderGiver: bot);
        try { crew.AddOrder(order, fadeOutTime: mark == "attention" ? null : order.FadeOutTime); }
        catch (Exception e) { Log.Warn("item marker apply: " + e.Message); }
        if (mark == "attention" && on && note != null) ContextMarks.SetNote(item, note);
        bool applied = mark == "attention" ? ContextMarks.IsMarked(item) == on
                && crew.ActiveOrders.Any(active => active.Order.Identifier == ContextMarks.MarkId && active.Order.TargetEntity == item) == on
            : affected.All(i => Item.DeconstructItems.Contains(i) == on);
        if (!applied) return "error: the game did not confirm the requested marker state";
        // This is a world-state marker, not speech. Send a normal order packet directly so a missing
        // radio, speaking range or speech impediment cannot leave the clients' marker state stale.
        // A network failure after the verified mutation must not tell the LLM that no mark was applied.
        int syncFailures = 0;
        if (GameMain.Server is { } server)
            foreach (var client in server.ConnectedClients.ToArray())
                try { server.SendDirectChatMessage(new OrderChatMessage(order, null, bot), client); }
                catch (Exception e) { syncFailures++; Log.Warn("item marker client sync: " + e.Message); }
        return $"ok, {(on ? "set" : "removed")} {mark} marker on " + string.Join(", ", affected.Select(i => $"#{i.ID}"))
            + (mark == "deconstruct" ? on ? "; native stack marker only; deconstruction orders may now act on it" : "; deconstruction marker cleared"
                : "; crew attention marker state updated")
            + (on && note != null ? "; note: " + note : "")
            + (syncFailures > 0 ? $"; warning: server state applied, but {syncFailures} client marker update(s) failed" : "");
    }
}
