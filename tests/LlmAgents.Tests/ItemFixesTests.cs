using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Item words: kinds of items and other word forms, so plain orders find the right things.</summary>
public class ItemWordsTests
{
    [Theory]
    [InlineData("медикаменты", new[] { ItemGroup.Medical })]
    [InlineData("Возьми лекарства", new ItemGroup[0])]                 // "возьми" is not an item word: a specific query
    [InlineData("Водолазный костюм", new[] { ItemGroup.DivingSuit })] // the item is called "Водолазный скафандр"
    [InlineData("запасные скафандры", new[] { ItemGroup.DivingSuit })]
    [InlineData("водолазное снаряжение", new[] { ItemGroup.DivingGear })]
    [InlineData("diving gear", new[] { ItemGroup.DivingGear })]
    [InlineData("подводная маска", new[] { ItemGroup.DivingMask })]
    [InlineData("оружие и патроны", new[] { ItemGroup.Weapon, ItemGroup.Ammo })]
    [InlineData("инструменты", new[] { ItemGroup.Tool })]
    [InlineData("tools", new[] { ItemGroup.Tool })]
    [InlineData("кислородные баллоны", new[] { ItemGroup.Oxygen })]
    [InlineData("бинт", new ItemGroup[0])]
    [InlineData("костюм", new ItemGroup[0])]                          // just clothes
    [InlineData("сварочное топливо", new ItemGroup[0])]                // the name says it, not reactor fuel
    [InlineData("снаряжение", new ItemGroup[0])]
    public void KindsOfItems(string query, ItemGroup[] expected) =>
        Assert.Equal(expected, ItemWords.Groups(query));

    [Theory]
    [InlineData("скафандры водолазные", "Водолазный скафандр", true)]
    [InlineData("fuel rods", "Fuel Rod", true)]
    [InlineData("сварочное топливо", "Бак со сварочным топливом", true)]
    [InlineData("Водолазный костюм", "Водолазный скафандр", false)] // "костюм" is not "скафандр": the kind covers it
    [InlineData("бинт", "Морфин", false)]
    [InlineData("все запасные", "Бинт", false)]                     // filler only
    public void OtherWordForms(string query, string name, bool match) =>
        Assert.Equal(match, ItemWords.WordsMatch(query, new[] { name }));

    [Theory]
    [InlineData("Lorriane Connors", true)]
    [InlineData("lorriane", true)]
    [InlineData("себе", true)]
    [InlineData("me", true)]
    [InlineData("в инвентарь", true)]
    [InlineData("Смертельное Рандеву", false)]
    [InlineData("medical cabinet", false)]
    public void TheBotItself(string to, bool self) => Assert.Equal(self, ItemWords.IsSelf(to, "Lorriane Connors"));

    [Fact]
    public void CopiedIdentifier()
    {
        Assert.Equal("antibloodloss1", ItemWords.BracketId("Физраствор [antibloodloss1]"));
        Assert.Null(ItemWords.BracketId("Физраствор"));
    }
}

/// <summary>Fixes from the round where bots fought over diving suits and the medic could not take medicine.</summary>
public class ItemPlanTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();
    readonly ConcurrentQueue<JsonObject> bodies = new();
    Func<JsonObject, string> reply = _ => Fixtures.OpenAiToolResponse(("carry_on", "{}"));

    AgentManager Create(string global = "", Func<JsonObject, string>? raw = null)
    {
        var settings = Fixtures.Settings(@", ""maxActionsPerDecision"": 6, ""maxPlanSeconds"": 120, ""planStepTimeoutSeconds"": 30" + global);
        var handler = new FakeHandler(body =>
        {
            bodies.Enqueue(body);
            return (HttpStatusCode.OK, (raw ?? reply)(body));
        });
        return new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
    }

    async Task Run(AgentManager mgr, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.5)
        {
            mgr.Update();
            await mgr.WhenIdle();
            clock.Advance(0.5);
        }
    }

    [Theory]
    [InlineData("{\"item\":\"medical\",\"count\":5}", "1:take medical x5")]
    [InlineData("{\"item\":\"Бинт\"}", "1:take Бинт x1")]
    [InlineData("{\"item\":\"ammo\",\"amount\":50}", "1:take ammo x8")]
    [InlineData("{\"item\":\"tools\",\"count\":3,\"from\":\"Электрика [h140]\"}", "1:take tools x3 from Электрика [h140]")]
    [InlineData("{\"item\":\"tools\",\"from\":\" \"}", "1:take tools x1")]
    public void TakeItemArguments(string args, string action)
    {
        game.AddBot(1, "Lorriane", "medicaldoctor");
        var agent = new BotAgent(new BotHandle(1, "Lorriane", "medicaldoctor"));
        var r = Tools.Execute(new ToolCall("take_item", (JsonObject)JsonNode.Parse(args)!), agent, game, Create());
        Assert.StartsWith("ok, started", r);
        Assert.Equal(action, game.Actions.Last());
    }

    [Theory]
    [InlineData("pick_up(item=\"bandage\", count=2)")]
    [InlineData("take(item=\"bandage\", count=2)")]
    public void TakeAliases(string text)
    {
        var calls = TextToolCallParser.Parse(text, Tools.All);
        Assert.Equal("take_item", Assert.Single(calls).Name);
    }

    [Fact]
    public async Task TakingItemsIsATaskThePlanWaitsFor()
    {
        game.AddBot(1, "Lorriane", "medicaldoctor");
        var mgr = Create();
        int n = 0;
        reply = _ => n++ == 0
            ? Fixtures.OpenAiToolResponse(("take_item", "{\"item\":\"medical\",\"count\":4}"), ("follow", "{\"target\":\"Daniil\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 3);
        Assert.Equal(new[] { "1:take medical x4" }, game.Actions);   // follows only once the medicine is taken

        game.PendingReports.Add(new GameReport(1, "task 'take medical' ok: took Бинт x2, Морфин x2 (in your inventory now)"));
        await Run(mgr, 1);
        Assert.Equal("1:follow Daniil", game.Actions.Last());
    }

    [Fact]
    public async Task AFailedLastStepIsReportedOnceAsFailed()
    {
        game.AddBot(1, "Lorriane", "medicaldoctor");
        var mgr = Create();
        await Run(mgr, 3);
        int n = 0;
        reply = _ => n++ == 0
            ? Fixtures.OpenAiToolResponse(("take_item", "{\"item\":\"medical\",\"count\":4}"), ("go_to_room", "{\"room\":\"Nowhere\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: medic, take medicine and come", from: "Daniil");
        await Run(mgr, 3);
        game.PendingReports.Add(new GameReport(1, "task 'take medical' ok: took Бинт x4 (in your inventory now)"));
        await Run(mgr, 2);

        var notices = game.Notices.Select(x => x.Text).Where(x => x.Contains("Lorriane:")).ToList();
        Assert.Contains(notices, x => x.Contains("не получилось"));
        Assert.DoesNotContain(notices, x => x.Contains("выполнено"));
    }

    [Fact]
    public async Task ARoomThatIsTheDestinationKeepsItsOrder()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        int n = 0;
        reply = _ => n++ == 0
            ? Fixtures.OpenAiToolResponse(("go_to_room", "{\"room\":\"Command Room [h11]\"}"), ("say", "{\"text\":\"On my way\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 3);
        game.Observations[1].Room = "Command Room [h11]";
        await Run(mgr, 2);
        Assert.Contains("1:say On my way", game.Actions);
        Assert.DoesNotContain("1:end travel", game.Actions);           // "go there" then talk: stay there
    }

    static string CutOff() => new JsonObject
    {
        ["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "" },
                ["finish_reason"] = "length",
            },
        },
        ["usage"] = new JsonObject
        {
            ["prompt_tokens"] = 100, ["completion_tokens"] = 800,
            ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 800 },
        },
    }.ToJsonString();

    [Fact]
    public async Task AReplyCutOffWhileThinkingIsRetriedWithMoreRoom()
    {
        game.AddBot(1, "Ann", "medicaldoctor");
        int n = 0;
        var mgr = Create(raw: _ => n++ == 0 ? CutOff() : Fixtures.OpenAiToolResponse(("carry_on", "{}")));
        await Run(mgr, 10);     // round start: cut off, then the retry
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: Ann, grab medicine", from: "Daniil");
        await Run(mgr, 3);

        var tokens = bodies.Select(b => b["max_tokens"]!.GetValue<int>()).ToList();
        Assert.True(tokens.Count >= 3, string.Join(",", tokens));
        Assert.Equal(800, tokens[0]);
        Assert.Equal(1600, tokens[1]);   // the retry: twice the room
        Assert.Equal(800, tokens[2]);    // back to normal once calls came through
    }
    [Fact]
    public async Task AReplyCutOffAgainGetsMoreRoomAgain()
    {
        game.AddBot(1, "Ann", "medicaldoctor");
        int n = 0;
        var mgr = Create(raw: _ => n++ < 2 ? CutOff() : Fixtures.OpenAiToolResponse(("carry_on", "{}")));
        await Run(mgr, 3);
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: Ann, grab medicine", from: "Daniil");
        await Run(mgr, 15);

        var tokens = bodies.Select(b => b["max_tokens"]!.GetValue<int>()).ToList();
        Assert.True(tokens.Count >= 3, string.Join(",", tokens));
        Assert.Equal(new[] { 800, 1600, 3200 }, tokens.Take(3));   // cut off twice: the order is not given up
        Assert.DoesNotContain(game.Notices, x => x.Text.Contains("не может выполнить"));
    }
}

/// <summary>Hints next to chat orders (log db9d61e0).</summary>
public class OrderHintTests
{
    [Theory]
    [InlineData("Трейси, бери отсюда инструменты", "take_item", "come_to")]       // "отсюда" is "from here", not "come here"
    [InlineData("Трейси, ко мне", "come_to", "take_item")]
    [InlineData("иди сюда", "come_to", null)]
    [InlineData("Медик, хватай из шкафа медикаменты и за мной!", "take_item", null)]
    [InlineData("у тебя мало при себе медикаментов, бери еще!", "take_item", null)]
    [InlineData("Медик, Подготовь медикаменты!", "take_item", null)]
    [InlineData("Садитесь за пушки!", "operate(role=\"weapons\")", null)]
    [InlineData("Надеваем водолазное снаряжение!", "equip(item=\"diving suit\")", null)]
    [InlineData("Не снимаем снаряжение!", "keep_gear(on=true)", "equip")]
    [InlineData("Трейси, ко мне, тебе надо в щитовой взять инструменты и разобрать их", "deconstruct", null)]
    public void Hints(string text, string expected, string? notExpected)
    {
        var hint = OrderHints.Suggest(text, "Daniil", "Электрика [h140]");
        Assert.NotNull(hint);
        Assert.Contains(expected, hint);
        if (notExpected != null) Assert.DoesNotContain(notExpected, hint);
    }

    [Fact]
    public void TakeFromHereNamesTheSpeakersRoom() =>
        Assert.Contains("from=\"Электрика [h140]\"", OrderHints.Suggest("бери отсюда инструменты", "Daniil", "Электрика [h140]"));

    [Fact]
    public void PlainTalkHasNoHint() => Assert.Null(OrderHints.Suggest("как дела?", "Daniil", null));
}

/// <summary>Marks (middle mouse button) and unpacking a crate.</summary>
public class MarksAndUnpackTests
{
    [Theory]
    [InlineData("помеченный ящик", MarkQuery.Ai, "ящик")]
    [InlineData("marked", MarkQuery.Ai, "")]
    [InlineData("the marked crate", MarkQuery.Ai, "crate")]
    [InlineData("помеченное на разбор", MarkQuery.Deconstruct, "")]
    [InlineData("marked for deconstruction", MarkQuery.Deconstruct, "")]
    [InlineData("металлический ящик", MarkQuery.None, "металлический ящик")]
    public void MarkQueries(string query, MarkQuery kind, string rest)
    {
        Assert.Equal(kind, MarkWords.Of(query));
        if (kind != MarkQuery.None) Assert.Equal(rest, MarkWords.Rest(query));
    }

    [Theory]
    [InlineData("на место", true)]
    [InlineData("по местам", true)]
    [InlineData("away", true)]
    [InlineData("its place", true)]
    [InlineData("медицинский шкаф", false)]
    [InlineData("Daniil", false)]
    public void ItsPlace(string to, bool expected) => Assert.Equal(expected, ItemWords.IsItsPlace(to));

    [Theory]
    [InlineData("Возьми металлический ящик и разнеси что в нём по подлодке", "unpack")]
    [InlineData("Разложи помеченный ящик", "unpack")]
    [InlineData("Разложи помеченный ящик", "item=\"marked\"")]
    [InlineData("Отнеси это помеченное в медотсек", "item=\"marked\"")]
    public void Hints(string text, string expected) =>
        Assert.Contains(expected, OrderHints.Suggest(text, "Daniil", "Грузовой отсек [h3]"));

    [Fact]
    public void BattleStationsIsNotUnpacking() =>
        Assert.DoesNotContain("unpack", OrderHints.Suggest("Все по местам!", "Daniil", null) ?? "");

    [Theory]
    [InlineData("{\"container\":\"metal crate\"}", "1:unpack metal crate")]
    [InlineData("{\"container\":\"marked\",\"carry\":false}", "1:unpack marked (no carry)")]
    [InlineData("{\"item\":\"ящик\"}", "1:unpack ящик")]
    public void UnpackArguments(string args, string action)
    {
        var game = new FakeGame();
        game.AddBot(1, "Tracy", "assistant");
        var agent = new BotAgent(new BotHandle(1, "Tracy", "assistant"));
        var settings = Fixtures.Settings();
        using var mgr = new AgentManager(settings, game, new LlmRouter(settings, new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}"))))), () => 0);
        var r = Tools.Execute(new ToolCall("unpack", (JsonObject)JsonNode.Parse(args)!), agent, game, mgr);
        Assert.StartsWith("ok, started", r);
        Assert.Equal(action, game.Actions.Last());
    }

    [Fact]
    public void UnpackIsATaskThePlanWaitsFor() => Assert.Contains("unpack", AgentManager.TaskTools);

    [Fact]
    public void MarksAreShownToTheModel()
    {
        var o = new Observation { Name = "Tracy", Marks = new() { "for the AI: Металлический ящик [metalcrate] in Грузовой отсек [h3] (marked by Daniil, 5s ago)" } };
        Assert.Contains("\"marks\"", o.ToCompactJson());
        Assert.DoesNotContain("\"marks\"", new Observation { Name = "Tracy" }.ToCompactJson());   // nothing marked: not shown
    }
}
