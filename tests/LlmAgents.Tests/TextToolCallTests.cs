using System.Threading.Tasks;
using System.Linq;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Real replies from a local Gemma model (llm_agents.log, 2026-10-15) that came back as text, not tool calls.</summary>
public class TextToolCallTests
{
    static string Calls(string text) =>
        string.Join(" | ", TextToolCallParser.Parse(text, Tools.All).Select(c => $"{c.Name}({Tools.ArgsToString(c)})"));

    [Fact]
    public void GemmaSay()
    {
        Assert.Equal("say(text=\"Принято. Сейчас вхожу в Инженерный отсек, чтобы посмотреть, как там дела с ремонтом.\")",
            Calls("say{text:<|\"|>Принято. Сейчас вхожу в Инженерный отсек, чтобы посмотреть, как там дела с ремонтом.<|\"|>}<tool_call|>"));
    }

    [Fact]
    public void GemmaGoToRoomAndFollow()
    {
        Assert.Equal("go_to_room(room=\"Инженерный отсек [h4]\")", Calls("go_to_room{room:<|\"|>Инженерный отсек [h4]<|\"|>}<tool_call|>"));
        Assert.Equal("follow(target=\"Miranda Dimauro\")", Calls("follow{target:<|\"|>Miranda Dimauro<|\"|>}<tool_call|>"));
        Assert.Equal("go_to_room(room=\"Реакторный отсек [h163]\")", Calls("call:go_to_room{room:<|\"|>Реакторный отсек [h163]<|\"|>}<tool_call|>"));
    }

    [Fact]
    public void GemmaTwoCallsWithUnquotedSayFirst()
    {
        var text = "sayЯ в Инженерном отсеке. Miranda уже там, занимается ремонтом. Похоже, реактор падает дальше...\n\n" +
                   "message_crew{text:<|\"|>Всем привет! Я в Инженерном отсеке (h4).<|\"|>,to:<|\"|>all<|\"|>}<tool_call|>";
        var calls = TextToolCallParser.Parse(text, Tools.All);
        Assert.Equal(new[] { "say", "message_crew" }, calls.Select(c => c.Name));
        Assert.StartsWith("Я в Инженерном отсеке.", Tools.Str(calls[0].Arguments, "text"));
        Assert.Equal("all", Tools.Str(calls[1].Arguments, "to"));
        Assert.Equal("Всем привет! Я в Инженерном отсеке (h4).", Tools.Str(calls[1].Arguments, "text"));
    }

    [Fact]
    public void GemmaRemember()
    {
        Assert.Equal("remember(note=\"Miranda Dimauro (mechanic) is in Reactor Room [h163] working on repairs.\")",
            Calls("remember{note:<|\"|>Miranda Dimauro (mechanic) is in Reactor Room [h163] working on repairs.<|\"|>}<tool_call|>"));
    }

    [Fact]
    public void CommaStyleWithKeys()
    {
        var text = "remember,\"note\":\"Miranda Dimauro is fixing systems in the Living Quarters [h146].\"\n" +
                   "message_crew,\"to\":\"all\",\"text\":\"All clear for now, but keeping an eye on things.\"";
        Assert.Equal("remember(note=\"Miranda Dimauro is fixing systems in the Living Quarters [h146].\") | " +
                     "message_crew(to=\"all\", text=\"All clear for now, but keeping an eye on things.\")", Calls(text));
    }

    [Fact]
    public void CommaStylePositional()
    {
        var text = "remember,\"Miranda Dimauro is currently repairing systems in the Central Access Shaft [h138].\"\n" +
                   "message_crew,\"all\",\"Everything looks stable for now.\"\n" +
                   "go_to_room,\"Пункт управления [h55]\"";
        Assert.Equal("remember(note=\"Miranda Dimauro is currently repairing systems in the Central Access Shaft [h138].\") | " +
                     "message_crew(to=\"all\", text=\"Everything looks stable for now.\") | " +
                     "go_to_room(room=\"Пункт управления [h55]\")", Calls(text));
    }

    [Fact]
    public void BareNameAndVanillaOrderAlias()
    {
        Assert.Equal("wait()", Calls("wait"));
        Assert.Equal("repair_systems()", Calls("//s:[<|\"|>Need to keep these systems running! Reactor's dropping fast.<|\"|>], \"repairsystems\""));
    }

    [Fact]
    public void QuotedNameWithJsonArgs()
    {
        Assert.Equal("go_to_room(room=\"Инженерный отсек [h4]\")",
            Calls("//g:[<|\"|>Alright, I'm in the living quarters now.<|\"|>], \"go_to_room\" { \"room\": \"Инженерный отсек [h4]\" }"));
    }

    [Theory]
    [InlineData("// Okay, I'm in the Central Passage now. Systems repair is still my priority.")]
    [InlineData("Wait, the reactor is dropping. I should say something to the captain.")]
    [InlineData("I will follow Miranda and wait there.")]
    [InlineData("say")]                       // required text missing
    [InlineData("go_to_room")]
    public void ProseIsNotACall(string text)
    {
        Assert.Equal("", Calls(text));
    }

    [Fact]
    public void JsonStillPreferred()
    {
        Assert.Equal("operate(role=\"reactor\")", Calls("Ok. {\"calls\":[{\"name\":\"operate\",\"arguments\":{\"role\":\"reactor\"}}]}"));
        Assert.Equal("fix_leaks() | say(text=\"go\")", Calls("[{\"name\":\"fixleaks\"},{\"name\":\"say\",\"arguments\":{\"text\":\"go\"}}]"));
    }

    [Fact]
    public void OnlyAllowedToolsAreParsed()
    {
        var onlySay = Tools.Filtered(new[] { "say" });
        Assert.Empty(TextToolCallParser.Parse("wait", onlySay));
        Assert.Single(TextToolCallParser.Parse("say{text:\"hi\"}", onlySay));
    }
}

public class ProviderHealthAndModesTests
{
    static readonly LlmTurn[] Turns = { new("user", "{}") };

    [Fact]
    public async Task ForbiddenProviderIsPausedAndFallbackIsUsedDirectly()
    {
        var now = new System.DateTime(2026, 10, 15, 8, 0, 0, System.DateTimeKind.Utc);
        var h = new FakeHandler((req, _) => System.Threading.Tasks.Task.FromResult(req.RequestUri!.Host == "a.test"
            ? (System.Net.HttpStatusCode.Forbidden, "{\n \"error\": {\"type\": \"forbidden\", \"message\": \"Request not allowed\"}\n}")
            : (System.Net.HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")))));
        using var router = new LlmRouter(Fixtures.Settings(), h, () => now);

        var r1 = await router.CompleteAsync("a", "s", Turns, Tools.All, default);
        Assert.Equal("b", r1.Provider);                 // 403 -> fallback in the same request
        Assert.Equal(2, h.Requests.Count);
        Assert.InRange(router.PausedFor("a"), 299, 300);

        var r2 = await router.CompleteAsync("a", "s", Turns, Tools.All, default);
        Assert.Equal("b", r2.Provider);                 // 'a' is not even tried while paused
        Assert.Equal(3, h.Requests.Count);

        now = now.AddMinutes(6);
        await router.CompleteAsync("a", "s", Turns, Tools.All, default);
        Assert.Contains(h.Requests, x => x.Url.Contains("a.test") && h.Requests.Count == 5);
    }

    [Fact]
    public async Task TransientErrorsBackOffAndRecover()
    {
        var now = new System.DateTime(2026, 10, 15, 8, 0, 0, System.DateTimeKind.Utc);
        bool down = true;
        var settings = ModSettings.Parse(Fixtures.Config().Replace("\"fallbackProvider\": \"b\"", "\"fallbackProvider\": \"\""));
        var h = new FakeHandler(_ => down ? (System.Net.HttpStatusCode.ServiceUnavailable, "busy") : (System.Net.HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        using var router = new LlmRouter(settings, h, () => now);

        Assert.Equal(RouteStatus.Failed, (await router.CompleteAsync("a", "s", Turns, Tools.All, default)).Status);
        Assert.InRange(router.PausedFor("a"), 4.9, 5);
        Assert.Equal(RouteStatus.Limited, (await router.CompleteAsync("a", "s", Turns, Tools.All, default)).Status);
        now = now.AddSeconds(6);
        Assert.Equal(RouteStatus.Failed, (await router.CompleteAsync("a", "s", Turns, Tools.All, default)).Status);
        Assert.InRange(router.PausedFor("a"), 9.9, 10);   // doubled
        now = now.AddSeconds(11);
        down = false;
        Assert.Equal(RouteStatus.Ok, (await router.CompleteAsync("a", "s", Turns, Tools.All, default)).Status);
        Assert.Equal(0, router.PausedFor("a"));
    }

    [Fact]
    public void PromptModeDescribesToolsAndAsksForJson()
    {
        var cfg = new ProviderConfig { Name = "x", Model = "gemma", ToolMode = "prompt" };
        var req = new LlmRequest("SYS", Turns, Tools.All, 100, 0.5);
        var body = OpenAiCompatibleProvider.BuildBody(cfg, req);
        Assert.Null(body["tools"]);
        Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
        var system = body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.StartsWith("SYS", system);
        Assert.Contains("- operate(role: string one of reactor|navigation|weapons|pumps, option: string (optional), priority: integer (optional))", system);
        Assert.Contains("{\"calls\": [", system);

        var anth = AnthropicProvider.BuildBody(cfg, req);
        Assert.Null(anth["tools"]);
        Assert.Contains("fix_leaks", anth["system"]![0]!["text"]!.GetValue<string>());

        var native = OpenAiCompatibleProvider.BuildBody(new ProviderConfig { Name = "y", Model = "m" }, req);
        Assert.NotNull(native["tools"]);
        Assert.Null(native["response_format"]);
    }

    [Fact]
    public void PromptModeReplyIsParsed()
    {
        var r = OpenAiCompatibleProvider.ParseResponse(Fixtures.OpenAiTextResponse(
            "{\"calls\": [{\"name\": \"say\", \"arguments\": {\"text\": \"Иду!\"}}, {\"name\": \"follow\", \"arguments\": {\"target\": \"Kim Reeves\"}}]}"), Tools.All);
        Assert.Equal(new[] { "say", "follow" }, r.ToolCalls.Select(c => c.Name));
    }

    [Fact]
    public void InvalidToolModeRejected()
    {
        var json = Fixtures.Config().Replace("\"model\": \"m-a\",", "\"model\": \"m-a\", \"toolMode\": \"magic\",");
        Assert.Throws<System.IO.InvalidDataException>(() => ModSettings.Parse(json));
        Assert.Equal("prompt", ModSettings.Parse(Fixtures.Config().Replace("\"model\": \"m-a\",", "\"model\": \"m-a\", \"toolMode\": \"Prompt\",")).Providers[0].ToolMode);
    }
}

public class JsonFormatFallbackTests
{
    [Fact]
    public async Task RetriesWithoutResponseFormatWhenServerRejectsIt()
    {
        var h = new FakeHandler(body => body.ContainsKey("response_format")
            ? (System.Net.HttpStatusCode.BadRequest, "{\"error\":\"'response_format.type' must be 'json_schema' or 'text'\"}")
            : (System.Net.HttpStatusCode.OK, Fixtures.OpenAiTextResponse("{\"calls\":[{\"name\":\"wait\",\"arguments\":{}}]}")));
        using var http = new System.Net.Http.HttpClient(h);
        var cfg = new ProviderConfig { Name = "lms", BaseUrl = "http://lms/v1", Model = "gemma", ToolMode = "prompt", TimeoutSeconds = 5 };
        var p = new OpenAiCompatibleProvider(cfg, http);
        var req = new LlmRequest("s", new[] { new LlmTurn("user", "{}") }, Tools.All, 50, 0.5);

        Assert.Equal("wait", (await p.CompleteAsync(req, default)).ToolCalls.Single().Name);
        Assert.Equal(2, h.Requests.Count);
        await p.CompleteAsync(req, default);
        Assert.Equal(3, h.Requests.Count);           // remembered: no second failed attempt
        Assert.False(h.Requests.Last().Body.ContainsKey("response_format"));
    }
}
