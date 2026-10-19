using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmAgents.Agents;

public static partial class Tools
{
    internal static readonly JsonSerializerOptions LoadoutJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    static ToolDef InventoryLoadoutTool()
    {
        var schema = Schema(
            Arg("enabled", "Enable automatic carried-item rearrangement and periodic model review; default true. false pauses both while retaining these rules.", false, "boolean"),
            Arg("check_interval_seconds", "Automatic local repair interval, 5-300 seconds; default 15. No model call is needed for these passes.", false, "integer"),
            Arg("review_interval_seconds", "Model review interval, 30-3600 seconds; default 120. Reviews wait for quiet periods, ongoing tasks and existing plans.", false, "integer"));
        schema["properties"]!["check_interval_seconds"]!["minimum"] = 5;
        schema["properties"]!["check_interval_seconds"]!["maximum"] = 300;
        schema["properties"]!["review_interval_seconds"]!["minimum"] = 30;
        schema["properties"]!["review_interval_seconds"]!["maximum"] = 3600;
        var step = Schema(Arg("slot", "Zero-based parent slot holding the carried container.", type: "integer"),
            Arg("container_index", "Zero-based ItemContainer component of that item; default 0.", false, "integer"));
        step["properties"]!["slot"]!["minimum"] = 0;
        step["properties"]!["slot"]!["maximum"] = 255;
        step["properties"]!["container_index"]!["minimum"] = 0;
        step["properties"]!["container_index"]!["maximum"] = 31;
        var rule = Schema(Arg("slot", "Zero-based destination slot, copied from inspect_inventory.", type: "integer"),
            Arg("count", "Desired individual stack members in this slot, 1-32, default 1. Use 1 for prefab=null. Native stack limits still apply.", false, "integer"));
        rule["properties"]!["slot"]!["minimum"] = 0;
        rule["properties"]!["slot"]!["maximum"] = 255;
        rule["properties"]!["count"]!["minimum"] = 1;
        rule["properties"]!["count"]!["maximum"] = 32;
        rule["properties"]!["prefab"] = new JsonObject
        {
            ["description"] = "Exact persistent prefab identifier from inspect_inventory (including modded identifiers). null explicitly keeps the slot empty. No #IDs, localized names, categories or wildcard selectors.",
            ["oneOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "null" }),
        };
        rule["required"]!.AsArray().Add("prefab");
        rule["properties"]!["path"] = new JsonObject
        {
            ["type"] = "array", ["maxItems"] = 4, ["items"] = step,
            ["description"] = "Omit or [] for your main character inventory. For nested carried containers, follow parent slots from your main inventory, e.g. [{slot:7,container_index:0}]. Inspect numeric indices; do not guess. Paths survive round ID changes.",
        };
        schema["properties"]!["slots"] = new JsonObject
        {
            ["type"] = "array", ["maxItems"] = 64, ["items"] = rule,
            ["description"] = "Complete replacement of managed slot rules, up to 64. Unlisted slots stay unmanaged. [] clears the layout. Copy all retained rules when changing one slot.",
        };
        schema["required"]!.AsArray().Add("slots");
        return T("set_inventory_loadout",
            "Set your persistent desired inventory as explicit slot -> prefab/count rules, including carried containers. Inspect inventory first. Replaces the entire previous layout atomically; it schedules safe automatic rearrangement of carried items rather than spawning or taking supplies. Missing items remain missing until you explicitly plan take_item/craft/refill. Periodic model review can revise this layout. Does not interrupt active work. Rules persist between rounds when memory is enabled.",
            schema, ToolKind.Order, ToolGroup.Inventory, c =>
            {
                var profile = JsonSerializer.Deserialize<InventoryLoadout>(c.A.ToJsonString(), LoadoutJson);
                if (InventoryLoadoutRules.Validate(profile) is { } error) return "error: " + error;
                return c.Mgr.SetInventoryLoadout(c.Agent, profile!);
            });
    }
}
