using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>A live instance, never a prefab alone. Identity includes the round and object lifetime, not just its reusable game ID.</summary>
public sealed record GameObjectInfo(string Reference, string Identity, string Name, string Identifier, string Kind,
    IReadOnlyList<string> Components, IReadOnlyList<ObjectToolBinding> Actions, string? Room = null);

/// <summary>A supported executor with some arguments fixed to this instance. Unknown component methods are never reflected or invoked.</summary>
public sealed record ObjectToolBinding(string Tool, JsonObject Arguments, string? Description = null);

/// <summary>
/// Complete instance catalog owned by one manager. Generated definitions are materialized only when discovered;
/// paging keeps native requests bounded while every object remains addressable. Game-thread use only.
/// </summary>
internal sealed class ObjectToolRegistry
{
    public const int NativeToolLimit = 128;
    public const int MaxBoundTools = 48;
    public const int MaxPageSize = 20;
    sealed record Entry(GameObjectInfo Object, ObjectToolBinding Binding, string Name, IReadOnlyDictionary<string, string> References);
    sealed record Bound(Entry Entry, ToolDef Definition);
    sealed class Catalog
    {
        public IReadOnlyList<GameObjectInfo>? Snapshot;
        public Dictionary<string, GameObjectInfo> Objects = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Bound> Known = new(StringComparer.Ordinal);
        public readonly LinkedList<string> Active = new();
    }
    readonly Dictionary<int, Catalog> bots = new();

    Catalog For(int id)
    {
        if (!bots.TryGetValue(id, out var catalog)) bots.Add(id, catalog = new Catalog());
        return catalog;
    }

    public void Clear() => bots.Clear();
    public void Remove(int botId) => bots.Remove(botId);

    public void Refresh(int botId, IReadOnlyList<GameObjectInfo> objects)
    {
        var catalog = For(botId);
        // Adapters may reuse an immutable snapshot until the world changes. Never retain game objects here.
        if (ReferenceEquals(catalog.Snapshot, objects)) return;
        var live = new Dictionary<string, GameObjectInfo>(StringComparer.Ordinal);
        foreach (var obj in objects)
        {
            if (string.IsNullOrWhiteSpace(obj.Reference) || string.IsNullOrWhiteSpace(obj.Identity)) continue;
            live[obj.Reference] = obj;
        }
        catalog.Objects = live;
        catalog.Snapshot = objects;
    }

    readonly Dictionary<string, JsonObject> partialSchemas = new(StringComparer.Ordinal);
    bool ValidBinding(ToolDef definition, JsonObject arguments)
    {
        if (!partialSchemas.TryGetValue(definition.Name, out var partial))
        {
            partial = (JsonObject)definition.Schema.DeepClone();
            partial["required"] = new JsonArray();
            partialSchemas.Add(definition.Name, partial);
        }
        return Tools.ValidateAgainstSchema(arguments, partial) == null;
    }

    IEnumerable<ObjectToolBinding> Actions(GameObjectInfo obj)
    {
        yield return new ObjectToolBinding("inspect_object", new JsonObject { ["target"] = obj.Reference });
        foreach (var action in obj.Actions)
            if (Tools.Def(action.Tool) is { Kind: ToolKind.Lookup or ToolKind.Task or ToolKind.Order } definition &&
                action.Tool is not ("object_tools" or "inspect_object") && ValidBinding(definition, action.Arguments)) yield return action;
    }

    static bool IsLive(Catalog catalog, Entry entry) => catalog.Objects.TryGetValue(entry.Object.Reference, out var obj) &&
        obj.Identity == entry.Object.Identity && (entry.Binding.Tool == "inspect_object" ||
            obj.Actions.Any(action => action.Tool == entry.Binding.Tool && JsonNode.DeepEquals(action.Arguments, entry.Binding.Arguments))) &&
        entry.References.All(pair => catalog.Objects.TryGetValue(pair.Key, out var dependency) && dependency.Identity == pair.Value);

    static IReadOnlyDictionary<string, string> BoundReferences(Catalog catalog, ObjectToolBinding binding)
    {
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in binding.Arguments)
        {
            if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
            // Only the formal game references are interpreted. Copied room labels may prefix their [hID].
            var reference = text.Trim();
            var start = reference.LastIndexOf(" [h", StringComparison.Ordinal);
            if (start >= 0) reference = reference.Substring(start + 1);
            var digits = reference.StartsWith("#", StringComparison.Ordinal) ? reference.Substring(1)
                : reference.Length > 3 && reference[0] == '[' && reference[^1] == ']' && "ches".Contains(reference[1])
                    ? reference.Substring(2, reference.Length - 3) : "";
            if (digits.Length == 0 || !digits.All(c => c is >= '0' and <= '9')) continue;
            references[reference] = catalog.Objects.TryGetValue(reference, out var obj) ? obj.Identity : "";
        }
        return references;
    }

    static string ToolName(GameObjectInfo obj, ObjectToolBinding binding, IReadOnlyDictionary<string, string> references)
    {
        var fixedArguments = new JsonObject();
        foreach (var p in binding.Arguments.OrderBy(p => p.Key, StringComparer.Ordinal)) fixedArguments[p.Key] = p.Value?.DeepClone();
        var input = obj.Identity + "\n" + obj.Reference + "\n" + binding.Tool + "\n" + fixedArguments.ToJsonString()
            + "\n" + string.Join("\n", references.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + pair.Value));
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        static string Safe(string value) => new(value.Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_').ToArray());
        var reference = Tools.Clip(Safe(obj.Reference), 12);
        var action = Tools.Clip(Safe(binding.Tool), 28);
        return $"obj_{reference}_{action}_{hash}"; // at most 62 chars; native-compatible ASCII identifier
    }

    static ToolDef Materialize(Entry entry)
    {
        var canonical = Tools.Def(entry.Binding.Tool)!;
        var schema = (JsonObject)canonical.Schema.DeepClone();
        foreach (var argument in entry.Binding.Arguments)
        {
            (schema["properties"] as JsonObject)?.Remove(argument.Key);
            if (schema["required"] is JsonArray required)
                for (var i = required.Count - 1; i >= 0; i--)
                    if (required[i]?.GetValue<string>() == argument.Key) required.RemoveAt(i);
        }
        var description = $"{canonical.Description}\nBound object: {entry.Object.Reference}; name={Tools.Clip(entry.Object.Name, 160)}; "
            + $"prefab={Tools.Clip(entry.Object.Identifier, 120)}; kind={entry.Object.Kind}. Fixed arguments: {entry.Binding.Arguments.ToJsonString()}. "
            + "Do not supply fixed fields. This handle expires when this object is removed or replaced; discover again after an expired-handle error. "
            + (entry.Binding.Description ?? "");
        return new ToolDef(entry.Name, description, schema, canonical.Kind, canonical.Group,
            c => c.Mgr.ExecuteObjectTool(c, entry.Name)) { ControlOnly = canonical.ControlOnly, MovesOn = canonical.MovesOn };
    }

    public ToolDef? Resolve(int botId, string name) => bots.TryGetValue(botId, out var catalog) && catalog.Known.TryGetValue(name, out var bound)
        ? bound.Definition : null;

    public string? CanonicalName(int botId, string name) => bots.TryGetValue(botId, out var catalog) && catalog.Known.TryGetValue(name, out var bound)
        ? bound.Entry.Binding.Tool : null;

    public ToolCall CanonicalCall(int botId, ToolCall call)
    {
        if (!bots.TryGetValue(botId, out var catalog) || !catalog.Known.TryGetValue(call.Name, out var bound)) return call;
        var arguments = (JsonObject)call.Arguments.DeepClone();
        foreach (var p in bound.Entry.Binding.Arguments) arguments[p.Key] = p.Value?.DeepClone();
        return new ToolCall(bound.Entry.Binding.Tool, arguments);
    }

    public string? LiveError(int botId, string name)
    {
        if (!bots.TryGetValue(botId, out var catalog) || !catalog.Known.ContainsKey(name)) return "object handle was not discovered for this bot";
        return IsLive(catalog, catalog.Known[name].Entry) ? null : "object handle expired: object was removed, replaced or its capability changed; call object_tools again";
    }

    public IReadOnlyCollection<string> Names(int botId) => bots.TryGetValue(botId, out var catalog) ? catalog.Known.Keys.ToArray() : Array.Empty<string>();

    static HashSet<string> Pinned(BotAgent agent)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(PlanTree.Node node)
        {
            if (node is PlanTree.Do action) names.Add(action.Call.Name);
            foreach (var child in node.Children) Walk(child);
        }
        if (agent.Plan is { } plan) Walk(plan.Tree.Root);
        return names;
    }

    static void Trim(Catalog catalog, BotAgent agent)
    {
        while (catalog.Active.Count > MaxBoundTools) catalog.Active.RemoveFirst();
        var keep = Pinned(agent);
        keep.UnionWith(catalog.Active);
        foreach (var name in catalog.Known.Keys.Where(name => !keep.Contains(name)).ToArray()) catalog.Known.Remove(name);
    }

    public IReadOnlyList<ToolSpec> Offered(BotAgent agent, int budget, Func<string, bool> allowed)
    {
        if (!bots.TryGetValue(agent.Id, out var catalog)) return Array.Empty<ToolSpec>();
        Trim(catalog, agent);
        return catalog.Active.Reverse().Where(name => IsLive(catalog, catalog.Known[name].Entry) && allowed(name))
            .Take(Math.Max(0, Math.Min(MaxBoundTools, budget))).Select(name => catalog.Known[name].Definition.Spec).ToArray();
    }

    public string Discover(BotAgent agent, string? target, int offset, int limit, Func<string, bool> canonicalAllowed, bool includeSchemas = false)
    {
        if (offset < 0) return "error: offset must be at least 0";
        if (limit is < 1 or > MaxPageSize) return $"error: limit must be between 1 and {MaxPageSize}";
        var catalog = For(agent.Id);
        var matches = catalog.Objects.Values.Where(obj => target == null ||
            obj.Reference.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            obj.Identifier.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            obj.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(obj => obj.Reference, StringComparer.Ordinal)
            .SelectMany(obj => Actions(obj).Where(binding => canonicalAllowed(binding.Tool))
                .OrderBy(binding => binding.Tool, StringComparer.Ordinal).ThenBy(binding => binding.Arguments.ToJsonString(), StringComparer.Ordinal)
                .Select(binding => (Object: obj, Binding: binding)))
            .ToArray();
        // Materialize names/contracts ONLY for this explicit page, even on submarines with tens of thousands of entities.
        var page = matches.Skip(offset).Take(limit).Select(match =>
        {
            var references = BoundReferences(catalog, match.Binding);
            return new Entry(match.Object, match.Binding with { Arguments = (JsonObject)match.Binding.Arguments.DeepClone() },
                ToolName(match.Object, match.Binding, references), references);
        }).ToArray();
        var text = new StringBuilder($"Object tools: {matches.Length} matching tools; offset={offset}; returned={page.Length}; "
            + (offset + page.Length < matches.Length ? $"next_offset={offset + page.Length}" : "end of catalog")
            + (includeSchemas ? $". Full schemas follow; these handles are bound for bot {agent.Name} [c{agent.Id}] only.\n"
                : ". Full schemas for returned handles will be offered on your NEXT turn.\n"));
        foreach (var entry in page)
        {
            if (!catalog.Known.ContainsKey(entry.Name)) catalog.Known.Add(entry.Name, new Bound(entry, Materialize(entry)));
            catalog.Active.Remove(entry.Name);
            catalog.Active.AddLast(entry.Name);
            text.Append(entry.Name).Append(" -> ").Append(entry.Binding.Tool).Append(" fixed=").Append(entry.Binding.Arguments.ToJsonString())
                .Append("; object=").Append(entry.Object.Reference).Append(" name=").Append(Tools.Clip(entry.Object.Name, 100))
                .Append(" prefab=").Append(entry.Object.Identifier).Append(" kind=").Append(entry.Object.Kind)
                .Append(" room=").Append(entry.Object.Room ?? "unknown").Append('\n');
            if (includeSchemas)
            {
                var definition = catalog.Known[entry.Name].Definition;
                text.Append("Description: ").Append(definition.Description).Append('\n');
                text.Append("Arguments JSON Schema: ").Append(definition.Schema.ToJsonString()).Append('\n');
            }
        }
        Trim(catalog, agent);
        return text.ToString();
    }
}
