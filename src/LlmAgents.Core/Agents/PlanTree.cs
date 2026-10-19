using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>One operation in an atomic plan edit. Steps use the same syntax as a new plan.</summary>
public sealed record PlanEdit(string Op, string Node, JsonNode? Steps = null);
public sealed record PlanEditResult(bool Success, string? Error = null, bool InvalidatesCurrent = false);

/// <summary>A bounded, structured program, with explicit execution state and transactional edits.</summary>
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
        public double? TimeoutSeconds;
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
        public PlanConditions.Condition? While;
        public int Max;
        public override IEnumerable<Node> Children => new Node[] { Body };
    }
    public sealed class Try : Node
    {
        public Seq Body = null!;
        public Seq? Catch;
        public override IEnumerable<Node> Children => Catch != null ? new Node[] { Body, Catch } : new Node[] { Body };
    }

    public abstract record Action;
    public sealed record ActAction(ToolCall Call, string NodeId, double? TimeoutSeconds = null) : Action;
    public sealed record WaitAction(Wait Node) : Action;
    public sealed record DoneAction : Action;
    public sealed record FailedAction(string Reason) : Action;

    sealed class Frame
    {
        public Node Node = null!;
        public int Index;
        public bool InCatch;
    }

    public Seq Root { get; private set; }
    readonly List<Frame> stack = new();
    readonly Dictionary<string, string> statuses = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> iterations = new(StringComparer.Ordinal);
    int nextId = 1, nextSeq = 1;
    string? pendingAction;
    string? failure;

    public int Revision { get; private set; } = 1;
    public bool? LastOk { get; private set; }
    public string? LastResult { get; private set; }
    public string? CurrentId { get; private set; }

    PlanTree(Seq root)
    {
        Root = root;
        stack.Add(new Frame { Node = root });
    }

    static readonly HashSet<string> NotInPlans = new(StringComparer.Ordinal)
    {
        "plan", "update_plan", "edit_plan", "append_plan", "inspect_plan", "remove_plan", "clear_plan", "cancel_plan", "pause_plan", "resume_plan", "keep_plan", "carry_on",
    };

    public static PlanTree Build(JsonNode? steps, IReadOnlyCollection<string> knownTools)
    {
        var tree = new PlanTree(new Seq());
        tree.Root = tree.ParseSeq(steps, knownTools, 1);
        tree.Root.Id = "s0";
        tree.stack.Clear();
        tree.stack.Add(new Frame { Node = tree.Root });
        tree.ValidateTree(knownTools);
        return tree;
    }

    /// <summary>Plain replies retain per-step validation, including soft speech failures, in the same runtime.</summary>
    public static PlanTree FromCalls(IEnumerable<ToolCall> calls)
    {
        var tree = new PlanTree(new Seq { Id = "s0" });
        foreach (var c in calls)
            tree.Root.Items.Add(new Do { Call = new ToolCall(c.Name, (JsonObject)c.Arguments.DeepClone()), Id = tree.NewId() });
        tree.ValidateTree();
        return tree;
    }

    string NewId() => "n" + nextId++;
    static IEnumerable<Node> All(Node n) => new[] { n }.Concat(n.Children.SelectMany(All));

    Seq ParseSeq(JsonNode? node, IReadOnlyCollection<string> tools, int depth)
    {
        if (depth > MaxDepth) throw new FormatException($"the plan is nested too deep (more than {MaxDepth} levels)");
        if (node is not JsonArray arr) throw new FormatException("a list of steps must be a JSON array of nodes");
        var seq = new Seq { Id = "s" + nextSeq++ };
        foreach (var item in arr)
        {
            seq.Items.Add(ParseNode(item, tools, depth));
            if (All(seq).Take(MaxNodes + 1).Count() > MaxNodes)
                throw new FormatException($"the plan is too big (more than {MaxNodes} nodes)");
        }
        return seq;
    }

    Node ParseNode(JsonNode? node, IReadOnlyCollection<string> tools, int depth)
    {
        if (node is not JsonObject o) throw new FormatException("every step must be an object like {\"do\":\"equip\",\"args\":{...}}");
        Node result;
        if (o.ContainsKey("if"))
        {
            Keys(o, "if", "then", "else");
            result = new If
            {
                Cond = Cond(RequiredString(o, "if")),
                Then = ParseSeq(o["then"], tools, depth + 1),
                Else = o.ContainsKey("else") ? ParseSeq(o["else"], tools, depth + 1) : null,
            };
        }
        else if (o.ContainsKey("wait_until"))
        {
            Keys(o, "wait_until", "timeout");
            result = new Wait { Cond = Cond(RequiredString(o, "wait_until")), Timeout = Number(o, "timeout", 60, 1, MaxWaitSeconds) };
        }
        else if (o.ContainsKey("repeat"))
        {
            Keys(o, "repeat", "until", "while", "max");
            if (o.ContainsKey("until") && o.ContainsKey("while")) throw new FormatException("a repeat may specify either 'until' or 'while', not both");
            result = new Repeat
            {
                Body = ParseSeq(o["repeat"], tools, depth + 1),
                Until = o.ContainsKey("until") ? Cond(RequiredString(o, "until")) : null,
                While = o.ContainsKey("while") ? Cond(RequiredString(o, "while")) : null,
                Max = (int)Number(o, "max", 3, 1, MaxRepeat, integer: true),
            };
        }
        else if (o.ContainsKey("try"))
        {
            Keys(o, "try", "catch");
            result = new Try
            {
                Body = ParseSeq(o["try"], tools, depth + 1),
                Catch = o.ContainsKey("catch") ? ParseSeq(o["catch"], tools, depth + 1) : null,
            };
        }
        else if (o.ContainsKey("steps"))
        {
            Keys(o, "steps");
            result = ParseSeq(o["steps"], tools, depth + 1);
        }
        else
        {
            Keys(o, "do", "args", "timeout");
            var name = RequiredString(o, "do");
            if (NotInPlans.Contains(name)) throw new FormatException($"'{name}' cannot be a step of a plan");
            if (!tools.Contains(name)) throw new FormatException($"unknown tool '{name}' in the plan");
            var args = o.ContainsKey("args") ? o["args"] as JsonObject ?? throw new FormatException($"arguments for '{name}' must be a JSON object") : new JsonObject();
            var call = new ToolCall(name, (JsonObject)args.DeepClone());
            if (Tools.Def(name) != null && Tools.ValidateArguments(call) is { } error)
                throw new FormatException($"'{name}': {error}");
            result = new Do { Call = call, TimeoutSeconds = o.ContainsKey("timeout") ? Number(o, "timeout", 60, 1, 7200) : null };
        }
        if (result.Id.Length == 0) result.Id = NewId();
        return result;
    }

    static void Keys(JsonObject o, params string[] allowed)
    {
        foreach (var p in o)
            if (!allowed.Contains(p.Key, StringComparer.Ordinal)) throw new FormatException($"unexpected field '{p.Key}' in a plan node");
    }

    static string RequiredString(JsonObject o, string key) =>
        o[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim() : throw new FormatException($"'{key}' must be a nonempty string");

    static double Number(JsonObject o, string key, double fallback, double min, double max, bool integer = false)
    {
        if (!o.ContainsKey(key)) return fallback;
        if (o[key] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number ||
            !double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ||
            double.IsNaN(n) || double.IsInfinity(n) || n < min || n > max || (integer && n != Math.Truncate(n)))
            throw new FormatException($"'{key}' must be {(integer ? "an integer" : "a number")} from {min} to {max}");
        return n;
    }

    static PlanConditions.Condition Cond(string text)
    {
        try { return PlanConditions.Parse(text); }
        catch (FormatException e) { throw new FormatException($"condition \"{text}\": {e.Message}"); }
    }

    void ValidateTree(IReadOnlyCollection<string>? knownTools = null)
    {
        if (All(Root).Count() > MaxNodes) throw new FormatException($"the plan is too big (more than {MaxNodes} nodes)");
        void Depth(Node n, int level)
        {
            if (level > MaxDepth) throw new FormatException($"the plan is nested too deep (more than {MaxDepth} levels)");
            if (n is Seq s)
                foreach (var child in s.Items) Depth(child, child is Seq ? level + 1 : level);
            else foreach (var child in n.Children) Depth(child, level + 1);
        }
        Depth(Root, 1);
        foreach (var d in knownTools != null ? All(Root).OfType<Do>() : Enumerable.Empty<Do>())
        {
            if (knownTools != null && !knownTools.Contains(d.Call.Name)) throw new FormatException($"unknown tool '{d.Call.Name}' in the plan");
            if (Tools.Def(d.Call.Name) != null && Tools.ValidateArguments(d.Call) is { } error)
                throw new FormatException($"'{d.Call.Name}': {error}");
        }
    }

    // A current Do stays on the stack until Report so an edit cannot silently skip its replacement.
    public Action Next(IConditionContext ctx)
    {
        if (failure != null) return new FailedAction(failure);
        ctx = new ExecutionContext(this, ctx);
        for (int guard = 0; guard < 500; guard++)
        {
            if (stack.Count == 0) { CurrentId = null; return new DoneAction(); }
            var f = stack[^1];
            statuses[f.Node.Id] = "running";
            switch (f.Node)
            {
                case Seq s:
                    if (f.Index < s.Items.Count) Push(s.Items[f.Index++]);
                    else Pop("completed");
                    break;
                case Do d:
                    pendingAction = CurrentId = d.Id;
                    return new ActAction(d.Call, d.Id, d.TimeoutSeconds);
                case If i:
                    if (f.Index == 0)
                    {
                        f.Index = 1;
                        if (i.Cond.IsTrue(ctx))
                        {
                            if (i.Else != null) Mark(i.Else, "skipped");
                            Push(i.Then);
                        }
                        else
                        {
                            Mark(i.Then, "skipped");
                            if (i.Else != null) Push(i.Else);
                        }
                    }
                    else Pop("completed");
                    break;
                case Wait w:
                    if (w.Cond.IsTrue(ctx)) { CompleteWait(w); break; }
                    CurrentId = w.Id;
                    return new WaitAction(w);
                case Repeat r:
                    if ((r.Until != null && r.Until.IsTrue(ctx)) || (r.While != null && !r.While.IsTrue(ctx)) || f.Index >= r.Max)
                    {
                        if (f.Index == 0) Mark(r.Body, "skipped");
                        Pop("completed");
                        break;
                    }
                    f.Index++;
                    iterations[r.Id] = f.Index;
                    ClearStatuses(r.Body);
                    Push(r.Body);
                    break;
                case Try t:
                    if (f.Index == 0) { f.Index = 1; Push(t.Body); }
                    else
                    {
                        if (!f.InCatch && t.Catch != null) Mark(t.Catch, "skipped");
                        Pop("completed");
                    }
                    break;
            }
        }
        return new FailedAction("the plan loops without doing anything");
    }

    void Push(Node n) => stack.Add(new Frame { Node = n });
    void Pop(string status)
    {
        var id = stack[^1].Node.Id;
        statuses[id] = status;
        if (CurrentId == id) CurrentId = null;
        stack.RemoveAt(stack.Count - 1);
    }
    void Mark(Node n, string status) { foreach (var child in All(n)) statuses[child.Id] = status; }
    void ClearStatuses(Node n)
    {
        foreach (var child in All(n)) { statuses.Remove(child.Id); iterations.Remove(child.Id); }
    }

    // A satisfied immediate wait and its following branch can run in one Next call. Result predicates must see
    // that new outcome rather than the observation context's snapshot from the start of the call.
    sealed class ExecutionContext : IConditionContext
    {
        readonly PlanTree tree;
        readonly IConditionContext source;
        public ExecutionContext(PlanTree tree, IConditionContext source) { this.tree = tree; this.source = source; }
        public double Number(string name) => name switch
        {
            "last_ok" => tree.LastOk == true ? 1 : 0,
            "last_failed" => tree.LastOk == false ? 1 : 0,
            _ => source.Number(name),
        };
        public double Call(string name, string arg) => name == "result"
            ? tree.LastResult?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true ? 1 : 0
            : source.Call(name, arg);
    }

    void CompleteWait(Wait node)
    {
        LastOk = true;
        LastResult = "condition satisfied: " + node.Cond.Text;
        Pop("completed");
    }

    /// <summary>Completes the outstanding action/wait. Failures unwind to the nearest still-active catch.</summary>
    public string? Report(bool ok, string result)
    {
        LastOk = ok;
        LastResult = result;
        if (stack.Count > 0 && (stack[^1].Node.Id == pendingAction || stack[^1].Node is Wait)) Pop(ok ? "completed" : "failed");
        pendingAction = CurrentId = null;
        if (ok) return null;
        while (stack.Count > 0)
        {
            var f = stack[^1];
            if (f.Node is Try t && f.Index == 1 && !f.InCatch)
            {
                f.InCatch = true;
                if (t.Catch != null) Push(t.Catch);
                return null;
            }
            if (f.Node is Seq s)
                foreach (var rest in s.Items.Skip(f.Index)) Mark(rest, "skipped");
            Pop("failed");
        }
        failure = result;
        return result;
    }

    public bool HasPendingSteps => stack.Any(f => f.Node is Do or Wait ||
        f.Node is Seq s && f.Index < s.Items.Count || f.Node is Repeat r && f.Index < r.Max);
    /// <summary>Includes work skipped when a failed branch unwound, for an accurate cancellation report.</summary>
    public bool HasUnfinishedSteps => All(Root).Any(n => n is Do or Wait && Status(n) is "pending" or "skipped");
    public Wait? CurrentWait => stack.Count > 0 ? stack[^1].Node as Wait : null;

    public bool TryResume(IConditionContext ctx)
    {
        if (CurrentWait is not { } w || !w.Cond.IsTrue(new ExecutionContext(this, ctx))) return false;
        CompleteWait(w);
        return true;
    }

    /// <summary>A complete independent copy, including active frames, pending action and loop counters.</summary>
    public PlanTree Fork()
    {
        Node Copy(Node n) => n switch
        {
            Do d => new Do { Id = d.Id, Call = new ToolCall(d.Call.Name, (JsonObject)d.Call.Arguments.DeepClone()), TimeoutSeconds = d.TimeoutSeconds },
            Seq s => new Seq { Id = s.Id, Items = s.Items.Select(Copy).ToList() },
            If i => new If { Id = i.Id, Cond = i.Cond, Then = (Seq)Copy(i.Then), Else = i.Else != null ? (Seq)Copy(i.Else) : null },
            Wait w => new Wait { Id = w.Id, Cond = w.Cond, Timeout = w.Timeout },
            Repeat r => new Repeat { Id = r.Id, Body = (Seq)Copy(r.Body), Until = r.Until, While = r.While, Max = r.Max },
            Try t => new Try { Id = t.Id, Body = (Seq)Copy(t.Body), Catch = t.Catch != null ? (Seq)Copy(t.Catch) : null },
            _ => throw new InvalidOperationException("unknown plan node"),
        };
        var copy = new PlanTree((Seq)Copy(Root))
        {
            nextId = nextId, nextSeq = nextSeq, pendingAction = pendingAction, failure = failure,
            Revision = Revision, LastOk = LastOk, LastResult = LastResult, CurrentId = CurrentId,
        };
        copy.stack.Clear();
        var nodes = All(copy.Root).ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var frame in stack) copy.stack.Add(new Frame { Node = nodes[frame.Node.Id], Index = frame.Index, InCatch = frame.InCatch });
        foreach (var pair in statuses) copy.statuses[pair.Key] = pair.Value;
        foreach (var pair in iterations) copy.iterations[pair.Key] = pair.Value;
        return copy;
    }

    /// <summary>Validates every change on a copy, then commits tree and cursor together. Rejection changes nothing.</summary>
    public PlanEditResult Edit(IReadOnlyList<PlanEdit> edits, IReadOnlyCollection<string> knownTools, int? expectedRevision = null)
    {
        if (expectedRevision != null && expectedRevision != Revision)
            return new(false, $"plan revision is {Revision}, expected {expectedRevision}; inspect it again");
        if (edits.Count == 0) return new(false, "at least one edit is required");
        var candidate = Fork();
        bool invalidates = false;
        try
        {
            foreach (var edit in edits) invalidates |= candidate.Apply(edit, knownTools);
            candidate.ValidateTree(knownTools);
        }
        catch (FormatException e) { return new(false, e.Message); }
        Root = candidate.Root;
        stack.Clear();
        stack.AddRange(candidate.stack);
        statuses.Clear();
        foreach (var pair in candidate.statuses) statuses[pair.Key] = pair.Value;
        iterations.Clear();
        foreach (var pair in candidate.iterations) iterations[pair.Key] = pair.Value;
        nextId = candidate.nextId;
        nextSeq = candidate.nextSeq;
        pendingAction = candidate.pendingAction;
        CurrentId = candidate.CurrentId;
        failure = candidate.failure;
        Revision++;
        return new(true, InvalidatesCurrent: invalidates);
    }

    bool Apply(PlanEdit edit, IReadOnlyCollection<string> knownTools)
    {
        var target = All(Root).FirstOrDefault(n => n.Id == edit.Node)
            ?? throw new FormatException($"no node '{edit.Node}' in the plan; inspect the current node ids");
        if (edit.Op is not ("replace" or "remove" or "insert_before" or "insert_after" or "append" or "prepend"))
            throw new FormatException($"unknown plan edit operation '{edit.Op}'");
        if (edit.Op == "remove" && edit.Steps != null) throw new FormatException("'remove' does not accept steps");
        if (edit.Op != "remove" && edit.Steps == null) throw new FormatException($"'{edit.Op}' requires steps");
        if (target != Root && Status(target) is "completed" or "failed" or "skipped")
            throw new FormatException($"node '{target.Id}' already executed or was skipped; edit a pending node instead");

        var replacement = edit.Op == "remove" ? new Seq { Id = "s" + nextSeq++ } : ParseSeq(edit.Steps, knownTools, 1);
        if (edit.Op is "append" or "prepend" or "insert_before" or "insert_after" && replacement.Items.Count == 0)
            throw new FormatException($"'{edit.Op}' requires at least one new step");
        var parent = target == Root ? null : All(Root).First(n => n.Children.Contains(target));
        int activeAt = stack.FindIndex(f => f.Node == target);
        bool isCurrent = CurrentId != null && All(target).Any(n => n.Id == CurrentId);

        if (edit.Op is "append" or "prepend")
        {
            if (target is not Seq sequence) throw new FormatException($"'{edit.Op}' needs a sequence id such as s0 or a branch sequence");
            int index = sequence.Items.Count;
            if (edit.Op == "prepend")
            {
                index = activeAt >= 0 ? stack[activeAt].Index : 0;
                if (activeAt >= 0 && stack.Count > activeAt + 1) index--;
            }
            return Insert(sequence, index, replacement.Items);
        }
        if (edit.Op is "insert_before" or "insert_after")
        {
            if (parent is not Seq sequence) throw new FormatException("insert_before/insert_after need a node directly inside a sequence; append to the branch sequence instead");
            return Insert(sequence, sequence.Items.IndexOf(target) + (edit.Op == "insert_after" ? 1 : 0), replacement.Items);
        }

        if (target == Root)
        {
            replacement.Id = "s0";
            Root = replacement;
            stack.Clear();
            stack.Add(new Frame { Node = Root });
            statuses.Clear();
            iterations.Clear();
            pendingAction = CurrentId = failure = null;
            return isCurrent;
        }

        bool remove = edit.Op == "remove";
        // Required branches remain valid empty sequences when their contents are removed.
        if (remove && parent is not Seq) replacement.Id = target.Id;
        switch (parent)
        {
            case Seq sequence:
                int index = sequence.Items.IndexOf(target);
                var parentFrame = stack.FirstOrDefault(f => f.Node == parent);
                if (remove)
                {
                    sequence.Items.RemoveAt(index);
                    if (parentFrame != null && index < parentFrame.Index) parentFrame.Index--;
                }
                else
                {
                    // Splice ordinary steps into their existing sequence. Repeated edits must not add invisible nesting.
                    sequence.Items.RemoveAt(index);
                    sequence.Items.InsertRange(index, replacement.Items);
                    if (activeAt >= 0 && parentFrame != null) parentFrame.Index = index;
                }
                break;
            case If i when i.Then == target: i.Then = replacement; break;
            case If i: i.Else = replacement; break;
            case Repeat r: r.Body = replacement; break;
            case Try t when t.Body == target: t.Body = replacement; break;
            case Try t: t.Catch = replacement; break;
            default: throw new FormatException("cannot locate the node's parent");
        }
        ClearStatuses(target);
        if (activeAt >= 0)
        {
            stack.RemoveRange(activeAt, stack.Count - activeAt);
            if (parent is not Seq) Push(replacement);
        }
        if (isCurrent) pendingAction = CurrentId = null;
        return isCurrent;
    }

    bool Insert(Seq parent, int index, List<Node> nodes)
    {
        var parentAt = stack.FindIndex(f => f.Node == parent);
        bool invalidates = false;
        if (parentAt < 0 && parent == Root && Status(parent) == "completed")
        {
            if (index < parent.Items.Count) throw new FormatException("insertion would replay completed steps; append new work or replace the root");
            stack.Add(new Frame { Node = Root, Index = parent.Items.Count });
            statuses[Root.Id] = "running";
            parentAt = 0;
        }
        if (failure != null) throw new FormatException("the plan failed; replace the root to start new work");
        if (parentAt >= 0)
        {
            var frame = stack[parentAt];
            bool hasActiveChild = stack.Count > parentAt + 1;
            int activeIndex = frame.Index - (hasActiveChild ? 1 : 0);
            if (index < activeIndex) throw new FormatException("insertion would replay completed steps; append or target a pending step");
            if (hasActiveChild && index == activeIndex)
            {
                var activeChild = stack[parentAt + 1].Node;
                if (activeChild is not (Do or Wait))
                    throw new FormatException("cannot insert before a running control branch; append to its active sequence or target a pending step");
                invalidates = CurrentId != null;
                stack.RemoveRange(parentAt + 1, stack.Count - parentAt - 1);
                frame.Index = index;
                pendingAction = CurrentId = null;
                statuses.Remove(activeChild.Id);
            }
        }
        parent.Items.InsertRange(index, nodes);
        return invalidates;
    }

    public string? Replace(string id, JsonNode? steps, IReadOnlyCollection<string> knownTools) =>
        Edit(new[] { new PlanEdit("replace", id, steps) }, knownTools).Error;

    string Status(Node n) => statuses.TryGetValue(n.Id, out var value) ? value : "pending";

    /// <summary>Lossless inspection: all ids, arguments, branch sequences and current loop counters.</summary>
    public JsonObject Snapshot()
    {
        JsonObject NodeJson(Node node)
        {
            var o = new JsonObject { ["id"] = node.Id, ["status"] = Status(node) };
            switch (node)
            {
                case Seq s: o["steps"] = new JsonArray(s.Items.Select(n => (JsonNode?)NodeJson(n)).ToArray()); break;
                case Do d:
                    o["do"] = d.Call.Name; o["args"] = d.Call.Arguments.DeepClone();
                    if (d.TimeoutSeconds != null) o["timeout"] = d.TimeoutSeconds;
                    break;
                case If i:
                    o["if"] = i.Cond.Text; o["then"] = NodeJson(i.Then);
                    if (i.Else != null) o["else"] = NodeJson(i.Else);
                    break;
                case Wait w: o["wait_until"] = w.Cond.Text; o["timeout"] = w.Timeout; break;
                case Repeat r:
                    o["repeat"] = NodeJson(r.Body); o["max"] = r.Max;
                    o["iterations_started"] = iterations.TryGetValue(r.Id, out var count) ? count : 0;
                    if (r.Until != null) o["until"] = r.Until.Text;
                    if (r.While != null) o["while"] = r.While.Text;
                    break;
                case Try t:
                    o["try"] = NodeJson(t.Body);
                    if (t.Catch != null) o["catch"] = NodeJson(t.Catch);
                    break;
            }
            return o;
        }
        return new JsonObject
        {
            ["revision"] = Revision, ["current"] = CurrentId, ["status"] = failure != null ? "failed" : stack.Count == 0 ? "completed" : "running",
            ["last_ok"] = LastOk, ["last_result"] = LastResult, ["failure"] = failure, ["root"] = NodeJson(Root),
        };
    }

    public string Describe(int max = 700)
    {
        var sb = new StringBuilder();
        Write(sb, Root, top: true);
        var text = sb.ToString();
        return text.Length <= max ? text : text.Substring(0, max) + "...";
    }

    void Write(StringBuilder sb, Node n, bool top = false)
    {
        string MarkText(Node x) => x.Id == CurrentId ? " <NOW>" : Status(x) == "completed" ? " (done)" : "";
        switch (n)
        {
            case Seq s:
                sb.Append(s.Id).Append(top ? ": " : " [");
                for (int i = 0; i < s.Items.Count; i++) { if (i > 0) sb.Append("; "); Write(sb, s.Items[i]); }
                if (!top) sb.Append(']');
                break;
            case Do d: sb.Append($"{d.Id} {d.Call.Name}({Tools.ArgsToString(d.Call)}){MarkText(d)}"); break;
            case If i:
                sb.Append($"{i.Id} if {i.Cond.Text} then "); Write(sb, i.Then);
                if (i.Else != null) { sb.Append(" else "); Write(sb, i.Else); }
                break;
            case Wait w: sb.Append($"{w.Id} wait until {w.Cond.Text} (max {w.Timeout:0}s){MarkText(w)}"); break;
            case Repeat r:
                var iteration = stack.FirstOrDefault(f => f.Node == r)?.Index;
                sb.Append($"{r.Id} repeat{(r.Until != null ? " until " + r.Until.Text : r.While != null ? " while " + r.While.Text : "")} (max {r.Max}{(iteration is > 0 ? $", round {iteration}" : "")}) ");
                Write(sb, r.Body);
                break;
            case Try t:
                sb.Append($"{t.Id} try "); Write(sb, t.Body);
                if (t.Catch != null) { sb.Append(" catch "); Write(sb, t.Catch); }
                break;
        }
    }

    public bool IsFlat => Root.Items.All(n => n is Do);
}
