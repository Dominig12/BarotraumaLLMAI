using System;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class NativeArgumentsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"incomplete\":")]
    public void InvalidNativeArgumentsCannotBecomeANoArgumentAction(string invalid)
    {
        Assert.Throws<FormatException>(() => OpenAiCompatibleProvider.ParseResponse(Fixtures.OpenAiToolResponse(("wait", invalid)), Tools.All));
        var ollama = new JsonObject { ["message"] = new JsonObject { ["tool_calls"] = new JsonArray(
            new JsonObject { ["function"] = new JsonObject { ["name"] = "wait", ["arguments"] = invalid } }) } };
        Assert.Throws<FormatException>(() => OllamaProvider.ParseResponse(ollama.ToJsonString(), Tools.All));
        var anthropic = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["name"] = "wait", ["input"] = invalid }) };
        Assert.Throws<FormatException>(() => AnthropicProvider.ParseResponse(anthropic.ToJsonString(), Tools.All));
    }

    [Fact]
    public void OneMalformedCallRejectsTheEntireNativeReply()
    {
        Assert.Throws<FormatException>(() => OpenAiCompatibleProvider.ParseResponse(
            Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Ready\"}"), ("wait", "[]")), Tools.All));
    }
}
