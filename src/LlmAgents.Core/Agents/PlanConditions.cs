using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LlmAgents.Agents;

/// <summary>What a plan condition can look at (built from the bot's latest observation).</summary>
public interface IConditionContext
{
    double Number(string name);
    double Call(string name, string arg);
}

/// <summary>
/// Conditions of plan trees: a tiny expression language the model writes, e.g.
/// <c>flora &gt; 0 and has('welding tool')</c>, <c>not in_room('Ballast [h5]')</c>, <c>health &lt; 50 or threats</c>.
/// A bare name means "more than 0". Unknown names are refused when the plan is set, so the model can fix them.
/// </summary>
public static class PlanConditions
{
    /// <summary>Numbers the condition can use, with what they mean (also shown to the model).</summary>
    public static readonly IReadOnlyDictionary<string, string> Numbers = new Dictionary<string, string>
    {
        ["health"] = "your health %", ["oxygen"] = "your oxygen %", ["room_water"] = "water % in your room",
        ["room_fire"] = "fires in your room", ["threats"] = "hostiles near you", ["leaks"] = "rooms with hull breaches",
        ["flora"] = "ballast flora branches aboard", ["reactor"] = "reactor output %", ["reactor_fuel"] = "reactor fuel %",
        ["sub_oxygen"] = "oxygen % aboard", ["injured"] = "crewmates below 50% health", ["time"] = "seconds since the plan started",
        ["task"] = "1 while a task runs", ["last_ok"] = "1 if the last step worked", ["last_failed"] = "1 if the last step failed",
        ["marked"] = "things players marked for the AI",
    };

    public static readonly IReadOnlyDictionary<string, string> Functions = new Dictionary<string, string>
    {
        ["in_room"] = "in_room('Ballast [h5]'): you are in that room", ["has"] = "has('welding tool'): you carry it",
        ["wearing"] = "wearing('diving suit')", ["order"] = "order('fixleaks'): you have that order",
        ["threat"] = "threat('crawler'): such a hostile is near", ["water"] = "water('Ballast [h5]'): water % there",
        ["fires"] = "fires('Engine [h2]'): fires there", ["near"] = "near('Daniil'): in your room",
        ["result"] = "result('equipped'): the last result contains the text",
    };

    public sealed class Condition
    {
        readonly Func<IConditionContext, double> eval;
        public string Text { get; }
        internal Condition(string text, Func<IConditionContext, double> eval) { Text = text; this.eval = eval; }
        public bool IsTrue(IConditionContext ctx)
        {
            try { return eval(ctx) != 0; }
            catch { return false; }
        }
    }

    /// <summary>Parses a condition; throws FormatException with a message the model can act on.</summary>
    public static Condition Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("empty condition");
        var p = new Parser(text);
        var f = p.Or();
        p.End();
        return new Condition(text.Trim(), f);
    }

    sealed class Parser
    {
        readonly List<string> tokens;
        int pos;

        static readonly Regex Token = new(@"\G\s*(?:(?<t>\d+(?:\.\d+)?|'[^']*'|""[^""]*""|<=|>=|==|!=|&&|\|\||[<>=!(),]|[A-Za-z_][A-Za-z0-9_]*))", RegexOptions.Compiled);

        public Parser(string text)
        {
            tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                var m = Token.Match(text, i);
                if (!m.Success) throw new FormatException($"cannot read the condition at '{text.Substring(i)}'");
                tokens.Add(m.Groups["t"].Value);
                i += m.Length;
            }
        }

        string? Peek => pos < tokens.Count ? tokens[pos] : null;
        string NextToken() => pos < tokens.Count ? tokens[pos++] : throw new FormatException("the condition ends too early");
        bool Accept(params string[] options)
        {
            if (Peek != null && options.Any(o => o.Equals(Peek, StringComparison.OrdinalIgnoreCase))) { pos++; return true; }
            return false;
        }

        public void End()
        {
            if (Peek != null) throw new FormatException($"unexpected '{Peek}' in the condition");
        }

        public Func<IConditionContext, double> Or()
        {
            var left = And();
            while (Accept("or", "||"))
            {
                var l = left; var r = And();
                left = c => l(c) != 0 || r(c) != 0 ? 1 : 0;
            }
            return left;
        }

        Func<IConditionContext, double> And()
        {
            var left = Not();
            while (Accept("and", "&&"))
            {
                var l = left; var r = Not();
                left = c => l(c) != 0 && r(c) != 0 ? 1 : 0;
            }
            return left;
        }

        Func<IConditionContext, double> Not()
        {
            if (Accept("not", "!"))
            {
                var inner = Not();
                return c => inner(c) == 0 ? 1 : 0;
            }
            return Compare();
        }

        Func<IConditionContext, double> Compare()
        {
            var left = Term();
            var op = Peek;
            if (op is "<" or ">" or "<=" or ">=" or "==" or "=" or "!=")
            {
                pos++;
                var right = Term();
                return op switch
                {
                    "<" => c => left(c) < right(c) ? 1 : 0,
                    ">" => c => left(c) > right(c) ? 1 : 0,
                    "<=" => c => left(c) <= right(c) ? 1 : 0,
                    ">=" => c => left(c) >= right(c) ? 1 : 0,
                    "!=" => c => Math.Abs(left(c) - right(c)) > 1e-9 ? 1 : 0,
                    _ => c => Math.Abs(left(c) - right(c)) < 1e-9 ? 1 : 0,
                };
            }
            return left;
        }

        Func<IConditionContext, double> Term()
        {
            var t = NextToken();
            if (t == "(")
            {
                var inner = Or();
                if (!Accept(")")) throw new FormatException("missing ')' in the condition");
                return inner;
            }
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return _ => n;
            if (t is "true") return _ => 1;
            if (t is "false") return _ => 0;
            var name = t.ToLowerInvariant();
            if (Accept("("))
            {
                if (!Functions.ContainsKey(name))
                    throw new FormatException($"unknown function '{t}'; known: {string.Join(", ", Functions.Keys)}");
                string arg = "";
                if (Peek != ")")
                {
                    var a = NextToken();
                    arg = a.Length >= 2 && (a[0] == '\'' || a[0] == '"') ? a.Substring(1, a.Length - 2) : a;
                }
                if (!Accept(")")) throw new FormatException($"missing ')' after {t}(...");
                return c => c.Call(name, arg);
            }
            if (!Numbers.ContainsKey(name))
                throw new FormatException($"unknown name '{t}'; known: {string.Join(", ", Numbers.Keys)} and {string.Join(", ", Functions.Keys)}(...)");
            return c => c.Number(name);
        }
    }

    static readonly Regex FloraCount = new(@"ballast flora: (\d+) branches", RegexOptions.Compiled);

    /// <summary>A condition context over an observation.</summary>
    public sealed class ObservationContext : IConditionContext
    {
        readonly Observation o;
        readonly double planSeconds;
        readonly bool? lastOk;
        readonly string? lastResult;
        readonly bool taskRunning;

        public ObservationContext(Observation o, double planSeconds, bool? lastOk, string? lastResult, bool taskRunning)
        {
            this.o = o;
            this.planSeconds = planSeconds;
            this.lastOk = lastOk;
            this.lastResult = lastResult;
            this.taskRunning = taskRunning;
        }

        public double Number(string name) => name switch
        {
            "health" => o.HealthPercent,
            "oxygen" => o.OxygenPercent,
            "room_water" => o.RoomWaterPercent,
            "room_fire" => o.RoomFires,
            "threats" => o.Threats.Count,
            "leaks" => o.Leaks.Count,
            "flora" => o.Infestations.Sum(i => FloraCount.Match(i) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 1),
            "reactor" => o.Submarine?.ReactorOutputPercent ?? 0,
            "reactor_fuel" => o.Submarine?.ReactorFuelPercent ?? 100,
            "sub_oxygen" => o.Submarine?.OxygenPercent ?? 100,
            "injured" => o.Crew.Count(c => c.HealthPercent < 50),
            "time" => planSeconds,
            "task" => taskRunning ? 1 : 0,
            "last_ok" => lastOk == true ? 1 : 0,
            "last_failed" => lastOk == false ? 1 : 0,
            "marked" => o.Marks.Count(m => m.StartsWith("for the AI", StringComparison.Ordinal)),
            _ => 0,
        };

        static bool Has(IEnumerable<string> list, string what) =>
            list.Any(x => x.Contains(what, StringComparison.OrdinalIgnoreCase)) || list.Any(x => ItemWords.WordsMatch(what, new[] { x }));

        public double Call(string name, string arg) => name switch
        {
            "in_room" => AgentManager.InRoom(o.Room, arg) ? 1 : 0,
            "has" => Has(o.Equipped.Concat(o.Inventory), arg) ? 1 : 0,
            "wearing" => Has(o.Equipped, arg) ? 1 : 0,
            "order" => o.CurrentOrder.Contains(arg, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            "threat" => o.Threats.Any(t => t.Kind.Contains(arg, StringComparison.OrdinalIgnoreCase)) ? 1 : 0,
            "water" => o.Rooms.FirstOrDefault(r => AgentManager.InRoom(r.Id, arg))?.WaterPercent ?? 0,
            "fires" => o.Rooms.FirstOrDefault(r => AgentManager.InRoom(r.Id, arg))?.Fires ?? 0,
            "near" => o.Crew.Any(c => c.Name.Contains(arg, StringComparison.OrdinalIgnoreCase) && c.Room != null && c.Room == o.Room) ? 1 : 0,
            "result" => lastResult?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true ? 1 : 0,
            _ => 0,
        };
    }
}
