using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using LlmAgents.SettingsUi;
using Microsoft.Xna.Framework;

namespace LlmAgents.Client;

/// <summary>
/// Marks the bots under full AI control in the crew list: the "Full AI control" icon next to the bot's other icons
/// (the order itself never reaches the clients, the server keeps it). The client asks the server every few seconds
/// who is under control (<see cref="ControlNet"/>); a server without the mod never answers, and nothing is shown.
/// </summary>
internal sealed class ControlMarks
{
    static readonly Identifier ControlId = "llmcontrol".ToIdentifier();
    const string IconTag = "llmcontrolicon";
    const double AskEvery = 2, ForgetAfter = 10, ApplyEvery = 0.5;

    readonly INetworkingService net;
    HashSet<ushort> controlled = new();
    double askAt, applyAt, answeredAt = double.NegativeInfinity;

    public ControlMarks(INetworkingService net)
    {
        this.net = net;
        net.Receive(ControlNet.MessageId, (NetMessageReceived)OnMessage);
    }

    void OnMessage(IReadMessage msg)
    {
        try
        {
            int n = Math.Min((int)msg.ReadUInt16(), ControlNet.MaxIds);
            var ids = new HashSet<ushort>();
            for (int i = 0; i < n; i++) ids.Add(msg.ReadUInt16());
            controlled = ids;
            answeredAt = Timing.TotalTime;
            applyAt = 0;   // show it right away
        }
        catch { /* a malformed answer: keep the last one */ }
    }

    public void Update()
    {
        double now = Timing.TotalTime;
        bool inRound = GameMain.Client != null && GameMain.GameSession?.IsRunning == true;
        if (inRound && now >= askAt)
        {
            askAt = now + AskEvery;
            var m = net.Start(ControlNet.MessageId);
            net.SendToServer(m, DeliveryMethod.Unreliable);
        }
        if (!inRound || now - answeredAt > ForgetAfter) controlled.Clear();
        if (now < applyAt) return;
        applyAt = now + ApplyEvery;
        Apply();
    }

    /// <summary>Puts the icon into each controlled bot's crew list entry (the free right half of its "extra icons"
    /// box: the left half holds the bot's objective icon) and hides it on the others.</summary>
    void Apply()
    {
        var list = GameMain.GameSession?.CrewManager?.crewList;
        if (list == null) return;
        OrderPrefab.Prefabs.TryGet(ControlId, out var prefab);
        foreach (var entry in list.Content.Children)
        {
            if (entry.UserData is not Character c) continue;
            var extra = entry.FindChild(x => x is GUILayoutGroup)?.GetChildByUserData("extraicons");
            if (extra == null) continue;
            var icon = extra.GetChildByUserData(IconTag);
            bool on = c.IsBot && controlled.Contains(c.ID) && prefab?.SymbolSprite != null;
            if (!on)
            {
                if (icon != null) icon.Visible = false;
                continue;
            }
            if (icon == null)
            {
                icon = new GUIImage(new RectTransform(new Vector2(0.8f), extra.RectTransform, Anchor.CenterRight, scaleBasis: ScaleBasis.Smallest),
                    prefab!.SymbolSprite, scaleToFit: true)
                {
                    UserData = IconTag,
                    Color = prefab.Color,
                    HoverColor = Color.Lerp(prefab.Color, Color.White, 0.5f),
                    ToolTip = TextManager.Get("tooltip.llmcontrolled").Fallback("Under full AI control"),
                    CanBeFocused = true,
                };
            }
            icon.Visible = true;
        }
    }

    public void Clear()
    {
        controlled.Clear();
        var list = GameMain.GameSession?.CrewManager?.crewList;
        if (list == null) return;
        foreach (var entry in list.Content.Children)
        {
            var extra = entry.FindChild(x => x is GUILayoutGroup)?.GetChildByUserData("extraicons");
            var icon = extra?.GetChildByUserData(IconTag);
            if (icon != null) extra!.RemoveChild(icon);
        }
    }
}
