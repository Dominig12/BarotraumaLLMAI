using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>
/// Chat. Every line is written into the round's conversation (<see cref="ConversationLog"/>). Who a player's message
/// is for is decided by a model, the dispatcher, which sees the recent conversation: a message without a name that
/// continues a talk goes to the one the player was talking with. No rules decide for it. When the dispatcher gives no
/// usable answer (twice) or none in time, everyone who heard the message gets it, marked as maybe not meant for them,
/// and each bot's own model decides whether to answer. The sender gets a private note of who took it.
/// </summary>
public sealed partial class AgentManager
{
    sealed class PendingChat
    {
        public double At;
        public ChatCommand Cmd = null!;
        public ConversationLog.Line Line = null!;
        public int Attempts;
        public bool InFlight;
    }

    readonly List<PendingChat> pendingChats = new();
    readonly ConversationLog conversation = new();

    public int PendingChatCount => pendingChats.Count;

    /// <summary>What was said aboard this round.</summary>
    public ConversationLog Conversation => conversation;

    const int DispatchAttempts = 2;
    int dispatchRequests;

    /// <summary>A human player wrote in the chat (game thread).</summary>
    public void SubmitChat(ChatCommand cmd)
    {
        var chat = Settings.Chat;
        if (!Settings.Enabled || !chat.Enabled) return;
        var text = cmd.Text.Trim();
        if (!string.IsNullOrEmpty(chat.Prefix))
        {
            if (!text.StartsWith(chat.Prefix, StringComparison.OrdinalIgnoreCase)) return;
            text = text.Substring(chat.Prefix.Length).Trim();
        }
        if (text.Length == 0) return;
        // "!command" of another mod, or one of ours with a typo: not an order for the crew
        if (text.StartsWith("!") || text.StartsWith("/"))
        {
            Log.Info($"chat from {cmd.SenderName}: \"{Tools.Clip(text, 60)}\" looks like a command, not passed to the crew");
            return;
        }
        cmd = cmd with { Text = Tools.Clip(text, 500) };
        double now = clock();
        var line = conversation.Add(now, cmd.SenderName, true, cmd.Text, cmd.ViaRadio, cmd.ListenerIds.ToArray());

        if (pendingChats.Count >= Math.Max(1, chat.MaxQueue)) pendingChats.RemoveAt(0);
        pendingChats.Add(new PendingChat { At = now, Cmd = cmd, Line = line });
        Log.Info($"chat from {cmd.SenderName}: \"{cmd.Text}\" heard by {cmd.ListenerIds.Count} bot(s)");
        nextTick = double.NegativeInfinity; // react on the next frame
    }

    /// <summary>A bot said something (say, message_crew, order_crew): part of the conversation.</summary>
    void NoteSpeech(BotAgent agent, ToolCall call, string result)
    {
        if (!result.StartsWith("ok")) return;
        var text = call.Name switch
        {
            "say" => Tools.Str(call.Arguments, "text"),
            "message_crew" => Tools.Str(call.Arguments, "text"),
            "order_crew" => Tools.Str(call.Arguments, "order"),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text)) return;
        var to = call.Name == "say" ? null : Tools.Str(call.Arguments, "to");
        conversation.Add(clock(), agent.Name, false, text!, radio: call.Name != "say", heardBy: null, to: to);
    }

    /// <summary>Lines of the recent conversation for one bot (null: for the dispatcher, all of them).</summary>
    List<string> ConversationFor(BotAgent? agent, double now, IReadOnlyCollection<int>? heardByAnyOf = null)
    {
        var c = Settings.Chat;
        var lines = conversation.Recent(agent?.Id, now, c.ConversationMinutes * 60, int.MaxValue);
        if (heardByAnyOf != null) lines = lines.Where(l => heardByAnyOf.Any(l.HeardByBot)).ToList();
        return lines.TakeLast(Math.Max(0, c.ConversationLines)).Select(l => ConversationLog.Format(l, now, agent?.Name)).ToList();
    }

    /// <summary>A message waits for the dispatcher only because every request slot is busy: the bots do not start new
    /// requests meanwhile, so it gets the next free one. (When a per-minute or daily limit is the reason, waiting helps
    /// nobody, and the bots go on.)</summary>
    bool holdForDispatch;

    void DispatchChats(double now)
    {
        holdForDispatch = false;
        var chat = Settings.Chat;
        for (int i = 0; i < pendingChats.Count;)
        {
            var p = pendingChats[i];
            if (p.InFlight) { i++; continue; }
            var cmd = p.Cmd;
            if (now - p.At > Math.Max(chat.MaxAgeSeconds, chat.DispatchWaitSeconds + 1))
            {
                Log.Warn($"chat from {cmd.SenderName} dropped: could not be dispatched in {chat.MaxAgeSeconds}s");
                pendingChats.RemoveAt(i);
                continue;
            }

            var roster = BuildRoster(cmd);
            if (roster.Count == 0)
            {
                Log.Info($"chat from {cmd.SenderName}: no AI crew heard it");
                if (OrderHints.Suggest(cmd.Text, cmd.SenderName, null) != null)
                    Tell(cmd.SenderName, "Приказ никто из ИИ-экипажа не услышал (далеко или нет рации).");
                conversation.SetRecipients(p.Line, Array.Empty<string>());
                pendingChats.RemoveAt(i);
                continue;
            }

            if (now - p.At >= chat.DispatchWaitSeconds)
            {
                // no answer in time: everyone who heard it, and each bot's own model decides
                DeliverToListeners(p, roster, "the dispatcher did not answer in time");
                pendingChats.RemoveAt(i);
                continue;
            }

            var provider = string.IsNullOrEmpty(chat.Provider) ? Settings.DefaultProvider : chat.Provider;
            if (!router.TryReserve(provider, out var reservation, out var why))
            {
                if (why.Contains("busy")) holdForDispatch = true;
                i++;
                continue;   // next tick
            }
            p.InFlight = true;
            p.Attempts++;
            StartDispatch(p, roster, reservation!, now);
            i++;
        }
    }

    List<RosterEntry> BuildRoster(ChatCommand cmd)
    {
        var roster = new List<RosterEntry>();
        foreach (var id in cmd.ListenerIds.Distinct())
        {
            if (!agents.TryGetValue(id, out var a) || !game.IsAlive(id)) continue;
            var o = game.Brief(id);
            roster.Add(new RosterEntry(a.Id, a.Name, a.Job, o.Room, o.CurrentOrder, a.CurrentGoal?.Text));
        }
        return roster;
    }

    void StartDispatch(PendingChat p, List<RosterEntry> roster, LlmRouter.Reservation reservation, double now)
    {
        int gen = generation;
        var talk = ConversationFor(null, now, roster.Select(r => r.Id).ToList());
        // the message itself is the last line of the conversation: not twice
        if (talk.Count > 0 && talk[^1].EndsWith($"\"{p.Cmd.Text}\"")) talk.RemoveAt(talk.Count - 1);
        var user = ChatDispatcher.UserPrompt(p.Cmd, roster, talk);
        var r0 = router;
        CountRequest();
        if (Settings.Global.LogPrompts) Log.Info($"dispatch -> {reservation.Provider}: {user.Replace('\n', ' ')}");
        Track(Task.Run(async () =>
        {
            RouteResult r;
            try
            {
                r = await r0.CompleteAsync(reservation, ChatDispatcher.SystemPrompt,
                    new[] { new LlmTurn("user", user) }, new[] { ChatDispatcher.Tool }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { r = new RouteResult(RouteStatus.Failed, null, reservation.Provider, e.Message); }
            OnGameThread(gen, t => ApplyDispatch(p, roster, r, t));
        }));
    }

    void ApplyDispatch(PendingChat p, List<RosterEntry> roster, RouteResult r, double now)
    {
        if (!pendingChats.Contains(p)) return;   // given up on already (see Watchdog)
        p.InFlight = false;
        CountTokens(r);
        dispatchRequests++;
        var parsed = r.Status == RouteStatus.Ok ? ChatDispatcher.ParseRecipients(r.Response, roster) : null;
        if (parsed != null)
        {
            var why = Tools.Str(r.Response!.ToolCalls.First(c => c.Name == ChatDispatcher.ToolName).Arguments, "reason") ?? "dispatcher";
            Log.Info($"dispatch <- {(parsed.Count == 0 ? "nobody" : string.Join(", ", parsed.Select(x => x.Name)))} ({why})");
            pendingChats.Remove(p);
            Deliver(p, parsed, "dispatcher: " + why);
            return;
        }
        var detail = r.Status != RouteStatus.Ok ? r.Detail
            : "no route_message call" + (string.IsNullOrWhiteSpace(r.Response?.Text) ? "" : ": " + Tools.Clip(JsonUtil.OneLine(r.Response!.Text), 300));
        Log.Warn($"dispatch of \"{Tools.Clip(p.Cmd.Text, 60)}\" failed ({detail})" + (p.Attempts < DispatchAttempts ? "; asking again" : ""));
        if (p.Attempts < DispatchAttempts) return;   // stays pending: asked again next tick
        pendingChats.Remove(p);
        DeliverToListeners(p, roster, "the dispatcher gave no usable answer");
    }

    void Deliver(PendingChat p, IReadOnlyList<RosterEntry> recipients, string how)
    {
        var cmd = p.Cmd;
        conversation.SetRecipients(p.Line, recipients.Select(r => r.Name));
        if (recipients.Count == 0)
        {
            Log.Info($"chat from {cmd.SenderName}: not addressed to the AI crew ({how})");
            return;
        }
        double now = clock();
        var got = new List<string>();
        foreach (var r in recipients)
        {
            if (!agents.TryGetValue(r.Id, out var a)) continue;
            a.ReceivePlayerOrder(ChatDispatcher.FormatOrder(cmd, recipients, r), authority: true, from: cmd.SenderName);
            a.AddEvent(EventKind.Order, $"new order from {cmd.SenderName} (see playerOrders)", now);
            got.Add(a.Name);
        }
        nextTick = double.NegativeInfinity;
        Log.Info($"order from {cmd.SenderName} -> {string.Join(", ", recipients.Select(x => x.Name))} ({how})");
        if (got.Count > 0) Tell(cmd.SenderName, "Приказ получили: " + string.Join(", ", got));
    }

    /// <summary>Nobody could tell who it is for: everyone who heard it gets it, and each one's model decides.</summary>
    void DeliverToListeners(PendingChat p, IReadOnlyList<RosterEntry> roster, string why)
    {
        var cmd = p.Cmd;
        conversation.SetRecipients(p.Line, Array.Empty<string>());
        double now = clock();
        foreach (var r in roster)
        {
            if (!agents.TryGetValue(r.Id, out var a)) continue;
            a.ReceivePlayerOrder(ChatDispatcher.FormatUnsure(cmd, r), authority: true, from: cmd.SenderName);
            a.AddEvent(EventKind.Order, $"{cmd.SenderName} said something that may be for you (see playerOrders)", now);
        }
        nextTick = double.NegativeInfinity;
        Log.Info($"chat from {cmd.SenderName} -> everyone who heard it, each decides ({why})");
    }

    /// <summary>A private note to a player (if chat.confirmToSender is on).</summary>
    void Tell(string player, string text)
    {
        if (!Settings.Chat.ConfirmToSender || string.IsNullOrWhiteSpace(player)) return;
        try { game.NotifyPlayer(player, "[ИИ] " + text); }
        catch (Exception e) { Log.Warn("could not notify " + player + ": " + e.Message); }
    }
}
