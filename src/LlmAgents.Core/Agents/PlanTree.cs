using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>
/// A plan with conditions, written by the model as a small tree and carried out by the mod without asking the model
/// again until it is done, fails or something important happens:
/// <code>
/// [ {"do":"equip","args":{"item":"welding tool"}},
///   {"if":"flora > 0", "then":[{"do":"attack","args":{"target":"ballast flora"}}], "else":[{"do":"repair_systems"}]},
///   {"wait_until":"in_room('Engine [h2]')", "timeout":60},
///   {"repeat":[{"do":"fix_leaks"}], "until":"leaks == 0", "max":3},
///   {"try":[{"do":"craft","args":{"item":"bandage"}}], "catch":[{"do":"say","args":{"text":"no materials"}}]} ]
/// </code>
/// Every node gets an id (n1, n2...) shown in 'plan'; update_plan replaces one node, plan replaces the whole tree.
/// A failed step fails the plan unless a "try" around it catches it.
/// </summary>
public sealed class PlanTree
{
    public const int MaxNodes = 40;
    public const int MaxDepth = 6;
    public const int MaxRepeat = 10;
    public const double MaxWaitSeconds = 300;

    public abstract class Node
    {
        public string Id = "";
        public abstract IEnumerable<Node> Children { get; }
    }
    public sealed class Do : Node
    {
        public ToolCall Call = null!;
        public override IEnumerable<Node> Children => Enumerable.Empty<Node>();
    }
    public sealed class Seq : Node
    {
        public List<Node> Items = new();
        public override IEnumerable<Node> Children => Items;
    }
    public sealed class If : Node
    {
        public PlanConditions.Condition Cond = null!;
        public Seq Then = null!;
        public Seq? Else;
        public override IEnumerable<Node> Children => Else != null ? new Node[] { Then, Else } : new Node[] { Then };
    }
    public sealed class Wait : Node
    {
        public PlanConditions.Condition Cond = null!;
        public double Timeout;
        public override IEnumerable<Node> Children => Enumerable.Empty<Node>();
    }
    public sealed class Repeat : Node
    {
        public Seq Body = null!;
        public PlanConditions.Condition? Until;
        public int Max;
        public override IEnumerable<Node> Children => new Node[] { Body };
    }
    public sealed class Try : Node
    {
        public Seq Body = null!;
        public Seq? Catch;
        public override IEnumerable<Node> Children => Catch != null ? new Node[] { Body, Catch } : new Node[] { Body };
    }

    // ---------------------------------------------------------------------------------------------- what to do next

    public abstract record Action;
    public sealed record ActAction(ToolCall Call, string NodeId) : Action;
    public sealed record WaitAction(Wait Node) : Action;
    public sealed record DoneAction : Action;
    public sealed record FailedAction(string Reason) : Action;

    sealed class Frame
    {
        public Node Node = null!;
        public int Index;        // Seq: next child; Repeat: iterations done; If/Try: 0 = not started, 1 = branch running
        public bool InCatch;
    }

    public Seq Root { get; private set; }
    readonly List<Frame> stack = new();
    readonly HashSet<string> done = new();
    int nextId = 1, nextSeq = 1;

    public bool? LastOk { get; private set; }
    public string? LastResult { get; private set; }
    public string? CurrentId { get; private set; }

    PlanTree(Seq root)
    {
        Root = root;
        stack.Add(new Frame { Node = root });
    }

    // ---------------------------------------------------------------------------------------------- building

    /// <summary>Tools a plan may not contain (they are about plans themselves).</summary>
    static readonly HashSet<string> NotInPlans = new() { "plan", "update_plan", "keep_plan", "carry_on" };

    /// <summary>Builds a tree from the model's JSON (an array of nodes, or one node). Throws FormatException.</summary>
    public static PlanTree Build(JsonNode? steps, IReadOnlyCollection<string> knownTools)
    {
        var tree = new PlanTree(new Seq());
        tree.Root = tree.ParseSeq(steps, knownTools, 1);
        tree.Root.Id = "s0";
        tree.stack.Clear();
        tree.stack.Add(new Frame { Node = tree.Root });
        if (tree.Count() > MaxNodes) throw new FormatException($"the plan is too big (more than {MaxNodes} nodes)");
        return tree;
    }

    /// <summary>The calls of a reply as a plain sequence (a reply without a "plan" call).</summary>
    public static PlanTree FromCalls(IEnumerable<ToolCall> calls)
    {
        var tree = new PlanTree(new Seq());
        tree.Root.Id = "s0";
        foreach (var c in calls) tree.Root.Items.Add(new Do { Call = c, Id = tree.NewId() });
        return tree;
    }

    string NewId() => "n" + nextId++;

    int Count() => All(Root).Count();

    static IEnumerable<Node> All(Node n) => new[] { n }.Concat(n.Children.SelectMany(All));

    Seq ParseSeq(JsonNode? node, IReadOnlyCollection<string> tools, int depth)
    {
        if (depth > MaxDepth) throw new FormatException($"the plan is nested too deep (more than {MaxDepth} levels)");
        var seq = new Seq { Id = "s" + nextSeq++ };
        if (node is JsonArray arr)
            foreach (var item in arr) seq.Items.Add(ParseNode(item, tools, depth));
        else if (node is JsonObject) seq.Items.Add(ParseNode(node, tools, depth));
        else if (node != null) throw new FormatException("a list of steps must be a JSON array of nodes");
        return seq;
    }

    Node ParseNode(JsonNode? node, IReadOnlyCollection<string> tools, int depth)
    {
        if (node is not JsonObject o) throw new FormatException("every step must be an object like {\"do\":\"equip\",\"args\":{...}}");
        Node result;
        if (Str(o, "if") is { } cond)
            result = new If { Cond = Cond(cond), Then = ParseSeq(o["then"], tools, depth + 1), Else = o["else"] != null ? ParseSeq(o["else"], tools, depth + 1) : null };
        else if (o["repeat"] == null && (Str(o, "wait_until") ?? Str(o, "until")) is { } until)
            result = new Wait { Cond = Cond(until), Timeout = Math.Clamp(Num(o, "timeout") ?? 60, 1, MaxWaitSeconds) };
        else if (o["repeat"] != null)
        {
            var w = Str(o, "while");
            var u = Str(o, "until") ?? (w != null ? $"not ({w})" : null);
            result = new Repeat
            {
                Body = ParseSeq(o["repeat"], tools, depth + 1),
                Until = u != null ? Cond(u) : null,
                Max = (int)Math.Clamp(Num(o, "max") ?? 3, 1, MaxRepeat),
            };
        }
        else if (o["try"] != null)
            result = new Try { Body = ParseSeq(o["try"], tools, depth + 1), Catch = o["catch"] != null ? ParseSeq(o["catch"], tools, depth + 1) : null };
        else if (o["steps"] != null || o["seq"] != null)
            result = ParseSeq(o["steps"] ?? o["seq"], tools, depth + 1);
        else
        {
            // {"do":"equip","args":{...}}, or the short form {"equip":{...}} / {"tool":"equip", ...}
            var name = Str(o, "do") ?? Str(o, "tool") ?? Str(o, "call");
            JsonObject args = o["args"] as JsonObject ?? o["arguments"] as JsonObject ?? new JsonObject();
            if (name == null && o.Count >= 1)
            {
                var first = o.First(kv => kv.Key != "id");
                name = first.Key;
                if (first.Value is JsonObject a) args = a;
            }
            if (name == null) throw new FormatException("a step needs \"do\": the tool to call");
            name = name.Trim();
            if (NotInPlans.Contains(name)) throw new FormatException($"'{name}' cannot be a step of a plan");
            if (!tools.Contains(name)) throw new FormatException($"unknown tool '{name}' in the plan");
            result = new Do { Call = new ToolCall(name, (JsonObject)args.DeepClone()) };
        }
        if (string.IsNullOrEmpty(result.Id)) result.Id = NewId();
        return result;
    }

    static PlanConditions.Condition Cond(string text)
    {
        try { return PlanConditions.Parse(text); }
        catch (FormatException e) { throw new FormatException($"condition \"{text}\": {e.Message}"); }
    }

    static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    static double? Num(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    // ---------------------------------------------------------------------------------------------- running

    /// <summary>The next thing to do. A step (Act) must be answered with <see cref="Report"/>, a wait with
    /// <see cref="Resume"/> or <see cref="Report"/>(false) on timeout.</summary>
    public Action Next(IConditionContext ctx)
    {
        for (int guard = 0; guard < 500; guard++)
        {
            if (stack.Count == 0) { CurrentId = null; return new DoneAction(); }
            var f = stack[^1];
            switch (f.Node)
            {
                case Seq s:
                    if (f.Index < s.Items.Count) Push(s.Items[f.Index++]);
                    else Pop();
                    break;
                case Do d:
                    Pop();
                    CurrentId = d.Id;
                    done.Add(d.Id);
                    return new ActAction(d.Call, d.Id);
                case If i:
                    if (f.Index == 0)
                    {
                        f.Index = 1;
                        if (i.Cond.IsTrue(ctx)) Push(i.Then);
                        else if (i.Else != null) Push(i.Else);
                    }
                    else Pop();
                    break;
                case Wait w:
                    if (w.Cond.IsTrue(ctx)) { Pop(); done.Add(w.Id); break; }
                    CurrentId = w.Id;
                    return new WaitAction(w);
                case Repeat r:
                    if ((r.Until != null && r.Until.IsTrue(ctx)) || f.Index >= r.Max) { Pop(); break; }
                    f.Index++;
                    Push(r.Body);
                    break;
                case Try t:
                    if (f.Index == 0) { f.Index = 1; Push(t.Body); }
                    else Pop();
                    break;
            }
        }
        return new FailedAction("the plan loops without doing anything");
    }

    void Push(Node n) => stack.Add(new Frame { Node = n });
    void Pop() => stack.RemoveAt(stack.Count - 1);

    /// <summary>How the last step (or wait) went. A failure unwinds to the nearest "try" (its "catch" runs);
    /// without one the whole plan has failed.</summary>
    /// <returns>null = go on; text = the plan failed (why).</returns>
    public string? Report(bool ok, string result)
    {
        LastOk = ok;
        LastResult = result;
        if (ok) return null;
        // a failed wait is still on the stack; a step was already popped
        while (stack.Count > 0)
        {
            var f = stack[^1];
            if (f.Node is Try t && f.Index == 1 && !f.InCatch)
            {
                f.InCatch = true;
                if (t.Catch != null) Push(t.Catch);
                return null;
            }
            Pop();
        }
        return result;
    }

    /// <summary>Some steps have not run yet (a plan whose last step failed has none).</summary>
    public bool HasPendingSteps => All(Root).Any(n => n is Do or Wait && !done.Contains(n.Id));

    /// <summary>The wait the plan is on (null = none).</summary>
    public Wait? CurrentWait => stack.Count > 0 ? stack[^1].Node as Wait : null;

    /// <summary>The awaited condition is true now: the wait is over and the plan moves on.</summary>
    public bool TryResume(IConditionContext ctx)
    {
        if (CurrentWait is not { } w || !w.Cond.IsTrue(ctx)) return false;
        done.Add(w.Id);
        Pop();
        return true;
    }

    /// <summary>Replaces one node (and what is under it) with new steps. If it is running, the new steps run instead.</summary>
    /// <returns>null = done; text = why not.</returns>
    public string? Replace(string id, JsonNode? steps, IReadOnlyCollection<string> knownTools)
    {
        var target = All(Root).FirstOrDefault(n => n.Id == id);
        if (target == null) return $"no node '{id}' in the plan; ids: {string.Join(", ", All(Root).Where(n => n is not Seq).Select(n => n.Id).Take(20))}";
        Seq repl;
        try { repl = ParseSeq(steps, knownTools, 2); }
        catch (FormatException e) { return e.Message; }
        if (target == Root)
        {
            Root = repl;
            stack.Clear();
            stack.Add(new Frame { Node = Root });
        }
        else
        {
            var parent = All(Root).First(n => n.Children.Contains(target));
            switch (parent)
            {
                case Seq s: s.Items[s.Items.IndexOf(target)] = repl; break;
                case If i when i.Then == target: i.Then = repl; break;
                case If i: i.Else = repl; break;
                case Repeat r: r.Body = repl; break;
                case Try t when t.Body == target: t.Body = repl; break;
                case Try t: t.Catch = repl; break;
            }
            // running: cut the stack back to it and run the new steps there
            int at = stack.FindIndex(fr => fr.Node == target);
            if (at >= 0)
            {
                stack.RemoveRange(at, stack.Count - at);
                stack.Add(new Frame { Node = repl });
            }
        }
        if (Count() > MaxNodes) return $"the plan is too big now (more than {MaxNodes} nodes)";
        return null;
    }

    // ---------------------------------------------------------------------------------------------- text for the model

    /// <summary>One line: the tree with ids, done steps marked (ok), the current one marked (NOW).</summary>
    public string Describe(int max = 700)
    {
        var sb = new StringBuilder();
        Write(sb, Root, top: true);
        var s = sb.ToString();
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    void Write(StringBuilder sb, Node n, bool top = false)
    {
        string mark(Node x) => x.Id == CurrentId ? " <NOW>" : done.Contains(x.Id) ? " (done)" : "";
        switch (n)
        {
            case Seq s:
                if (!top) sb.Append('[');
                for (int i = 0; i < s.Items.Count; i++)
                {
                    if (i > 0) sb.Append("; ");
                    Write(sb, s.Items[i]);
                }
                if (!top) sb.Append(']');
                break;
            case Do d:
                sb.Append($"{d.Id} {d.Call.Name}({Tools.ArgsToString(d.Call)}){mark(d)}");
                break;
            case If i:
                sb.Append($"{i.Id} if {i.Cond.Text} then ");
                Write(sb, i.Then);
                if (i.Else != null) { sb.Append(" else "); Write(sb, i.Else); }
                break;
            case Wait w:
                sb.Append($"{w.Id} wait until {w.Cond.Text} (max {w.Timeout:0}s){mark(w)}");
                break;
            case Repeat r:
                var it = stack.FirstOrDefault(f => f.Node == r)?.Index;
                sb.Append($"{r.Id} repeat{(r.Until != null ? " until " + r.Until.Text : "")} (max {r.Max}{(it is > 0 ? $", round {it}" : "")}) ");
                Write(sb, r.Body);
                break;
            case Try t:
                sb.Append($"{t.Id} try ");
                Write(sb, t.Body);
                if (t.Catch != null) { sb.Append(" catch "); Write(sb, t.Catch); }
                break;
        }
    }

    /// <summary>True when the tree has nothing but plain steps (no conditions): shown like an ordinary plan.</summary>
    public bool IsFlat => Root.Items.All(n => n is Do);
}
