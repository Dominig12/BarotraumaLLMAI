using System;
using Barotrauma;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;

namespace LlmAgents;

/// <summary>Receives chat messages players send to the server and forwards them as possible orders.</summary>
internal sealed class ChatListener : IEventChatMessage
{
    readonly Plugin plugin;

    public ChatListener(Plugin plugin) => this.plugin = plugin;

    public bool? OnChatMessage(string messageText, Client sender, ChatMessageType type, ChatMessage message)
    {
        try
        {
            plugin.OnPlayerChat(messageText, sender, type);
        }
        catch (Exception e)
        {
            Log.Error("chat handling failed: " + e.Message);
        }
        return null; // never block or alter the message
    }
}
