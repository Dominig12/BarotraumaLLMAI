using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class OllamaThinkingTests
{
    static readonly LlmTurn[] Turns = { new("user", "{\"name\":\"Ann\"}") };

    static ProviderConfig Cfg(string thinking = "off", int ctx = 12288, string mode = "native", string url = "http://127.0.0.1:11434/v1") =>
        new() { Name = "local", Type = "ollama", BaseUrl = url, Model = "qwen3:8b", Thinking = thinking, ContextLength = ctx, ToolMode = mode, KeepAliveMinutes = 30 };

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", "http://127.0.0.1:11434/api/chat")]
    [InlineData("http://127.0.0.1:11434/", "http://127.0.0.1:11434/api/chat")]
    [InlineData("http://192.168.1.5:11434/api", "http://192.168.1.5:11434/api/chat")]
    public void ChatUrlAcceptsAnyBase(string baseUrl, string expected) =>
        Assert.Equal(expected, OllamaProvider.ChatUrl(Cfg(url: baseUrl)));

    [Fact]
    public void BodyTurnsThinkingOffAndSetsContext()
    {
        var body = OllamaProvider.BuildBody(Cfg(), new LlmRequest("SYS", Turns, Tools.All, 300, 0.6));
        Assert.False(body["think"]!.GetValue<bool>());
        Assert.Equal(12288, body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal(300, body["options"]!["num_predict"]!.GetValue<int>());
        Assert.Equal("30m", body["keep_alive"]!.GetValue<string>());
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.Equal(Tools.All.Count, body["tools"]!.AsArray().Count);
        Assert.Null(body["format"]);

        var auto = OllamaProvider.BuildBody(Cfg("auto", 0), new LlmRequest("SYS", Turns, Tools.All, 300, 0.6));
        Assert.Null(auto["think"]);
        Assert.Null(auto["options"]!["num_ctx"]);

        var prompt = OllamaProvider.BuildBody(Cfg(mode: "prompt"), new LlmRequest("SYS", Turns, Tools.All, 300, 0.6));
        Assert.Null(prompt["tools"]);
        Assert.Equal("json", prompt["format"]!.GetValue<string>());
        Assert.Contains("{\"calls\": [", prompt["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ParsesNativeToolCallsAndIgnoresThoughts()
    {
        var raw = "{\"message\":{\"role\":\"assistant\",\"content\":\"<think>the reactor...\\nwait is bad</think>\",\"tool_calls\":[" +
                  "{\"function\":{\"name\":\"fix_leaks\",\"arguments\":{\"priority\":2}}},{\"function\":{\"name\":\"say\",\"arguments\":{\"text\":\"Варю!\"}}}]}," +
                  "\"prompt_eval_count\":812,\"eval_count\":31}";
        var r = OllamaProvider.ParseResponse(raw, Tools.All);
        Assert.Equal(new[] { "fix_leaks", "say" }, r.ToolCalls.Select(c => c.Name));
        Assert.Equal("Варю!", r.ToolCalls[1].Arguments["text"]!.GetValue<string>());
        Assert.Equal(812, r.InputTokens);
        Assert.Equal("", r.Text);

        // thoughts with a tool name inside must not become calls; the real answer after them does
        var text = "{\"message\":{\"content\":\"<think>maybe wait()? no.</think>\\n{\\\"calls\\\":[{\\\"name\\\":\\\"fix_leaks\\\",\\\"arguments\\\":{}}]}\"}}";
        Assert.Equal(new[] { "fix_leaks" }, OllamaProvider.ParseResponse(text, Tools.All).ToolCalls.Select(c => c.Name));
    }

    [Fact]
    public void OpenAiTypeUsesTheSoftSwitch()
    {
        var cfg = new ProviderConfig { Name = "x", Type = "openai", BaseUrl = "http://x/v1", Model = "qwen3", Thinking = "off" };
        var body = OpenAiCompatibleProvider.BuildBody(cfg, new LlmRequest("SYS", Turns, Tools.All, 100, 0.5));
        Assert.EndsWith("/no_think", body["messages"]![0]!["content"]!.GetValue<string>());
        cfg.Thinking = "auto";
        Assert.DoesNotContain("/no_think", OpenAiCompatibleProvider.BuildBody(cfg, new LlmRequest("SYS", Turns, Tools.All, 100, 0.5))["messages"]![0]!["content"]!.GetValue<string>());
        var raw = "{\"choices\":[{\"message\":{\"content\":\"<think>wait</think>{\\\"calls\\\":[{\\\"name\\\":\\\"repair_systems\\\",\\\"arguments\\\":{}}]}\"}}]}";
        Assert.Equal(new[] { "repair_systems" }, OpenAiCompatibleProvider.ParseResponse(raw, Tools.All).ToolCalls.Select(c => c.Name));
    }

    [Fact]
    public async Task RouterTalksToTheNativeEndpoint()
    {
        var providers = @"[ { ""name"": ""a"", ""type"": ""ollama"", ""baseUrl"": ""http://ollama.test:11434/v1"", ""model"": ""qwen3:8b"", ""thinking"": ""off"", ""contextLength"": 8192 } ]";
        var settings = ModSettings.Parse(Fixtures.Config(providersOverride: providers).Replace("\"fallbackProvider\": \"b\"", "\"fallbackProvider\": \"\""));
        string? url = null; JsonObject? sent = null;
        var handler = new FakeHandler((req, body) =>
        {
            url = req.RequestUri!.ToString(); sent = body;
            return Task.FromResult((HttpStatusCode.OK, "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"wait\",\"arguments\":{}}}]}}"));
        });
        using var router = new LlmRouter(settings, handler);
        var r = await router.CompleteAsync("a", "SYS", Turns, Tools.All, CancellationToken.None);
        Assert.Equal(RouteStatus.Ok, r.Status);
        Assert.Equal("http://ollama.test:11434/api/chat", url);
        Assert.False(sent!["think"]!.GetValue<bool>());
        Assert.Equal("wait", r.Response!.ToolCalls.Single().Name);
    }

    [Theory]
    [InlineData("\"thinking\": \"maybe\"", "thinking must be")]
    [InlineData("\"contextLength\": -1", "contextLength")]
    public void Validation(string extra, string error)
    {
        var providers = $@"[ {{ ""name"": ""a"", ""type"": ""ollama"", ""baseUrl"": ""http://o.test"", ""model"": ""m"", {extra} }} ]";
        var json = Fixtures.Config(providersOverride: providers).Replace("\"fallbackProvider\": \"b\"", "\"fallbackProvider\": \"\"");
        var e = Assert.Throws<System.IO.InvalidDataException>(() => ModSettings.Parse(json));
        Assert.Contains(error, e.Message);
    }
}
