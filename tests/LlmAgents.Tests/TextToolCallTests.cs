using System.Threading.Tasks;
using System.Linq;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Prompt mode executes explicit JSON decisions and refuses guesses from older loose reply formats.</summary>
public class TextToolCallTests
{
    static string Calls(string text) =>
        string.Join(" | ", TextToolCallParser.Parse(text, Tools.All).Select(c => $"{c.Name}({Tools.ArgsToString(c)})"));

    [Fact]
    public void StructuredCallsPreserveArgumentValuesAndTypes()
    {
        var calls = TextToolCallParser.Parse("{\"calls\":[{\"name\":\"craft\",\"arguments\":{\"item\":\"railgun shell\",\"amount\":2,\"salvage\":false}},{\"name\":\"say\",\"arguments\":{\"text\":\"I'm on it, don't worry\"}}]}", Tools.All);
        Assert.Equal(new[] { "craft", "say" }, calls.Select(c => c.Name));
        Assert.Equal("railgun shell", calls[0].Arguments["item"]!.GetValue<string>());
        Assert.Equal(2, calls[0].Arguments["amount"]!.GetValue<int>());
        Assert.False(calls[0].Arguments["salvage"]!.GetValue<bool>());
        Assert.Equal("I'm on it, don't worry", calls[1].Arguments["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("say{text:<|\"|>Есть<|\"|>}<tool_call|>")]
    [InlineData("call:go_to_room{room:<|\"|>Реакторный отсек [h163]<|\"|>}<tool_call|>")]
    [InlineData("sayЯ в Инженерном отсеке.\nmessage_crew{text:<|\"|>Привет<|\"|>,to:<|\"|>all<|\"|>}")]
    [InlineData("remember,\"note\":\"Miranda is fixing systems\"")]
    [InlineData("message_crew,\"all\",\"Everything is stable\"")]
    [InlineData("wait")]
    [InlineData("\"repairsystems\"")]
    [InlineData("\"go_to_room\" {\"room\":\"Инженерный отсек [h4]\"}")]
    [InlineData("say(\"Ready\") wait()")]
    [InlineData("Wait, the reactor is dropping. I should say something to the captain.")]
    [InlineData("Ok. {\"calls\":[{\"name\":\"operate\",\"arguments\":{\"role\":\"reactor\"}}]}")]
    [InlineData("```json\n{\"calls\":[{\"name\":\"wait\",\"arguments\":{}}]}\n```")]
    [InlineData("{\"calls\":[\"wait()\"]}")]
    [InlineData("{\"calls\":[{\"name\":\"fixleaks\",\"arguments\":{}}]}")]
    public void LegacySyntaxAndProseNeverBecomeCommands(string text) => Assert.Equal("", Calls(text));

    [Theory]
    [InlineData("{\"name\":\"attack\"}")]
    [InlineData("{\"name\":\"attack\",\"arguments\":\"broken\"}")]
    [InlineData("{\"name\":\"attack\",\"arguments\":[]}")]
    [InlineData("{\"name\":\"attack\",\"arguments\":null}")]
    [InlineData("{\"calls\":{},\"name\":\"attack\",\"arguments\":{}}")]
    [InlineData("{\"calls\":[],\"actions\":[{\"name\":\"attack\",\"arguments\":{}}]}")]
    [InlineData("{\"name\":\"say\",\"name\":\"attack\",\"arguments\":{}}")]
    [InlineData("{\"name\":\"say\",\"arguments\":{\"text\":\"hello\",\"text\":\"attack\"}}")]
    [InlineData("{\"name\":\"say\",\"arguments\":\"{\\\"text\\\":\\\"hello\\\",\\\"text\\\":\\\"attack\\\"}\"}")]
    [InlineData("{\"calls\":[{\"name\":\"say\",\"arguments\":{\"text\":\"hello\"}}, {\"name\":\"attack\"}]}")]
    [InlineData("{\"calls\":[{\"name\":\"say\",\"arguments\":{\"text\":\"hello\"}},null]}")]
    [InlineData("{\"calls\":[{\"name\":\"say\",\"arguments\":{\"text\":\"hello\"}}, {\"name\":\"go_to_room\",\"arguments\":{\"room\":\"Eng")]
    public void InvalidDecisionsDoNotExecuteFragmentsOrInventDefaultArguments(string text) => Assert.Equal("", Calls(text));

    [Fact]
    public void JsonArgumentTextIsNotSearchedForNestedCommands()
    {
        var text = "{\"name\":\"say\",\"arguments\":{\"text\":\"wait() {\\\"name\\\":\\\"attack\\\",\\\"arguments\\\":{}}\"}}";
        var call = Assert.Single(TextToolCallParser.Parse(text, Tools.All));
        Assert.Equal("say", call.Name);
        Assert.StartsWith("wait()", call.Arguments["text"]!.GetValue<string>());
    }

    [Fact]
    public void OnlyOfferedToolNamesAreAccepted()
    {
        var onlySay = Tools.Filtered(new[] { "say" });
        Assert.Empty(TextToolCallParser.Parse("{\"name\":\"wait\",\"arguments\":{}}", onlySay));
        Assert.Equal("say", Assert.Single(TextToolCallParser.Parse("{\"name\":\"say\",\"arguments\":{\"text\":\"hi\"}}", onlySay)).Name);
        Assert.Empty(TextToolCallParser.Parse("{\"name\":\"SAY\",\"arguments\":{\"text\":\"hi\"}}", onlySay));
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
        Assert.Contains("Tool name: operate", system);
        Assert.Contains("Arguments JSON Schema:", system);
        Assert.Contains("Station", system);
        Assert.Contains("\"reactor\"", system);
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
