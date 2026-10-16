using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>
/// Chat orders. A message is heard by the bots in range (or on the radio). Clear cases are routed by rules
/// (<see cref="ChatRouter"/>: a bot's name, a job, an order for everybody); only unclear ones ask the LLM dispatcher,
/// and if it gets no slot in time, the message goes to the bots named in it or to everyone who heard it.
/// Every recipient gets the order as an event. The sender gets a private note: who took it, or that nobody heard it.
/// </summary>
public sealed partial class AgentManager
{
    readonly List<(double At, ChatCommand Cmd)> pendingChats = new();

    public int PendingChatCount => pendingChats.Count;

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

        if (pendingChats.Count >= Math.Max(1, chat.MaxQueue)) pendingChats.RemoveAt(0);
        pendingChats.Add((clock(), cmd));
        Log.Info($"chat from {cmd.SenderName}: \"{cmd.Text}\" heard by {cmd.ListenerIds.Count} bot(s)");
        nextTick = double.NegativeInfinity; // react on the next frame
    }

    void DispatchChats(double now)
    {
        var chat = Settings.Chat;
        for (int i = 0; i < pendingChats.Count;)
        {
            var (at, cmd) = pendingChats[i];
            if (now - at > chat.MaxAgeSeconds)
            {
                Log.Warn($"chat from {cmd.SenderName} dropped: could not be dispatched in {chat.MaxAgeSeconds}s");
                pendingChats.RemoveAt(i);
                continue;
            }

            var roster = BuildRoster(cmd);
            if (roster.Count == 0)
            {
                Log.Info($"chat from {cmd.SenderName}: no AI crew heard it");
                if (ChatRouter.LooksLikeOrder(cmd.Text))
                    Tell(cmd.SenderName, "Приказ никто из ИИ-экипажа не услышал (далеко или нет рации).");
                pendingChats.RemoveAt(i);
                continue;
            }

            if (roster.Count == 1 && chat.DirectIfSingleListener)
            {
                Deliver(cmd, roster, "only listener");
                pendingChats.RemoveAt(i);
                continue;
            }

            if (chat.RuleRouting && ChatRouter.TryRoute(cmd, roster) is { } route)
            {
                Deliver(cmd, route.Recipients, route.Reason);
                pendingChats.RemoveAt(i);
                continue;
            }

            var provider = string.IsNullOrEmpty(chat.Provider) ? Settings.DefaultProvider : chat.Provider;
            if (!router.TryReserve(provider, out var reservation, out var why))
            {
                if (now - at >= chat.DispatchWaitSeconds)
                {
                    // no LLM slot for the dispatcher (limits, busy, provider down): route by names instead of dropping the order
                    Deliver(cmd, ChatDispatcher.Fallback(cmd, roster), "fallback: no free LLM slot for the dispatcher (" + why + ")");
                    pendingChats.RemoveAt(i);
                    continue;
                }
                i++; // try again next tick
                continue;
            }
            pendingChats.RemoveAt(i);
            StartDispatch(cmd, roster, reservation!);
        }
    }

    List<RosterEntry> BuildRoster(ChatCommand cmd)
    {
        var roster = new List<RosterEntry>();
        foreach (var id in cmd.ListenerIds.Distinct())
        {
            if (!agents.TryGetValue(id, out var a) || !game.IsAlive(id)) continue;
            var o = game.Brief(id);
            roster.Add(new RosterEntry(a.Id, a.Name, a.Job, o.Room, o.CurrentOrder));
        }
        return roster;
    }

    void StartDispatch(ChatCommand cmd, List<RosterEntry> roster, LlmRouter.Reservation reservation)
    {
        int gen = generation;
        var user = ChatDispatcher.UserPrompt(cmd, roster);
        var r0 = router;
        CountRequest();
        if (Settings.Global.LogPrompts) Log.Info($"dispatch -> {reservation.Provider}: {user.Replace('\n', ' ')}");
        Track(Task.Run(async () =>
        {
            List<RosterEntry> recipients;
            string why;
            try
            {
                var r = await r0.CompleteAsync(reservation, ChatDispatcher.SystemPrompt,
                    new[] { new LlmTurn("user", user) }, new[] { ChatDispatcher.Tool }, cts.Token).ConfigureAwait(false);
                var parsed = r.Status == RouteStatus.Ok ? ChatDispatcher.ParseRecipients(r.Response, roster) : null;
                if (parsed != null)
                {
                    recipients = parsed;
                    why = Tools.Str(r.Response!.ToolCalls.First(c => c.Name == ChatDispatcher.ToolName).Arguments, "reason") ?? "dispatcher";
                }
                else
                {
                    recipients = ChatDispatcher.Fallback(cmd, roster);
                    why = r.Status == RouteStatus.Ok ? "fallback: dispatcher gave no route" : "fallback: " + r.Detail;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                recipients = ChatDispatcher.Fallback(cmd, roster);
                why = "fallback: " + e.Message;
            }
            Log.Info($"dispatch <- {(recipients.Count == 0 ? "nobody" : string.Join(", ", recipients.Select(x => x.Name)))} ({why})");
            OnGameThread(gen, _ => Deliver(cmd, recipients, "dispatcher: " + why));
        }));
    }

    void Deliver(ChatCommand cmd, IReadOnlyList<RosterEntry> recipients, string how)
    {
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

    /// <summary>A private note to a player (if chat.confirmToSender is on).</summary>
    void Tell(string player, string text)
    {
        if (!Settings.Chat.ConfirmToSender || string.IsNullOrWhiteSpace(player)) return;
        try { game.NotifyPlayer(player, "[ИИ] " + text); }
        catch (Exception e) { Log.Warn("could not notify " + player + ": " + e.Message); }
    }
}
