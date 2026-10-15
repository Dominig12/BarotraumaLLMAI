using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmAgents.SettingsUi;

/// <summary>
/// Edits llm_config.json as a JSON tree, so unknown keys survive and only touched values change.
/// Every change set is validated by parsing the result with <see cref="ModSettings.Parse"/> before it is kept.
///
/// Paths are JSON property names, with two conveniences:
/// providers are addressed by name (["providers", "local", "model"]) and
/// bots.disabledJobs by job (["bots", "disabledJobs", "assistant"] = true/false).
/// API keys cannot be set this way (they would travel over the game network); use apiKeyEnv.
/// </summary>
public sealed class ConfigEditor
{
    readonly string? path;
    JsonObject root;

    public ModSettings Settings { get; private set; }

    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public ConfigEditor(string json, string? path = null)
    {
        this.path = path;
        root = Parse(json);
        Settings = ModSettings.Parse(json);
    }

    public static ConfigEditor Load(string path) => new(File.ReadAllText(path), path);

    static JsonObject Parse(string json) =>
        JsonNode.Parse(json, NodeOptions, DocOptions) as JsonObject ?? throw new InvalidDataException("llm_config.json is not a JSON object");

    public string ToJson() => root.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Re-reads the file (after editing it by hand). Returns an error text or null.</summary>
    public string? Reload()
    {
        if (path == null) return "error: no file";
        try
        {
            var json = File.ReadAllText(path);
            var s = ModSettings.Parse(json);
            root = Parse(json);
            Settings = s;
            return null;
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
    }

    public void Save()
    {
        if (path == null) return;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    /// <summary>
    /// Applies all changes or none. On success <see cref="Settings"/> is the new configuration and the result is null;
    /// otherwise the error text is returned and nothing changes.
    /// </summary>
    public string? TrySet(IEnumerable<(string[] Path, JsonNode? Value)> changes)
    {
        var copy = (JsonObject)JsonNode.Parse(root.ToJsonString(), NodeOptions)!;
        foreach (var (p, v) in changes)
        {
            var err = SetIn(copy, p, v);
            if (err != null) return err;
        }
        ModSettings parsed;
        try
        {
            parsed = ModSettings.Parse(copy.ToJsonString());
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
        root = copy;
        Settings = parsed;
        return null;
    }

    /// <summary>
    /// Adds settings that are missing from the file with their default values (new versions bring new settings;
    /// an existing llm_config.json is never replaced on update). Values already there are never touched.
    /// Returns the added keys ("global.maxChatEchoPerMinute", "providers.local.toolMode"...); nothing changes on error.
    /// </summary>
    public List<string> AddMissingDefaults()
    {
        var added = new List<string>();
        var copy = (JsonObject)JsonNode.Parse(root.ToJsonString(), NodeOptions)!;
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var defaults = JsonSerializer.SerializeToNode(new ModSettings(), opts)!.AsObject();
        defaults.Remove("providers");
        Merge(copy, defaults, "", added);

        if (copy["providers"] is JsonArray providers)
        {
            var providerDefaults = JsonSerializer.SerializeToNode(new ProviderConfig(), opts)!.AsObject();
            foreach (var key in new[] { "name", "type", "baseUrl", "model", "apiKey" }) providerDefaults.Remove(key);
            foreach (var p in providers.OfType<JsonObject>())
                Merge(p, providerDefaults, $"providers.{(string?)p["name"] ?? "?"}.", added);
        }
        if (added.Count == 0) return added;
        try
        {
            Settings = ModSettings.Parse(copy.ToJsonString());
            root = copy;
            return added;
        }
        catch (Exception e)
        {
            Log.Warn("could not add new default settings: " + e.Message);
            return new List<string>();
        }
    }

    static void Merge(JsonObject target, JsonObject defaults, string prefix, List<string> added)
    {
        foreach (var (key, value) in defaults.ToList())
        {
            var existing = target.Select(kv => kv.Key).FirstOrDefault(k => Is(k, key));
            if (existing == null)
            {
                target[key] = value?.DeepClone();
                added.Add(prefix + key);
            }
            else if (target[existing] is JsonObject t && value is JsonObject d && d.Count > 0 && !IsMap(key))
            {
                Merge(t, d, prefix + key + ".", added);
            }
        }
    }

    /// <summary>Maps keyed by job etc.: their defaults are examples, not keys to add.</summary>
    static bool IsMap(string key) => Is(key, "providerByJob") || Is(key, "extraPromptByJob") || Is(key, "extraHeaders");

    public JsonNode? Get(string[] p)
    {
        if (p.Length == 0) return null;
        JsonNode? node = root;
        for (int i = 0; i < p.Length; i++)
        {
            if (node is JsonObject o)
            {
                if (i == 1 && Is(p[0], "bots") && IsNameList(p[1]) && p.Length == 3)
                    return JsonValue.Create(o[p[1]] is JsonArray arr && arr.Any(x => Is(x?.GetValue<string>() ?? "", p[2])));
                node = o[p[i]];
            }
            else if (node is JsonArray a && i == 1 && Is(p[0], "providers"))
                node = a.OfType<JsonObject>().FirstOrDefault(x => Is((string?)x["name"] ?? "", p[i]));
            else return null;
        }
        return node;
    }

    /// <summary>bots.disabledJobs / bots.disabledCharacters: lists edited as name = true/false.</summary>
    static bool IsNameList(string s) => Is(s, "disabledJobs") || Is(s, "disabledCharacters");

    static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static string? SetIn(JsonObject root, string[] p, JsonNode? value)
    {
        if (p.Length == 0) return "error: empty path";
        if (p.Any(s => Is(s, "apiKey")))
            return "error: API keys cannot be changed from the game (they would be sent over the network); set apiKeyEnv and an environment variable on the server";

        // providers.<name>.<prop>
        if (Is(p[0], "providers"))
        {
            if (p.Length != 3) return "error: use providers.<name>.<setting>";
            var arr = root["providers"] as JsonArray;
            var prov = arr?.OfType<JsonObject>().FirstOrDefault(x => Is((string?)x["name"] ?? "", p[1]));
            if (prov == null) return $"error: no provider '{p[1]}'";
            if (Is(p[2], "name")) return "error: providers cannot be renamed from the game";
            prov[Prop(prov, p[2])] = value?.DeepClone();
            return null;
        }

        // bots.disabledJobs.<job> / bots.disabledCharacters.<name> = bool
        if (p.Length == 3 && Is(p[0], "bots") && IsNameList(p[1]))
        {
            var bots = Obj(root, "bots");
            var list = bots[Prop(bots, p[1])] as JsonArray;
            if (list == null) bots[Prop(bots, p[1])] = list = new JsonArray();
            bool on = value is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
            var existing = list.Where(x => Is(x?.GetValue<string>() ?? "", p[2])).ToList();
            foreach (var x in existing) list.Remove(x);
            if (on) list.Add(p[2]);
            return null;
        }

        JsonObject node = root;
        for (int i = 0; i < p.Length - 1; i++) node = Obj(node, p[i]);
        var last = Prop(node, p[^1]);
        bool isMap = p.Length == 3 && Is(p[0], "bots") && (Is(p[1], "providerByJob") || Is(p[1], "extraPromptByJob"));
        // in maps an empty value means "no entry" (use the default)
        if (isMap && (value == null || (value is JsonValue sv && sv.TryGetValue<string>(out var str) && string.IsNullOrWhiteSpace(str))))
            node.Remove(last);
        else
            node[last] = value?.DeepClone();
        return null;
    }

    /// <summary>Child object by (case-insensitive) name, created when missing.</summary>
    static JsonObject Obj(JsonObject parent, string name)
    {
        var key = Prop(parent, name);
        if (parent[key] is JsonObject o) return o;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    /// <summary>The existing spelling of a property (files may use any case), or the name as given.</summary>
    static string Prop(JsonObject o, string name) => o.Select(kv => kv.Key).FirstOrDefault(k => Is(k, name)) ?? name;
}
