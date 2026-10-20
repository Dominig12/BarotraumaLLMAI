using System.Text.Json.Nodes;

namespace LlmAgents.Agents;

public static partial class Tools
{
    static void AddPlanVersionArguments(JsonObject schema)
    {
        schema["properties"]!["expected_revision"] = new JsonObject
        {
            ["type"] = "integer", ["minimum"] = 1,
            ["description"] = "Optional revision from inspect_plan; reject stale edits before any mutation. All edits in one reply use the same original revision.",
        };
        schema["properties"]!["expected_plan_id"] = new JsonObject
        {
            ["type"] = "string", ["description"] = "Optional exact plan_id from inspect_plan; protects against applying old node IDs to a replacement plan.",
        };
    }

    static ToolDef EditPlanTool()
    {
        var schema = Schema();
        var operation = Schema(
            Arg("op", "Edit operation. replace/remove target a node; insert_before/insert_after target a sibling node; append/prepend target a sequence ID (root s0 or a branch sequence). For an active sequence, prepend inserts before its current/pending work, preserving completed steps.", en: new[] { "replace", "remove", "insert_before", "insert_after", "append", "prepend" }),
            Arg("node", "Exact node or sequence ID from inspect_plan, including s0 for the root sequence."));
        var nodes = PlanArgs();
        operation["properties"]!["steps"] = nodes["properties"]!["steps"]!.DeepClone();
        operation["properties"]!["steps"]!["description"] = "Canonical plan nodes. Required except for remove; remove must omit steps. Empty replace removes the node. Insert/append/prepend need at least one step.";
        schema["properties"]!["operations"] = new JsonObject
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 16, ["items"] = operation,
            ["description"] = "Edits are validated as one transaction in order; if any operation fails, the plan and active task stay unchanged. New nodes get fresh IDs; existing untouched IDs remain stable.",
        };
        schema["required"]!.AsArray().Add("operations");
        schema["$defs"] = nodes["$defs"]!.DeepClone();
        AddPlanVersionArguments(schema);
        return T("edit_plan", "Atomically edit the active plan without repeating completed work. Supports replace/remove, insertion before/after a node, append/prepend to a sequence, conditions and bounded loops. Changing a running step/ancestor cancels that step's native task before executing its replacement; completed effects are not undone. Inspect first and include expected_plan_id/expected_revision. Do not combine with a new plan or cancel_plan.",
            schema, ToolKind.Plan, ToolGroup.Plans, _ => "error: edit_plan is applied with the whole reply and cannot be nested in a plan");
    }

    static ToolDef AppendPlanTool()
    {
        var schema = PlanArgs();
        schema["properties"]!["steps"]!["minItems"] = 1;
        schema["properties"]!["node"] = new JsonObject
        {
            ["type"] = "string", ["description"] = "Sequence ID to extend, default s0 (root). Copy branch sequence IDs from inspect_plan.",
        };
        AddPlanVersionArguments(schema);
        return T("append_plan", "Append canonical steps to the end of a running plan or selected sequence, retaining progress and the active task. Requires an existing plan; use plan to create one. This is shorthand for edit_plan op=append.",
            schema, ToolKind.Plan, ToolGroup.Plans, _ => "error: append_plan is applied with the whole reply and cannot be nested in a plan");
    }

    static ToolDef CancelPlanTool()
    {
        var schema = Schema(Arg("reason", "Optional brief reason for cancellation, recorded in the bot's results.", false),
            Arg("clear_goal", "true also abandons the current goal; false (default) retains it. Cancellation never claims successful completion.", false, "boolean"));
        AddPlanVersionArguments(schema);
        return T("cancel_plan", "Cancel remaining steps and the native task/temporary travel awaited by this plan. Does not undo completed crafting/transfers or dismiss persistent orders. Player-issued orders remain protected; use the normal order tools for persistent orders. Optional clear_goal abandons the old goal. To switch jobs immediately, combine one cancel_plan with new action calls OR one new plan; the whole replacement is validated before stopping anything. Cannot be combined with edit/update/append/keep_plan. When replacing, omit inspect_plan too: inspect first in a separate turn. Cancellation alone starts no new job.",
            schema, ToolKind.Plan, ToolGroup.Plans, c => c.Mgr.CancelPlan(c.Agent, c.Str("reason"), c.Bool("clear_goal") ?? false,
                c.A["expected_revision"] == null ? null : c.Int("expected_revision", 0), c.Str("expected_plan_id")));
    }
}
