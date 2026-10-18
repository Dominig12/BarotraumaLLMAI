using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LlmAgents.Agents;

namespace LlmAgents.Tests;

/// <summary>In-memory game: bots, observations and a log of every action the agents took.</summary>
public sealed class FakeGame : IGameApi
{
    public readonly List<BotHandle> Bots = new();
    public readonly Dictionary<int, Observation> Observations = new();
    public readonly ConcurrentQueue<string> Actions = new();
    public readonly HashSet<int> Dead = new();
    public int ObserveCalls;

    public FakeGame AddBot(int id, string name, string job, Action<Observation>? setup = null)
    {
        Bots.Add(new BotHandle(id, name, job));
        var o = new Observation
        {
            Name = name, Job = job, Room = "Engine Room [h10]",
            Rooms = { new RoomInfo("Engine Room [h10]", 0, 0), new RoomInfo("Command Room [h11]", 0, 0) },
        };
        setup?.Invoke(o);
        Observations[id] = o;
        return this;
    }

    public IReadOnlyList<BotHandle> GetBots() => Bots.Where(b => !Dead.Contains(b.Id)).ToList();
    public bool IsAlive(int botId) => !Dead.Contains(botId);

    public Observation Observe(int botId)
    {
        ObserveCalls++;
        var src = Observations[botId];
        // return a copy, like the real adapter builds a fresh one each time
        return new Observation
        {
            Name = src.Name, Job = src.Job, HealthPercent = src.HealthPercent, OxygenPercent = src.OxygenPercent,
            Room = src.Room, RoomWaterPercent = src.RoomWaterPercent, RoomFires = src.RoomFires,
            CurrentOrder = src.CurrentOrder, Doing = src.Doing, Rooms = src.Rooms.ToList(), Crew = src.Crew.ToList(),
            Gear = src.Gear, Submarine = src.Submarine, CurrentTask = src.CurrentTask,
            Threats = src.Threats.ToList(), Leaks = src.Leaks.ToList(), Inventory = src.Inventory.ToList(),
            Bodies = src.Bodies.ToList(),
            Equipped = src.Equipped.ToList(), Infestations = src.Infestations.ToList(), Marks = src.Marks.ToList(),
        };
    }

    string Act(int id, string what, string result = "ok")
    {
        Actions.Enqueue($"{id}:{what}");
        return result;
    }

    static string P(int priority) => priority == Tools.TopPriority ? "" : $" p{priority}";
    public string GoToRoom(int botId, string roomId, int priority = Tools.TopPriority) =>
        Observations[botId].Rooms.Any(r => r.Id == roomId) ? Act(botId, $"goto {roomId}{P(priority)}") : $"error: unknown room '{roomId}'";
    public string Follow(int botId, string targetName, int priority = Tools.TopPriority) => Act(botId, $"follow {targetName}{P(priority)}");
    public string ComeTo(int botId, string targetName) => Act(botId, $"come to {targetName}", "ok, started: come to " + targetName);
    public string EndTravel(int botId) => Act(botId, "end travel");
    /// <summary>What SimpleOrder answers (e.g. "ok (already doing that)").</summary>
    public string SimpleOrderResult = "ok";
    public string SimpleOrder(int botId, string orderKey, int priority = Tools.TopPriority) => Act(botId, orderKey + P(priority), SimpleOrderResult);
    public string Operate(int botId, string role, string? option, int priority = Tools.TopPriority) => Act(botId, $"operate {role} {option}".TrimEnd() + P(priority));
    public string SetOrderPriority(int botId, string order, int priority) => Act(botId, $"priority {order} {priority}");
    public string Dismiss(int botId) => Act(botId, "dismiss");
    public void Speak(int botId, string text) => Act(botId, $"say {text}");

    public readonly Dictionary<int, bool> Authority = new();
    public readonly List<GameReport> PendingReports = new();
    public void BeginCommand(int botId, bool playerAuthority) => Authority[botId] = playerAuthority;
    public ItemFilter? LastFilter;
    public string? LastDestination, LastMachine, LastTargetType;
    string Capture(ItemFilter? filter, string result) { LastFilter = filter; return result; }
    public string Equip(int botId, string item, ItemFilter? filter = null) => Capture(filter, Act(botId, $"equip {item}", "ok, started: equip " + item));
    public string RefuelReactor(int botId) => Act(botId, "refuel", "ok, started: refuel reactor");
    public string Craft(int botId, string item, int amount, bool salvage = false, string? machine = null) { LastMachine = machine; return Act(botId, $"craft {item} x{amount}{(salvage ? " salvage" : "")}", "ok, started: craft " + item); }
    public string Deconstruct(int botId, string item, string? from = null, int count = 0, ItemFilter? filter = null) =>
        Capture(filter, Act(botId, $"deconstruct {item}{(from != null ? " from " + from : "")}{(count > 0 ? " x" + count : "")}", "ok, started"));
    public string Treat(int botId, string target, string? item) => Act(botId, $"treat {target}{(item != null ? " with " + item : "")}", "ok, started");
    public string Unequip(int botId, string item, ItemFilter? filter = null) => Capture(filter, Act(botId, $"unequip {item}"));
    public string DropResult = "ok";
    public string Drop(int botId, string item, ItemFilter? filter = null) => Capture(filter, Act(botId, $"drop {item}", DropResult));
    public string MoveItem(int botId, string item, string to, int count = 0, string destination = "auto", ItemFilter? filter = null) { LastDestination = destination; return Capture(filter, Act(botId, $"move {item} -> {to}" + (count > 0 ? $" x{count}" : ""), "ok, started")); }
    public string GiveOrder(int botId, string orderId, string? option, string? target, int priority = Tools.TopPriority) =>
        Act(botId, $"order {orderId}" + (option != null ? ":" + option : "") + (target != null ? $" @{target}" : "") + $" [p{priority}]");
    public string PutIn(int botId, string item, string into, ItemFilter? filter = null) => Capture(filter, Act(botId, $"put {item} into {into}", "ok, started: put " + item));
    public string Refill(int botId, string? item, ItemFilter? filter = null) => Capture(filter, Act(botId, "refill" + (item != null ? " " + item : ""), "ok, started: refill"));
    public string TidyInventory(int botId, string? keep) => Act(botId, "tidy" + (keep != null ? " keep " + keep : ""), "ok, started: tidy");
    public string Attack(int botId, string target, string? weapon, string targetType = "character") { LastTargetType = targetType; return
        Act(botId, $"attack {target}" + (weapon != null ? $" with {weapon}" : ""), "ok, started: attack " + target); }
    public string DragBody(int botId, string target, string to) => Act(botId, $"drag {target} -> {to}", "ok, started: drag body");
    public string LootBody(int botId, string target, string? item = null, int count = 0, ItemFilter? filter = null) =>
        Capture(filter, Act(botId, $"loot {target}" + (item == null ? "" : " item " + item) + $" x{count}", "ok, started: loot body"));
    public string Unpack(int botId, string container, bool carry = true, ItemFilter? filter = null) =>
        Capture(filter, Act(botId, $"unpack {container}" + (carry ? "" : " (no carry)"), "ok, started: unpack " + container));
    public string TakeItem(int botId, string item, int count, string? from = null, ItemFilter? filter = null) =>
        Capture(filter, Act(botId, $"take {item} x{count}" + (from != null ? $" from {from}" : ""), "ok, started: take " + item));
    public string KeepGear(int botId, bool on) => Act(botId, $"keepgear {on}");
    public string Inspect(int botId, string kind, string query, ItemFilter? filter = null) => Capture(filter, $"{kind} {query}: " + new string('x', Tools.MaxInspectLength + 100));
    public readonly Dictionary<int, bool> Control = new();
    public readonly List<ControlEvent> PendingControl = new();
    public void SetAiControl(int botId, bool on) => Control[botId] = on;
    public IReadOnlyList<ControlEvent> DrainControlEvents()
    {
        var r = PendingControl.ToList();
        PendingControl.Clear();
        return r;
    }
    public readonly List<MenuOrder> PendingMenuOrders = new();
    public IReadOnlyList<MenuOrder> DrainMenuOrders()
    {
        var r = PendingMenuOrders.ToList();
        PendingMenuOrders.Clear();
        return r;
    }

    /// <summary>Private notes sent to players: (player, text).</summary>
    public readonly ConcurrentQueue<(string Player, string Text)> Notices = new();
    public void NotifyPlayer(string playerName, string text) => Notices.Enqueue((playerName, text));

    /// <summary>Makes DrainReports throw, like a broken game call.</summary>
    public bool BreakReports;

    public IReadOnlyList<GameReport> DrainReports()
    {
        if (BreakReports) throw new InvalidOperationException("the game is broken");
        var r = PendingReports.ToList();
        PendingReports.Clear();
        return r;
    }
}

/// <summary>Fake HTTP endpoint: a function from request JSON to response (status, body).</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    readonly Func<HttpRequestMessage, JsonObject, CancellationToken, Task<(HttpStatusCode, string)>> respond;
    public readonly ConcurrentQueue<(string Url, JsonObject Body, HttpRequestMessage Request)> Requests = new();

    public FakeHandler(Func<HttpRequestMessage, JsonObject, CancellationToken, Task<(HttpStatusCode, string)>> respond) => this.respond = respond;

    public FakeHandler(Func<HttpRequestMessage, JsonObject, Task<(HttpStatusCode, string)>> respond)
        : this((r, b, _) => respond(r, b)) { }

    public FakeHandler(Func<JsonObject, (HttpStatusCode, string)> respond)
        : this((_, body) => Task.FromResult(respond(body))) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var text = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : "{}";
        var body = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        Requests.Enqueue((request.RequestUri!.ToString(), body, request));
        var (code, payload) = await respond(request, body, ct);
        ct.ThrowIfCancellationRequested();
        return new HttpResponseMessage(code) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    }
}

public static class Fixtures
{
    /// <summary>The observation JSON of a bot's user message (an order header may come first).</summary>
    public static string Obs(string user)
    {
        const string marker = "Observation:\n";
        int i = user.IndexOf(marker, System.StringComparison.Ordinal);
        return i >= 0 ? user.Substring(i + marker.Length) : user;
    }

    /// <summary>A stand-in for the dispatcher model in tests: the roster names that appear in the message, else nobody.</summary>
    public static string DispatchByName(string prompt)
    {
        var message = prompt.Contains("Message from") ? prompt[prompt.IndexOf("Message from")..] : prompt;
        var quoted = message.Split('"').Skip(1).FirstOrDefault() ?? "";
        var names = prompt.Split('\n').Where(l => l.StartsWith("- ") && l.Contains(" | job: ")).Select(l => l[2..].Split(" | ")[0]).ToList();
        var text = Translit.ToLatin(quoted.ToLowerInvariant());
        var hit = names.Where(n => text.Contains(Translit.ToLatin(n.Split(' ')[0].ToLowerInvariant())[..3])).ToList();
        var arr = new System.Text.Json.Nodes.JsonArray(hit.Select(h => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(h)!).ToArray());
        return OpenAiToolResponse((ChatDispatcher.ToolName, new System.Text.Json.Nodes.JsonObject { ["recipients"] = arr }.ToJsonString()));
    }

    public static string OpenAiToolResponse(params (string Name, string ArgsJson)[] calls)
    {
        var tc = new JsonArray();
        int i = 0;
        foreach (var (name, args) in calls)
        {
            tc.Add(new JsonObject
            {
                ["id"] = $"call_{i++}",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args },
            });
        }
        return new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = null, ["tool_calls"] = tc },
                },
            },
            ["usage"] = new JsonObject { ["prompt_tokens"] = 120, ["completion_tokens"] = 15 },
        }.ToJsonString();
    }

    public static string OpenAiTextResponse(string text) => new JsonObject
    {
        ["choices"] = new JsonArray
        {
            new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text } },
        },
    }.ToJsonString();

    public static string AnthropicToolResponse(string name, string inputJson, string? text = null)
    {
        var content = new JsonArray();
        if (text != null) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = name, ["input"] = JsonNode.Parse(inputJson) });
        return new JsonObject
        {
            ["type"] = "message",
            ["role"] = "assistant",
            ["content"] = content,
            ["stop_reason"] = "tool_use",
            ["usage"] = new JsonObject { ["input_tokens"] = 200, ["output_tokens"] = 30 },
        }.ToJsonString();
    }

    /// <summary>Minimal valid config. Two OpenAI-style providers: "a" (default) and "b" (fallback).</summary>
    public static string Config(string? mutateGlobal = null, string? providersOverride = null, string extraRoot = "") => $@"{{
  ""defaultProvider"": ""a"",
  ""fallbackProvider"": ""b"",
  {extraRoot}
  ""global"": {{
    ""maxConcurrentRequests"": 10, ""requestsPerMinute"": 0, ""tickSeconds"": 0.5,
    ""decisionIntervalSeconds"": 10, ""minSecondsBetweenCallsPerBot"": 2, ""urgencyCheckSeconds"": 1,
    ""maxActionsPerDecision"": 3, ""historyTurns"": 3, ""maxNotes"": 2, ""logFile"": """", ""logPrompts"": false
    {mutateGlobal}
  }},
  ""providers"": {providersOverride ?? @"[
    { ""name"": ""a"", ""type"": ""openai"", ""baseUrl"": ""http://a.test/v1"", ""model"": ""m-a"", ""timeoutSeconds"": 5, ""maxConcurrent"": 4 },
    { ""name"": ""b"", ""type"": ""openai"", ""baseUrl"": ""http://b.test/v1"", ""model"": ""m-b"", ""timeoutSeconds"": 5, ""maxConcurrent"": 4 }
  ]"},
  ""bots"": {{ ""providerByJob"": {{}}, ""disabledJobs"": [], ""allowedTools"": [] }}
}}";

    public static ModSettings Settings(string? mutateGlobal = null, string? providers = null, string extraRoot = "") =>
        ModSettings.Parse(Config(mutateGlobal, providers, extraRoot));
}

/// <summary>Manually advanced clock for deterministic scheduling tests.</summary>
public sealed class FakeClock
{
    public double Now;
    public double Get() => Now;
    public void Advance(double seconds) => Now += seconds;
    static readonly DateTime Epoch = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    /// <summary>Same fake time as a UTC clock, for the router's limits and provider pauses.</summary>
    public DateTime Utc() => Epoch.AddSeconds(Now);
}
