using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class ProviderTests
{
    static readonly IReadOnlyList<ToolSpec> Known = Tools.All;

    static LlmRequest Request() => new(
        "sys",
        new[] { new LlmTurn("user", "obs1"), new LlmTurn("assistant", "fix_leaks()"), new LlmTurn("user", "obs2") },
        Tools.All, 123, 0.4);

    [Fact]
    public void OpenAiBodyShape()
    {
        var cfg = new ProviderConfig { Name = "x", Model = "qwen" };
        var body = OpenAiCompatibleProvider.BuildBody(cfg, Request());
        Assert.Equal("qwen", body["model"]!.GetValue<string>());
        var msgs = body["messages"]!.AsArray();
        Assert.Equal(4, msgs.Count);
        Assert.Equal("system", msgs[0]!["role"]!.GetValue<string>());
        Assert.Equal("obs2", msgs[3]!["content"]!.GetValue<string>());
        var tools = body["tools"]!.AsArray();
        Assert.Equal(Tools.All.Count, tools.Count);
        Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal("object", tools[0]!["function"]!["parameters"]!["type"]!.GetValue<string>());
        Assert.Equal(123, body["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void OpenAiParsesStringArguments()
    {
        var r = OpenAiCompatibleProvider.ParseResponse(
            Fixtures.OpenAiToolResponse(("go_to_room", "{\"room\":\"Engine Room [h10]\"}"), ("fix_leaks", "")), Known);
        Assert.Equal(2, r.ToolCalls.Count);
        Assert.Equal("Engine Room [h10]", r.ToolCalls[0].Arguments["room"]!.GetValue<string>());
        Assert.Empty(r.ToolCalls[1].Arguments);
        Assert.Equal(120, r.InputTokens);
        Assert.Equal(15, r.OutputTokens);
    }

    [Fact]
    public void OpenAiParsesObjectArgumentsLikeOllama()
    {
        var raw = """
        {"choices":[{"message":{"role":"assistant","content":"","tool_calls":[
          {"function":{"name":"say","arguments":{"text":"Течь в машинном!"}}}]}}],
         "usage":{"prompt_tokens":10.0,"completion_tokens":2}}
        """;
        var r = OpenAiCompatibleProvider.ParseResponse(raw, Known);
        Assert.Equal("Течь в машинном!", Tools.Str(r.ToolCalls.Single().Arguments, "text"));
        Assert.Equal(10, r.InputTokens);
    }

    [Fact]
    public void OpenAiFallsBackToToolCallsWrittenAsText()
    {
        var r = OpenAiCompatibleProvider.ParseResponse(Fixtures.OpenAiTextResponse(
            "I'll weld it. ```json\n{\"name\": \"fix_leaks\", \"arguments\": {}}\n```"), Known);
        Assert.Equal("fix_leaks", r.ToolCalls.Single().Name);
    }

    [Fact]
    public void OpenAiErrorObjectThrows()
    {
        Assert.Throws<FormatException>(() => OpenAiCompatibleProvider.ParseResponse("{\"error\":{\"message\":\"model not found\"}}", Known));
    }

    [Fact]
    public void AnthropicBodyAndParse()
    {
        var cfg = new ProviderConfig { Name = "c", Model = "claude" };
        var body = AnthropicProvider.BuildBody(cfg, Request());
        Assert.Equal("sys", body["system"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(3, body["messages"]!.AsArray().Count);
        Assert.NotNull(body["tools"]![0]!["input_schema"]);

        var r = AnthropicProvider.ParseResponse(Fixtures.AnthropicToolResponse("operate", "{\"role\":\"reactor\"}", "On it."), Known);
        Assert.Equal("On it.", r.Text);
        Assert.Equal("reactor", Tools.Str(r.ToolCalls.Single().Arguments, "role"));
        Assert.Equal(230, r.InputTokens + r.OutputTokens);
        Assert.Throws<FormatException>(() => AnthropicProvider.ParseResponse("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}", Known));
    }

    [Fact]
    public void AnthropicTurnsAreAlternatingAndStartWithUser()
    {
        var turns = AnthropicProvider.NormalizeTurns(new[]
        {
            new LlmTurn("assistant", "orphan"),
            new LlmTurn("user", "a"),
            new LlmTurn("user", "b"),
            new LlmTurn("assistant", "c"),
        });
        Assert.Equal(new[] { "user", "assistant" }, turns.Select(t => t.Role));
        Assert.Equal("a\nb", turns[0].Text);
    }

    [Fact]
    public async Task OpenAiHttpRoundTripSendsAuthAndHeaders()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        using var http = new HttpClient(handler);
        var cfg = new ProviderConfig
        {
            Name = "x", BaseUrl = "http://host:1234/v1/", Model = "m", ApiKey = "k1", TimeoutSeconds = 5,
            ExtraHeaders = new() { ["HTTP-Referer"] = "barotrauma" },
        };
        var r = await new OpenAiCompatibleProvider(cfg, http).CompleteAsync(Request(), CancellationToken.None);
        Assert.Equal("wait", r.ToolCalls.Single().Name);
        var (url, _, req) = handler.Requests.Single();
        Assert.Equal("http://host:1234/v1/chat/completions", url);
        Assert.Equal("Bearer k1", req.Headers.Authorization!.ToString());
        Assert.True(req.Headers.Contains("HTTP-Referer"));
    }

    [Fact]
    public async Task AnthropicHttpRoundTripSendsVersionAndKey()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.AnthropicToolResponse("wait", "{}")));
        using var http = new HttpClient(handler);
        var cfg = new ProviderConfig { Name = "c", BaseUrl = "https://api.example.com", Model = "m", ApiKey = "k2", TimeoutSeconds = 5 };
        await new AnthropicProvider(cfg, http).CompleteAsync(Request(), CancellationToken.None);
        var (url, _, req) = handler.Requests.Single();
        Assert.Equal("https://api.example.com/v1/messages", url);
        Assert.Equal("k2", req.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicProvider.ApiVersion, req.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task HttpErrorAndTimeoutAreReported()
    {
        using var err = new HttpClient(new FakeHandler(_ => (HttpStatusCode.TooManyRequests, "{\"error\":\"slow down\"}")));
        var cfg = new ProviderConfig { Name = "x", BaseUrl = "http://h/v1", Model = "m", TimeoutSeconds = 5 };
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => new OpenAiCompatibleProvider(cfg, err).CompleteAsync(Request(), CancellationToken.None));
        Assert.Contains("429", e.Message);

        using var slow = new HttpClient(new FakeHandler(async (_, _, ct) =>
        {
            await Task.Delay(3000, ct);
            return (HttpStatusCode.OK, "{}");
        }));
        cfg.TimeoutSeconds = 0.2;
        await Assert.ThrowsAsync<TimeoutException>(() => new OpenAiCompatibleProvider(cfg, slow).CompleteAsync(Request(), CancellationToken.None));
    }
}

public class TextToolCallParserTests
{
    static readonly IReadOnlyList<ToolSpec> Known = Tools.All;

    [Theory]
    [InlineData("{\"name\":\"fix_leaks\",\"arguments\":{}}", "fix_leaks")]
    [InlineData("Sure! [{\"name\":\"say\",\"arguments\":{\"text\":\"hi }\"}}]", "say")]
    [InlineData("{\"tool_calls\":[{\"function\":{\"name\":\"wait\",\"arguments\":\"{}\"}}]}", "wait")]
    [InlineData("{\"tool\":\"operate\",\"parameters\":{\"role\":\"reactor\"}}", "operate")]
    public void Extracts(string text, string expected)
    {
        Assert.Equal(expected, TextToolCallParser.Parse(text, Known).Single().Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I will fix the leaks now.")]
    [InlineData("{\"name\":\"rm_rf\",\"arguments\":{}}")]
    [InlineData("{broken json")]
    public void IgnoresNonCalls(string text)
    {
        Assert.Empty(TextToolCallParser.Parse(text, Known));
    }
}
