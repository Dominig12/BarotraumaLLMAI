using System.Threading.Tasks;
using System.Linq;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Replies from the 2026-10-16 log (deepseek through an OpenAI-compatible gateway, prompt mode).</summary>
public class Log4ParserTests
{
    static string Calls(string text) =>
        string.Join(" | ", TextToolCallParser.Parse(text, Tools.All).Select(c => $"{c.Name}({Tools.ArgsToString(c)})"));

    [Fact]
    public void Calls_written_as_strings_inside_the_list()
    {
        Assert.Equal("inspect(kind=\"recipes\", query=\"грап\") | inspect(kind=\"items\", query=\"Инженерный отсек [h4]\")",
            Calls("{\"calls\":[\"inspect(kind=\\\"recipes\\\",query=\\\"грап\\\")\",\"inspect(kind=\\\"items\\\",query=\\\"Инженерный отсек [h4]\\\")\"]}"));
        Assert.StartsWith("inspect(kind=\"items\", query=\"fabricator\")",
            Calls("{\"calls\":[\"inspect(kind=\\\"items\\\", query=\\\"fabricator\\\"), {\\\"name\\\":\\\"inspect\\\",\\\"arguments\\\":{\\\"kind\\\":\\\"recipes\\\"}}\"]}"));
    }

    [Fact]
    public void A_cut_off_call_is_dropped_not_run_empty()
    {
        Assert.Equal("", Calls("{\"calls\": [{\"name\": \"say\", \"arguments\": {\"text\": \""));
        Assert.Equal("", Calls("{\"calls\": [{\"name\": \"say\", \"arguments\": {\"text\": \"Принял. Разбираю три сва"));
        Assert.Equal("", Calls("{\"calls\": [{\""));
    }

    [Fact]
    public void Reasoning_prose_is_not_a_call() =>
        Assert.Equal("", Calls("The room is flooding. The current task is \"burn the ballast flora\". My plan is repeating attack/refill. " +
                               "But my oxygen is 44% — that's concerning. Should I r"));

    [Fact]
    public void An_empty_list_means_carry_on() => Assert.Equal("carry_on()", Calls("{\"calls\":[]}"));

    [Fact]
    public void Quoted_names_with_arguments_still_work() =>
        Assert.Equal("say(text=\"Есть\")", Calls("\"say\", {\"text\": \"Есть\"}"));
}

public class Log4CoreTests
{
    static LlmRequest Req() => new("sys", new[] { new LlmTurn("user", "obs") }, Tools.All, 800, 1);

    [Fact]
    public void Thinking_off_also_switches_reasoning_off_on_gateways()
    {
        var gw = new ProviderConfig { Name = "g", Model = "deepseek/deepseek-v4.1-flash", BaseUrl = "https://gatellm.ru/v1", Thinking = "off" };
        Assert.False(OpenAiCompatibleProvider.BuildBody(gw, Req())["reasoning"]!["enabled"]!.GetValue<bool>());
        var openai = new ProviderConfig { Name = "o", Model = "gpt", BaseUrl = "https://api.openai.com/v1", Thinking = "off" };
        Assert.Null(OpenAiCompatibleProvider.BuildBody(openai, Req())["reasoning"]);
        var auto = new ProviderConfig { Name = "a", Model = "m", BaseUrl = "https://gatellm.ru/v1", Thinking = "auto" };
        Assert.Null(OpenAiCompatibleProvider.BuildBody(auto, Req())["reasoning"]);
        var own = new ProviderConfig { Name = "e", Model = "m", BaseUrl = "https://gatellm.ru/v1", Thinking = "off",
            ExtraBody = new System.Text.Json.Nodes.JsonObject { ["reasoning"] = new System.Text.Json.Nodes.JsonObject { ["effort"] = "low" } } };
        Assert.Equal("low", OpenAiCompatibleProvider.BuildBody(own, Req())["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failed_word_does_not_stop_the_work()
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        settings.Bots.ThinkAtRoundStart = false;
        var handler = new FakeHandler(_ => (System.Net.HttpStatusCode.OK,
            Fixtures.OpenAiToolResponse(("say", "{}"), ("message_crew", "{\"to\":\"Nobody\"}"), ("fix_leaks", "{}"))));
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update();
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: weld", from: "Daniil");
        for (int i = 0; i < 4; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }
        Assert.Contains("1:fix_leaks", game.Actions);
    }
}

public class NearPhraseTests
{
    [Theory]
    [InlineData("шкаф у фабрикатора", "шкаф", "фабрикатора")]
    [InlineData("шкафчик рядом с фабрикатором", "шкафчик", "фабрикатором")]
    [InlineData("the locker next to the reactor", "the locker", "the reactor")]
    [InlineData("ящик возле деконструктора", "ящик", "деконструктора")]
    public void Splits(string text, string what, string anchor) => Assert.Equal((what, anchor), ItemWords.SplitNear(text));

    [Theory]
    [InlineData("Большой стальной шкаф")]
    [InlineData("Инженерный отсек [h4]")]
    public void Plain_names_are_not_near_phrases(string text) => Assert.Null(ItemWords.SplitNear(text));
}
