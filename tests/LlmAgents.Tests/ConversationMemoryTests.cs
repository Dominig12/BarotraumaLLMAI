using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class ConversationMemoryTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new FakeGame().AddBot(1, "Fernanda", "assistant").AddBot(2, "Bettyann", "captain");

    [Fact]
    public void Recall_finds_by_word_stems_in_any_alphabet_latest_first()
    {
        var items = new List<(string, string, double)>
        {
            ("talk", "(5 min ago) Daniil (player) -> you: \"положи ресурсы в шкаф рядом с фабрикатором\"", 300),
            ("done", "move_item(item=\"Стальной слиток\", to=\"Большой стальной шкаф\") -> ok: Стальной слиток x3 in Большой стальной шкаф", 1e5),
            ("note", "Daniil wants a report when the harpoons are done", 60),
        };
        var r = AgentManager.RecallText(items, "шкафу");
        Assert.Contains("Стальной слиток x3", r);
        Assert.Contains("фабрикатором", r);
        Assert.DoesNotContain("harpoons", r);
        Assert.Contains("harpoons", AgentManager.RecallText(items, "harpoon"));
        Assert.StartsWith("[note]", AgentManager.RecallText(items, null));     // the latest first
        Assert.StartsWith("nothing about", AgentManager.RecallText(items, "реактор"));
    }

    [Fact]
    public async Task Bots_hear_the_talk_and_recall_it_with_their_own_deeds()
    {
        var settings = Fixtures.Settings();
        int n = 0;
        string? recallAnswer = null;
        var handler = new FakeHandler(body =>
        {
            var msgs = body["messages"]!.AsArray();
            var user = msgs[^1]!["content"]!.GetValue<string>();
            if (body["tools"]!.AsArray().Any(t => t!["function"]!["name"]!.GetValue<string>() == ChatDispatcher.ToolName))
                return (HttpStatusCode.OK, Fixtures.DispatchByName(user));
            var obs = JsonNode.Parse(Fixtures.Obs(user))!;
            if (obs["name"]!.GetValue<string>() != "Fernanda") return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}")));
            if (user.Contains("recall(")) recallAnswer = user;
            return (HttpStatusCode.OK, n++ switch
            {
                0 => Fixtures.OpenAiToolResponse(("carry_on", "{}")),
                1 => Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Сейчас положу в шкаф\"}"), ("remember", "{\"note\":\"Daniil ждёт доклад про шкаф\"}"),
                                                  ("move_item", "{\"item\":\"steel\",\"to\":\"cabinet\"}")),
                _ => Fixtures.OpenAiToolResponse(("recall", "{\"query\":\"шкаф\"}")),
            });
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        for (int i = 0; i < 6; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }
        mgr.SubmitChat(new ChatCommand("Daniil", null, "Фернанда, положи слитки в шкаф", new[] { 1, 2 }, true));
        for (int i = 0; i < 6; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }

        var talk = mgr.Conversation.All;
        Assert.Contains(talk, l => l.IsPlayer && l.To == "Fernanda");
        Assert.Contains(talk, l => l.Speaker == "Fernanda" && l.Text == "Сейчас положу в шкаф");

        var recall = mgr.Recall(mgr.GetAgent(1)!, "шкаф");
        Assert.Contains("[talk]", recall);
        Assert.Contains("you: \"Сейчас положу в шкаф\"", recall);
        Assert.Contains("[note] Daniil ждёт доклад", recall);
        Assert.Contains("move_item", mgr.Recall(mgr.GetAgent(1)!, "steel"));   // what it did, by the item's name

        // Bettyann heard the radio too: the follow-up line is in her conversation
        var heard = mgr.Conversation.Recent(2, clock.Now, 300, 10);
        Assert.Contains(heard, l => l.Text.StartsWith("Фернанда, положи"));
    }

    [Fact]
    public void Old_notes_leave_the_prompt_but_not_the_memory()
    {
        var a = new BotAgent(new BotHandle(1, "Ann", "mechanic"));
        for (int i = 0; i < 8; i++) a.AddNote("note " + i, 3, i);
        Assert.Equal(3, a.Notes().Count);
        Assert.Equal(8, a.AllNotes().Count);
    }

    [Fact]
    public void Recall_is_a_lookup_and_always_offered()
    {
        Assert.Equal(ToolKind.Lookup, Tools.Def("recall")!.Kind);
        Assert.Contains(Tools.For(new List<string>(), false, memory: false, ToolGroup.Core), t => t.Name == "recall");
    }

    [Fact]
    public void Prompt_tells_how_to_use_conversation_and_memory()
    {
        var s = Prompts.System(new BotAgent(new BotHandle(1, "Ann", "mechanic")), 6, memory: false);
        Assert.Contains("'conversation'", s);
        Assert.Contains("recall(query)", s);
        Assert.Contains("remember(note)", s);
    }
}
