using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using LlmAgents.Agents;

namespace LlmAgents;

public sealed partial class GameAdapter
{
    /// <summary>Uses the same crew orders and network broadcast as the player's context wheel.</summary>
    public string MarkItem(int botId, string reference, string mark, bool on = true, string? note = null, string? from = null)
    {
        if (ItemMarkRules.Validate(reference, mark, on, note, from) is { } invalid) return "error: " + invalid;
        if (Find(botId) is not { Removed: false, IsDead: false } bot) return "error: character gone";
        HashSet<Item>? scope = null;
        if (from != null)
        {
            var source = ItemsAt(bot, from);
            if (source == null) return "error: no current body or container at that exact source; no mark changed";
            scope = source.Value.Items.ToHashSet();
        }
        if (reference == "*")
        {
            var changed = new HashSet<Item>();
            var attempted = new HashSet<Item>();
            var skipped = new List<string>();
            foreach (var candidate in scope!.OrderBy(item => item.ID))
            {
                if (!attempted.Add(candidate)) continue;
                var result = MarkOne(bot, candidate, mark, on, note, scope, out var affected);
                if (result.StartsWith("ok", StringComparison.Ordinal))
                {
                    changed.UnionWith(affected);
                    attempted.UnionWith(affected);
                }
                else skipped.Add("#" + candidate.ID + ": " + result);
            }
            return (changed.Count > 0 ? $"ok, {(on ? "set" : "removed")} {mark} markers on {changed.Count} item(s): "
                + string.Join(", ", changed.OrderBy(item => item.ID).Select(item => "#" + item.ID))
                : "error: no selected item could be marked") + $"; scope from='{from}'"
                + (skipped.Count > 0 ? "; skipped: " + string.Join("; ", skipped) : "");
        }
        if (ExactObject(reference) is not Item item || !InInventoryScope(bot, item))
            return "error: no current item at that exact #ID in your observable area";
        if (scope != null && !scope.Contains(item)) return "error: the exact item is outside the requested body/container scope; no mark changed";
        return MarkOne(bot, item, mark, on, note, scope, out _);
    }

    string MarkOne(Character bot, Item item, string mark, bool on, string? note, HashSet<Item>? scope, out Item[] affected, bool apply = true)
    {
        affected = mark == "deconstruct" ? item.GetStackedItems().Where(i => !i.Removed).Distinct().ToArray() : new[] { item };
        if (affected.Length == 0) affected = new[] { item };
        if (item.Removed || !InInventoryScope(bot, item)) return "error: the selected item is no longer observable";
        var crew = GameMain.GameSession?.CrewManager;
        if (crew == null) return "error: crew session unavailable; no mark changed";
        if (item.ParentInventory != null && InventoryAccessProblem(bot, item.ParentInventory, requireRange: false) is { } access)
            return "error: " + access;
        if (on && ItemQueries.IsIgnored(item)) return "error: item or its parent is ordered ignored; existing player restriction stays";

        if (!DestructiveSelectionRules.StackWithinScope(affected, scope))
            return "error: the native stack also contains items outside the requested source; split the stack before marking it";
        if (on && mark == "deconstruct" && affected.Any(i => !i.AllowDeconstruct || !i.Prefab.AllowDeconstruct || !i.Prefab.DeconstructItems.Any()))
            return "error: the selected stack contains an item that cannot be deconstructed; no mark changed";
        if (on && mark == "deconstruct" && affected.Any(member => member.GetComponents<Barotrauma.Items.Components.ItemContainer>()
                .Any(container => container.Inventory.FindAllItems(recursive: true).Any(inside => !inside.Removed && (scope == null || !scope.Contains(inside))))))
            return "error: a selected container contains items outside the explicit scrap selection; remove its contents before marking it for deconstruction";
        if (on && mark == "deconstruct" && affected.Any(i => ItemQueries.IsIgnored(i)))
            return "error: a stack member is ordered ignored; no mark changed";

        var identifier = mark == "attention" ? on ? ContextMarks.MarkId : ContextMarks.UnmarkId
            : on ? Tags.DeconstructThis : Tags.DontDeconstructThis;
        if (!OrderPrefab.Prefabs.TryGet(identifier, out var prefab)) return $"error: marker order '{identifier}' is not loaded";
        if (!apply) return "ok, exact selected stack is safe to mark";
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
