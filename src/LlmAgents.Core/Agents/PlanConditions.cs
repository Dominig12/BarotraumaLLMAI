using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LlmAgents.Agents;

/// <summary>What a plan condition can look at (built from the bot's latest observation).</summary>
public interface IConditionContext
{
    double Number(string name);
    double Call(string name, string arg);
}

/// <summary>A bounded expression language for explicit plan predicates, not natural-language commands.
/// Bare numbers are true when nonzero. Unknown names and malformed expressions are refused before execution.</summary>
public static class PlanConditions
{
    public const int MaxExpressionLength = 2048;
    public const int MaxTokens = 256;
    public const int MaxDepth = 32;

    /// <summary>Numbers the condition can use, also shown to the model.</summary>
    public static readonly IReadOnlyDictionary<string, string> Numbers = new Dictionary<string, string>
    {
        ["health"] = "your health %", ["oxygen"] = "your oxygen %", ["room_water"] = "water % in your room",
        ["room_fire"] = "fires in your room", ["threats"] = "hostiles near you", ["leaks"] = "rooms with hull breaches",
        ["flora"] = "ballast flora branches aboard", ["reactor"] = "reactor output % when known", ["reactor_fuel"] = "reactor fuel % when known",
        ["sub_oxygen"] = "oxygen % aboard when known", ["injured"] = "crewmates below 50% health", ["time"] = "seconds since the plan started",
        ["task"] = "1 while a task runs", ["last_ok"] = "1 if the last step worked", ["last_failed"] = "1 if the last step failed",
        ["marked"] = "things players marked for the AI", ["bodies"] = "known nearby bodies",
        ["free_slots"] = "empty general-purpose root inventory slots when known",
    };

    public static readonly IReadOnlyDictionary<string, string> Functions = new Dictionary<string, string>
    {
        ["in_room"] = "in_room('Ballast [h5]'): you are in that room",
        ["has"] = "has('weldingtool') or has('#123'): own inventory contains the exact prefab, exact name, or entity ID, including bags",
        ["wearing"] = "wearing('divingsuit'): exact prefab, exact name, or #ID is worn or held, not stored in a bag",
        ["count"] = "count('bandage'): number of own item instances with that exact prefab, exact name, or #ID, including bags",
        ["condition"] = "condition('#123'): item condition %; for a prefab or exact name, best matching item condition; missing item is unknown",
        ["order"] = "order('fixleaks'): you have that order", ["threat"] = "threat('crawler'): such a hostile is near",
        ["water"] = "water('Ballast [h5]'): water % in a known room", ["fires"] = "fires('Engine [h2]'): fires in a known room",
        ["near"] = "near('Daniil'): in your room", ["result"] = "result('equipped'): the last result contains the text",
    };

    public const string Syntax = "Use numbers, true/false, one-argument functions, comparisons < <= > >= == !=, and/or/not (also && || !), and parentheses. " +
        "Quote function arguments; escape quotes or backslashes with a backslash. Conditions are checked against fresh observations. " +
        "Unavailable numeric facts make the evaluated condition false, including comparisons to zero; and/or short-circuit. " +
        "Inventory selectors use exact prefab identifiers, #entity IDs, or exact names. Maximum 2048 characters, 256 tokens, nesting 32.";

    public sealed class Condition
    {
        readonly Func<IConditionContext, double> eval;
        public string Text { get; }
        internal Condition(string text, Func<IConditionContext, double> eval) { Text = text; this.eval = eval; }
        public bool IsTrue(IConditionContext ctx)
        {
            try { return Value(eval, ctx) != 0; }
            catch { return false; }
        }
    }

    static double Value(Func<IConditionContext, double> eval, IConditionContext context)
    {
        var value = eval(context);
        if (!double.IsFinite(value)) throw new InvalidOperationException("condition value is unavailable or not finite");
        return value;
    }

    /// <summary>Parses a condition; throws FormatException with a message the model can act on.</summary>
    public static Condition Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("empty condition");
        if (text.Length > MaxExpressionLength) throw new FormatException($"condition exceeds {MaxExpressionLength} characters");
        var p = new Parser(text);
        var f = p.Or();
        p.End();
        return new Condition(text.Trim(), f);
    }

    readonly record struct Token(string Text, bool Quoted = false);

    sealed class Parser
    {
        readonly List<Token> tokens = new();
        int pos;
        int depth;

        public Parser(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (tokens.Count >= MaxTokens) throw new FormatException($"condition exceeds {MaxTokens} tokens");
                var start = i;
                char c = text[i];
                if (c is '\'' or '"')
                {
                    i++;
                    var value = new StringBuilder();
                    bool closed = false;
                    while (i < text.Length)
                    {
                        var ch = text[i++];
                        if (ch == c) { closed = true; break; }
                        if (ch != '\\') { value.Append(ch); continue; }
                        if (i >= text.Length) throw new FormatException("unfinished escape in quoted condition argument");
                        var escape = text[i++];
                        if (escape == 'u')
                        {
                            if (i + 4 > text.Length || !ushort.TryParse(text.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                                throw new FormatException("invalid Unicode escape in quoted condition argument");
                            value.Append((char)code);
                            i += 4;
                        }
                        else value.Append(escape switch
                        {
                            '\\' => '\\', '\'' => '\'', '"' => '"', 'n' => '\n', 'r' => '\r', 't' => '\t',
                            _ => throw new FormatException($"unsupported escape '\\{escape}' in condition argument"),
                        });
                    }
                    if (!closed) throw new FormatException("unterminated quoted condition argument");
                    tokens.Add(new Token(value.ToString(), Quoted: true));
                    continue;
                }
                if (char.IsDigit(c) || c is '-' or '+' || c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
                {
                    if (c is '-' or '+') i++;
                    bool digit = false;
                    while (i < text.Length && char.IsDigit(text[i])) { i++; digit = true; }
                    if (i < text.Length && text[i] == '.')
                    {
                        i++;
                        while (i < text.Length && char.IsDigit(text[i])) { i++; digit = true; }
                    }
                    if (!digit) throw new FormatException($"invalid number at character {start + 1}");
                    if (i < text.Length && text[i] is 'e' or 'E')
                    {
                        i++;
                        if (i < text.Length && text[i] is '-' or '+') i++;
                        var exponent = i;
                        while (i < text.Length && char.IsDigit(text[i])) i++;
                        if (i == exponent) throw new FormatException($"invalid numeric exponent at character {start + 1}");
                    }
                    var number = text.Substring(start, i - start);
                    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                        throw new FormatException("condition numbers must be finite");
                    tokens.Add(new Token(number));
                    continue;
                }
                if (char.IsAsciiLetter(c) || c == '_')
                {
                    i++;
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(new Token(text.Substring(start, i - start)));
                    continue;
                }
                if (i + 1 < text.Length && text.Substring(i, 2) is "<=" or ">=" or "==" or "!=" or "&&" or "||")
                { tokens.Add(new Token(text.Substring(i, 2))); i += 2; continue; }
                if (c is '<' or '>' or '=' or '!' or '(' or ')' or ',')
                { tokens.Add(new Token(c.ToString())); i++; continue; }
                throw new FormatException($"cannot read the condition at character {i + 1}: '{text.Substring(i, Math.Min(40, text.Length - i))}'");
            }
        }

        Token? Peek => pos < tokens.Count ? tokens[pos] : null;
        Token NextToken() => pos < tokens.Count ? tokens[pos++] : throw new FormatException("the condition ends too early");
        bool Accept(params string[] options)
        {
            if (Peek is { Quoted: false } token && options.Any(o => o.Equals(token.Text, StringComparison.OrdinalIgnoreCase))) { pos++; return true; }
            return false;
        }

        void Enter()
        {
            if (++depth > MaxDepth) throw new FormatException($"condition nesting exceeds {MaxDepth}");
        }

        public void End()
        {
            if (Peek is { } token) throw new FormatException($"unexpected '{token.Text}' in the condition");
        }

        public Func<IConditionContext, double> Or()
        {
            var left = And();
            while (Accept("or", "||"))
            {
                var l = left; var r = And();
                left = c => Value(l, c) != 0 || Value(r, c) != 0 ? 1 : 0;
            }
            return left;
        }

        Func<IConditionContext, double> And()
        {
            var left = Not();
            while (Accept("and", "&&"))
            {
                var l = left; var r = Not();
                left = c => Value(l, c) != 0 && Value(r, c) != 0 ? 1 : 0;
            }
            return left;
        }

        Func<IConditionContext, double> Not()
        {
            if (!Accept("not", "!")) return Compare();
            Enter();
            var inner = Not();
            depth--;
            return c => Value(inner, c) == 0 ? 1 : 0;
        }

        Func<IConditionContext, double> Compare()
        {
            var left = Term();
            var op = Peek;
            if (op is not { Quoted: false } token || token.Text is not ("<" or ">" or "<=" or ">=" or "==" or "=" or "!=")) return left;
            pos++;
            var right = Term();
            return token.Text switch
            {
                "<" => c => Value(left, c) < Value(right, c) ? 1 : 0,
                ">" => c => Value(left, c) > Value(right, c) ? 1 : 0,
                "<=" => c => Value(left, c) <= Value(right, c) ? 1 : 0,
                ">=" => c => Value(left, c) >= Value(right, c) ? 1 : 0,
                "!=" => c => Math.Abs(Value(left, c) - Value(right, c)) >= 1e-9 ? 1 : 0,
                _ => c => Math.Abs(Value(left, c) - Value(right, c)) < 1e-9 ? 1 : 0,
            };
        }

        Func<IConditionContext, double> Term()
        {
            var token = NextToken();
            var t = token.Text;
            if (token.Quoted) throw new FormatException("quoted strings are only allowed as function arguments");
            if (t == "(")
            {
                Enter();
                var inner = Or();
                depth--;
                if (!Accept(")")) throw new FormatException("missing ')' in the condition");
                return inner;
            }
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                if (!double.IsFinite(n)) throw new FormatException("condition numbers must be finite");
                return _ => n;
            }
            var name = t.ToLowerInvariant();
            if (name == "true") return _ => 1;
            if (name == "false") return _ => 0;
            if (Accept("("))
            {
                if (!Functions.ContainsKey(name)) throw new FormatException($"unknown function '{t}'; known: {string.Join(", ", Functions.Keys)}");
                var argument = NextToken();
                if (!argument.Quoted && (argument.Text is ")" or "(" or "," or "<" or ">" or "=" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "!"))
                    throw new FormatException($"{t}(...) requires one nonempty argument");
                if (string.IsNullOrWhiteSpace(argument.Text)) throw new FormatException($"{t}(...) requires one nonempty argument");
                if (!Accept(")")) throw new FormatException($"missing ')' after {t}(...; exactly one argument is required");
                return c => c.Call(name, argument.Text);
            }
            if (!Numbers.ContainsKey(name)) throw new FormatException($"unknown name '{t}'; known: {string.Join(", ", Numbers.Keys)} and {string.Join(", ", Functions.Keys)}(...)");
            return c => c.Number(name);
        }
    }

    static readonly Regex FloraCount = new(@"ballast flora: (\d+) branches", RegexOptions.Compiled);

    /// <summary>A condition context over a lazily fetched observation. No observation is read for pure plan state conditions.</summary>
    public sealed class ObservationContext : IConditionContext
    {
        readonly Lazy<Observation> observation;
        readonly double planSeconds;
        readonly bool? lastOk;
        readonly string? lastResult;
        readonly bool taskRunning;

        public ObservationContext(Observation o, double planSeconds, bool? lastOk, string? lastResult, bool taskRunning)
            : this(() => o, planSeconds, lastOk, lastResult, taskRunning) { }

        public ObservationContext(Func<Observation> look, double planSeconds, bool? lastOk, string? lastResult, bool taskRunning)
        {
            observation = new Lazy<Observation>(look, isThreadSafe: false);
            this.planSeconds = planSeconds;
            this.lastOk = lastOk;
            this.lastResult = lastResult;
            this.taskRunning = taskRunning;
        }

        Observation o => observation.Value;

        public double Number(string name) => name switch
        {
            "health" => o.HealthPercent,
            "oxygen" => o.OxygenPercent,
            "room_water" => o.RoomWaterPercent,
            "room_fire" => o.RoomFires,
            "threats" => o.Threats.Count,
            "leaks" => o.Leaks.Count,
            "flora" => o.Infestations.Sum(i => FloraCount.Match(i) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var count) ? count : 1),
            "reactor" => Known(o.Submarine?.ReactorOutputPercent),
            "reactor_fuel" => Known(o.Submarine?.ReactorFuelPercent),
            "sub_oxygen" => Known(o.Submarine?.OxygenPercent),
            "injured" => o.Crew.Count(c => c.HealthPercent < 50),
            "time" => planSeconds,
            "task" => taskRunning ? 1 : 0,
            "last_ok" => lastOk == true ? 1 : 0,
            "last_failed" => lastOk == false ? 1 : 0,
            "marked" => o.Marks.Count(m => m.StartsWith("for the AI", StringComparison.Ordinal)),
            "bodies" => o.Bodies.Count,
            "free_slots" => Known(o.InventoryFreeSlots),
            _ => throw new InvalidOperationException($"unknown condition number '{name}'"),
        };

        static double Known(int? value) => value ?? throw new InvalidOperationException("condition fact is unavailable");

        static bool LegacyHas(IEnumerable<string> list, string what) =>
            list.Any(x => x.Contains(what, StringComparison.OrdinalIgnoreCase)) || list.Any(x => ItemWords.WordsMatch(what, new[] { x }));

        static bool Matches(ObservedInventoryItem item, string selector)
        {
            selector = selector.Trim();
            if (selector.StartsWith("#", StringComparison.Ordinal))
                return int.TryParse(selector.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && item.Id == id;
            return item.Prefab.Equals(selector, StringComparison.OrdinalIgnoreCase) || item.Name.Equals(selector, StringComparison.OrdinalIgnoreCase);
        }

        IEnumerable<ObservedInventoryItem> MatchingItems(string selector) =>
            o.InventoryItems?.Where(item => Matches(item, selector)) ?? throw new InvalidOperationException("exact inventory facts are unavailable");

        double Has(string selector, bool equippedOnly)
        {
            if (o.InventoryItems != null) return MatchingItems(selector).Any(item => !equippedOnly || item.Equipped) ? 1 : 0;
            // Old saved observations and third-party adapters used presentation strings. Live game observations supply typed facts.
            return LegacyHas(equippedOnly ? o.Equipped : o.Equipped.Concat(o.Inventory), selector) ? 1 : 0;
        }

        double ItemCondition(string selector)
        {
            var items = MatchingItems(selector).Select(item => item.ConditionPercent).ToList();
            return items.Count > 0 ? items.Max() : throw new InvalidOperationException("the requested item is missing");
        }

        public double Call(string name, string arg) => name switch
        {
            "in_room" => AgentManager.InRoom(o.Room, arg) ? 1 : 0,
            "has" => Has(arg, equippedOnly: false),
            "wearing" => Has(arg, equippedOnly: true),
            "count" => MatchingItems(arg).Count(),
            "condition" => ItemCondition(arg),
            "order" => o.CurrentOrder.Contains(arg, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            "threat" => o.Threats.Any(t => t.Kind.Contains(arg, StringComparison.OrdinalIgnoreCase)) ? 1 : 0,
            "water" => Known(o.Rooms.FirstOrDefault(r => AgentManager.InRoom(r.Id, arg))?.WaterPercent),
            "fires" => Known(o.Rooms.FirstOrDefault(r => AgentManager.InRoom(r.Id, arg))?.Fires),
            "near" => o.Crew.Any(c => c.Name.Contains(arg, StringComparison.OrdinalIgnoreCase) && c.Room != null && c.Room == o.Room) ? 1 : 0,
            "result" => lastResult?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true ? 1 : 0,
            _ => throw new InvalidOperationException($"unknown condition function '{name}'"),
        };
    }
}
