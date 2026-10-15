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
            CurrentOrder = src.CurrentOrder, Rooms = src.Rooms.ToList(), Crew = src.Crew.ToList(),
            Threats = src.Threats.ToList(), Leaks = src.Leaks.ToList(), Inventory = src.Inventory.ToList(),
        };
    }

    string Act(int id, string what, string result = "ok")
    {
        Actions.Enqueue($"{id}:{what}");
        return result;
    }

    public string GoToRoom(int botId, string roomId) =>
        Observations[botId].Rooms.Any(r => r.Id == roomId) ? Act(botId, $"goto {roomId}") : $"error: unknown room '{roomId}'";
    public string Follow(int botId, string targetName) => Act(botId, $"follow {targetName}");
    public string SimpleOrder(int botId, string orderKey) => Act(botId, orderKey);
    public string Operate(int botId, string role, string? option) => Act(botId, $"operate {role} {option}".TrimEnd());
    public string Dismiss(int botId) => Act(botId, "dismiss");
    public void Speak(int botId, string text) => Act(botId, $"say {text}");

    public readonly Dictionary<int, bool> Authority = new();
    public readonly List<GameReport> PendingReports = new();
    public void BeginCommand(int botId, bool playerAuthority) => Authority[botId] = playerAuthority;
    public string Equip(int botId, string item) => Act(botId, $"equip {item}", "ok, started: equip " + item);
    public string RefuelReactor(int botId) => Act(botId, "refuel", "ok, started: refuel reactor");
    public string Craft(int botId, string item, int amount) => Act(botId, $"craft {item} x{amount}", "ok, started: craft " + item);
    public string Deconstruct(int botId, string item) => Act(botId, $"deconstruct {item}", "ok, started");
    public string Treat(int botId, string target, string? item) => Act(botId, $"treat {target}{(item != null ? " with " + item : "")}", "ok, started");
    public string Inspect(int botId, string kind, string query) => $"{kind} {query}: " + new string('x', 2000);
    public IReadOnlyList<GameReport> DrainReports()
    {
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
