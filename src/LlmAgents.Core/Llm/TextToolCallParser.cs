using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LlmAgents.Llm;

/// <summary>
/// Fallback for models whose tool calls arrive as plain text (small local models, servers without a
/// tool-call template for the model). Understands, in this order:
///   1. JSON: {"name":..,"arguments":{..}}, arrays of those, {"calls"|"actions"|"tool_calls":[..]}
///   2. Gemma-style and other loose syntax seen in the wild:
///        call:say{text:&lt;|"|&gt;Hi&lt;|"|&gt;}&lt;tool_call|&gt;     go_to_room{room:"Engine [h4]"}
///        remember,"note":"..."     message_crew,"all","text"     "repairsystems"     wait
///      Positional values are mapped to the tool's parameters in schema order (required first).
/// Tool names must match exactly (case-sensitive) and start a line or follow a quote, so ordinary
/// prose ("Wait, the reactor...") is not mistaken for a call. Vanilla order ids are accepted as aliases.
/// </summary>
public static class TextToolCallParser
{
    const char Q = '\u0001'; // normalized quote for Gemma's <|"|> string delimiter

    static readonly Dictionary<string, string> Aliases = new()
    {
        ["fixleaks"] = "fix_leaks",
        ["repairsystems"] = "repair_systems",
        ["extinguishfires"] = "extinguish_fires",
        ["fightintruders"] = "fight_intruders",
        ["rescue"] = "rescue_injured",
        ["cleanupitems"] = "clean_up",
        ["goto"] = "go_to_room",
        ["go_to"] = "go_to_room",
        ["dismissed"] = "return_to_default",
        ["dismiss"] = "return_to_default",
        ["carryon"] = "carry_on",
        ["continue"] = "carry_on",
        ["no_action"] = "carry_on",
        ["noop"] = "carry_on",
        ["take"] = "take_item",
        ["pick_up"] = "take_item",
        ["pickup"] = "take_item",
        ["grab"] = "take_item",
        ["get_item"] = "take_item",
        ["order"] = "give_order",
        ["set_order"] = "give_order",
        ["issue_order"] = "give_order",
        ["burn"] = "attack",
        ["burn_flora"] = "attack",
        ["kill"] = "attack",
        ["unload"] = "unpack",
        ["sort_items"] = "unpack",
        ["distribute"] = "unpack",
        ["put_away"] = "unpack",
    };

    public static List<ToolCall> Parse(string? text, IReadOnlyList<ToolSpec> tools)
    {
        var result = new List<ToolCall>();
        if (string.IsNullOrWhiteSpace(text) || tools.Count == 0) return result;
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var json = ParseJson(text!, byName);
        if (json.Count > 0) return json;
        return ParseLoose(Normalize(text!), byName);
    }

    // ------------------------------------------------------------------ JSON

    /// <summary>
    /// Every valid JSON block that holds tool calls. When a block is broken (small models often add or drop a
    /// brace: {"calls": [{"name": "a", "arguments": {..}}}, {"name": "b"..}]}), the calls inside it are still
    /// recovered one by one instead of losing all but the last.
    /// </summary>
    static List<ToolCall> ParseJson(string text, Dictionary<string, ToolSpec> tools)
    {
        var result = new List<ToolCall>();
        for (int start = 0; start < text.Length; start++)
        {
            char open = text[start];
            if (open != '{' && open != '[') continue;
            int end = MatchBracket(text, start);
            if (end < 0) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(text.Substring(start, end - start + 1)); }
            catch (JsonException) { continue; } // broken: look inside it

            int before = result.Count;
            if (node is JsonArray arr)
            {
                foreach (var item in arr) TryAddJson(item as JsonObject, tools, result);
            }
            else if (node is JsonObject obj)
            {
                var list = obj["calls"] as JsonArray ?? obj["actions"] as JsonArray ?? obj["tool_calls"] as JsonArray;
                if (list != null) foreach (var item in list) TryAddJson(item as JsonObject, tools, result);
                else TryAddJson(obj, tools, result);
            }
            if (result.Count > before) start = end; // its insides are done
        }
        return result;
    }

    static void TryAddJson(JsonObject? o, Dictionary<string, ToolSpec> tools, List<ToolCall> into)
    {
        if (o == null) return;
        if (o["function"] is JsonObject fn) o = fn; // OpenAI-shaped
        var name = Resolve(JsonUtil.Str(o["name"]) ?? JsonUtil.Str(o["tool"]), tools);
        if (name == null) return;
        var args = o["arguments"] ?? o["parameters"] ?? o["input"] ?? o["args"];
        JsonObject parsed = args switch
        {
            JsonObject a => (JsonObject)a.DeepClone(),
            JsonValue v when v.TryGetValue<string>(out var s) => SafeObject(s),
            _ => new JsonObject(),
        };
        into.Add(new ToolCall(name, parsed));
    }

    static JsonObject SafeObject(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    /// <summary>Yields balanced {...} / [...] substrings, outermost first.</summary>
    static IEnumerable<string> JsonCandidates(string text)
    {
        for (int start = 0; start < text.Length; start++)
        {
            char open = text[start];
            if (open != '{' && open != '[') continue;
            int end = MatchBracket(text, start);
            if (end < 0) continue;
            yield return text.Substring(start, end - start + 1);
            start = end;
        }
    }

    static int MatchBracket(string text, int start)
    {
        char open = text[start], close = open == '{' ? '}' : ']';
        int depth = 0;
        bool inString = false, escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"' || c == Q) inString = false;
                continue;
            }
            if (c == '"' || c == Q) inString = true;
            else if (c == open) depth++;
            else if (c == close && --depth == 0) return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------ loose syntax

    static string Normalize(string text) => text
        .Replace("<|\"|>", Q.ToString())
        .Replace("<tool_call|>", "\n")
        .Replace("<|tool_call>", "\n")
        .Replace("<start_function_call>", "\n")
        .Replace("<end_function_call>", "\n")
        .Replace("```json", "\n").Replace("```", "\n");

    static List<ToolCall> ParseLoose(string text, Dictionary<string, ToolSpec> tools)
    {
        var names = string.Join("|", tools.Keys.Concat(Aliases.Keys).OrderByDescending(n => n.Length).Select(Regex.Escape));
        // a tool name that starts a line (after bullets / comment marks / "call:"), follows a quote,
        // or follows another call on the same line: say("a") wait()
        var rx = new Regex(
            @"(?:(?<=^[ \t>*\-/#]*(?:call:)?)|(?<=[""\u0001])|(?<=\)[ \t;,]*)(?=(?:" + names + @")\())(?<name>" + names + @")(?![A-Za-z0-9_])",
            RegexOptions.Multiline);

        var matches = rx.Matches(text).Cast<Match>().ToList();
        var result = new List<ToolCall>();
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var name = Resolve(m.Groups["name"].Value, tools);
            if (name == null) continue;
            int segStart = m.Index + m.Length;
            int segEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var segment = text.Substring(segStart, segEnd - segStart);
            var args = ParseArgs(segment, tools[name], out bool plausible);
            if (plausible) result.Add(new ToolCall(name, args));
        }
        return result;
    }

    static readonly Regex KeyValue = new(
        @"[""']?(?<key>[A-Za-z_][A-Za-z0-9_]*)[""']?\s*[:=]\s*(?:""(?<v>(?:[^""\\]|\\.)*)""|\u0001(?<v>[^\u0001]*)\u0001|'(?<v>(?:[^'\\]|\\.)*)'(?=\s*[,})\]\n]|\s*$)|(?<v>[^,}\)\]\n""\u0001]+))");

    static readonly Regex Quoted = new(@"""(?<v>(?:[^""\\]|\\.)*)""|\u0001(?<v>[^\u0001]*)\u0001|(?<=[(,\s])'(?<v>(?:[^'\\]|\\.)*)'(?=\s*[,)])");

    static JsonObject ParseArgs(string segment, ToolSpec tool, out bool plausible)
    {
        var props = Properties(tool);
        var args = new JsonObject();
        var trimmed = segment.TrimStart();

        // Text right after the name must look like an argument list (or nothing), not like prose.
        plausible = trimmed.Length == 0 || "{(,:\"[\u0001".Contains(trimmed[0]) || char.IsWhiteSpace(segment.FirstOrDefault())
                    || props.Count(p => p.Required) == 1;

        // Strip a balanced {...} / (...) block if the arguments are wrapped in one.
        var body = segment;
        int brace = trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '(') ? segment.IndexOf(trimmed[0]) : -1;
        if (brace >= 0)
        {
            int end = trimmed[0] == '{' ? MatchBracket(segment, brace) : segment.IndexOf(')', brace);
            body = end > brace ? segment.Substring(brace + 1, end - brace - 1) : segment.Substring(brace + 1);
        }

        var used = new List<(int Start, int End)>();
        foreach (Match kv in KeyValue.Matches(body))
        {
            var key = kv.Groups["key"].Value;
            if (!props.Any(p => p.Name == key) || args.ContainsKey(key)) continue;
            args[key] = Unescape(kv.Groups["v"].Value.Trim());
            used.Add((kv.Index, kv.Index + kv.Length));
        }

        // Positional values; mixed with named ones only in call syntax: craft("medical kit", amount=1) -> item="medical kit".
        bool callSyntax = trimmed.StartsWith("(");
        var positional = (args.Count == 0 || callSyntax ? Quoted.Matches(body).Cast<Match>() : Enumerable.Empty<Match>())
            .Where(q => !used.Any(u => q.Index >= u.Start && q.Index < u.End))
            // ", " between two JSON strings of a cut-off reply is not a value
            .Select(q => Unescape(q.Groups["v"].Value)).Where(v => v.Any(char.IsLetterOrDigit)).ToList();
        var free = props.Where(p => !args.ContainsKey(p.Name)).ToList();
        for (int i = 0; i < positional.Count && i < free.Count; i++) args[free[i].Name] = positional[i];

        var required = props.Where(p => p.Required).ToList();
        // right after a JSON string ("say", "arguments": ...) the rest is JSON syntax, not the value
        bool insideJson = System.Text.RegularExpressions.Regex.IsMatch(segment, @"^[""\u0001]\s*[,:]");
        if (args.Count == 0 && required.Count == 1 && !insideJson)
        {
            // e.g. "sayЯ в инженерном отсеке..." : the rest of the line is the value
            var line = segment.Split('\n')[0].Trim().Trim('"', '\u0001', ':', ',', '{', '}', '(', ')', ' ');
            if (line.Length > 0) { args[required[0].Name] = line; plausible = true; }
        }

        // a required-args tool with no arguments at all is almost certainly prose
        if (required.Count > 0 && required.All(r => !args.ContainsKey(r.Name)) && args.Count == 0) plausible = false;
        return args;
    }

    sealed record Prop(string Name, bool Required);

    static List<Prop> Properties(ToolSpec tool)
    {
        var required = (tool.Parameters["required"] as JsonArray)?.Select(r => JsonUtil.Str(r)).Where(r => r != null).ToHashSet()
                       ?? new HashSet<string?>();
        var all = (tool.Parameters["properties"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? new List<string>();
        return all.Select(n => new Prop(n, required.Contains(n))).OrderBy(p => p.Required ? 0 : 1).ToList();
    }

    static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\n", " ").Replace("\\\\", "\\");

    static string? Resolve(string? name, Dictionary<string, ToolSpec> tools)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (tools.ContainsKey(name!)) return name;
        return Aliases.TryGetValue(name!, out var real) && tools.ContainsKey(real) ? real : null;
    }
}
