using System;
using System.Collections.Generic;
using System.Linq;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

public sealed partial class AgentManager
{
    readonly ObjectToolRegistry objectTools = new();

    internal ToolDef? ResolveTool(BotAgent agent, string name) => Tools.Def(name) ?? objectTools.Resolve(agent.Id, name);
    internal ToolCall CanonicalCall(BotAgent agent, ToolCall call) => objectTools.CanonicalCall(agent.Id, call);
    internal bool IsToolAllowed(BotAgent agent, string name)
    {
        var canonical = Tools.Def(name) != null ? name : objectTools.CanonicalName(agent.Id, name);
        return canonical != null && (Settings.Bots.AllowedTools.Count == 0 || Settings.Bots.AllowedTools.Contains(canonical, StringComparer.OrdinalIgnoreCase));
    }
    bool CanonicalToolAllowed(BotAgent agent, string name) => Tools.Def(name) is { } definition &&
        (Settings.Bots.AllowedTools.Count == 0 || Settings.Bots.AllowedTools.Contains(name, StringComparer.OrdinalIgnoreCase)) &&
        (agent.AiControl || !definition.ControlOnly) && (MemoryOn || definition.Group != ToolGroup.Memory || name == "remember");

    internal IReadOnlyCollection<string> PlanToolNames(BotAgent agent) => Tools.For(Settings.Bots.AllowedTools, agent.AiControl, MemoryOn).Select(spec => spec.Name)
        .Concat(objectTools.Names(agent.Id).Where(name => IsToolAllowed(agent, name))).ToHashSet(StringComparer.Ordinal);

    internal IReadOnlyList<ToolSpec> ToolsFor(BotAgent agent)
    {
        objectTools.Refresh(agent.Id, game.GetObjects(agent.Id));
        var canonical = Tools.For(Settings.Bots.AllowedTools, agent.AiControl, MemoryOn);
        return canonical.Concat(objectTools.Offered(agent, ObjectToolRegistry.NativeToolLimit - canonical.Count,
            name => objectTools.CanonicalName(agent.Id, name) is { } original && CanonicalToolAllowed(agent, original))).ToArray();
    }

    internal string DiscoverObjectTools(BotAgent agent, string? target, int offset, int limit, bool includeSchemas = false)
    {
        objectTools.Refresh(agent.Id, game.GetObjects(agent.Id));
        return objectTools.Discover(agent, target, offset, limit, name => CanonicalToolAllowed(agent, name), includeSchemas);
    }

    internal string ToolHelp(BotAgent agent, string? name) => name != null && objectTools.Resolve(agent.Id, name) is { } generated
        ? $"{generated.Name}: {generated.Description}\nArguments JSON Schema: " + generated.Schema.ToJsonString(new System.Text.Json.JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        : Tools.ToolHelp(name);

    internal string ExecuteObjectTool(ToolContext context, string name)
    {
        // Re-read the live world for EVERY step, including a handle queued in a long-running plan.
        objectTools.Refresh(context.Id, game.GetObjects(context.Id));
        if (objectTools.LiveError(context.Id, name) is { } error) return "error: " + error;
        var canonical = CanonicalCall(context.Agent, context.Call);
        return Tools.Execute(canonical, context.Agent, context.Game, this);
    }

    bool PassiveTool(BotAgent agent, string name) => ResolveTool(agent, name)?.Kind is ToolKind.Talk or ToolKind.Lookup or ToolKind.Memory
        || name is "keep_plan" or "update_plan" or "edit_plan" or "append_plan" or "cancel_plan";

    bool MissingAllRequired(BotAgent agent, ToolCall call)
    {
        if (ResolveTool(agent, call.Name)?.Schema["required"] is not System.Text.Json.Nodes.JsonArray required || required.Count == 0) return false;
        return required.All(node => node?.GetValue<string>() is { } key &&
            (call.Arguments[key] == null || call.Arguments[key] is System.Text.Json.Nodes.JsonValue value &&
                value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text)));
    }
}
