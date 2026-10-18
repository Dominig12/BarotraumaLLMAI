using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Barotrauma;
using Barotrauma.Items.Components;

namespace LlmAgents;

public sealed partial class GameAdapter
{
    internal static IEnumerable<ItemComponent> NativeInteractionComponents(Item item, string key)
    {
        var input = key == "select" ? InputType.Select : InputType.Use;
        // Ordinary Pickable/ Holdable pickup has its own complete take/equip tasks, including timed
        // pickup and carrying. Controller.Pick is a button/switch operation and uses the native key.
        return item.Components.Where(component => component.CanBePicked && component is not Pickable && component.PickKey == input
            || component.CanBeSelected && component.SelectKey == input);
    }

    internal static string? NativeInteractionProblem(Character bot, Item item, string key, bool requireRange)
    {
        if (item.Removed) return "target item was removed";
        if (!bot.CanInteract) return "actor cannot currently interact";
        if (ItemQueries.IsIgnored(item)) return "target is ordered ignored";
        if (!item.IsInteractable(bot)) return "target is not interactable by this character";
        var components = NativeInteractionComponents(item, key).ToArray();
        if (components.Length == 0) return "no component supports the requested native interaction key";
        if (!components.Any(component => component.HasRequiredItems(bot, false) && component.HasAccess(bot)))
            return "required items or access are missing for the native interaction";
        if (item.ParentInventory != null && InventoryAccessProblem(bot, item.ParentInventory, requireRange) is { } access)
            return access;
        if (requireRange && !bot.CanInteractWith(item)) return "not in interaction range of target";
        return null;
    }

    /// <summary>
    /// One native Pick/Select interaction, including normal callbacks and requirements. This is not
    /// Item.Use and does not fabricate, fire held weapons, edit machine properties or invent mod APIs.
    /// </summary>
    public string InteractObject(int botId, string target, string key = "select")
    {
        if (Find(botId) is not { Removed: false } bot || !Ai(bot, out var ai)) return "error: character gone";
        if (key is not ("select" or "use")) return "error: key must be select or use";
        if (ExactObject(target) is not Item item) return "error: native interaction target must be an exact live #item ID";
        if (NativeInteractionProblem(bot, item, key, requireRange: false) is { } problem) return "error: " + problem;
        if (ClaimedByOthers(bot).Contains(item)) return "error: another bot is already interacting with this exact item";
        if (!HasAuthority(bot) && item.GetComponents<Controller>().Any(controller => controller.User is { Removed: false } user && user != bot))
            return "error: another character is using this controller; their station remains assigned";
        return StartTask(bot, new InteractObjectTask(bot, ai, item, key, HasAuthority(bot)));
    }

    internal static string NativeInteractionState(Character bot, Item item)
    {
        var state = new JsonObject
        {
            ["item_removed"] = item.Removed,
            ["selected_item"] = bot.SelectedItem != null ? ObjectReference(bot.SelectedItem) : null,
            ["selected_secondary_item"] = bot.SelectedSecondaryItem != null ? ObjectReference(bot.SelectedSecondaryItem) : null,
        };
        if (!item.Removed)
        {
            state["condition"] = item.Condition;
            state["parent_inventory"] = item.ParentInventory != null ? InventoryOwnerRef(item.ParentInventory.Owner) : null;
            state["doors"] = new JsonArray(item.GetComponents<Door>().Select(door => (JsonNode?)new JsonObject
            {
                ["is_open"] = door.IsOpen, ["should_be_open"] = door.ShouldBeOpen,
                ["open_state"] = door.OpenState, ["jammed"] = door.IsJammed, ["stuck"] = door.IsStuck,
            }).ToArray());
            state["controllers"] = new JsonArray(item.GetComponents<Controller>().Select(controller => (JsonNode?)new JsonObject
            {
                ["state"] = controller.State, ["toggle"] = controller.IsToggle,
                ["user"] = controller.User != null ? ObjectReference(controller.User) : null,
                ["active"] = controller.IsActive,
                ["require_power"] = controller.RequirePower, ["output"] = controller.Output, ["false_output"] = controller.FalseOutput,
            }).ToArray());
        }
        return state.ToJsonString(MachineJson);
    }
}
