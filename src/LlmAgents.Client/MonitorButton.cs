using System;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace LlmAgents.Client;

/// <summary>
/// A small "AI monitor" button on the HUD, right of the top of the crew list (it slides with the list when the list
/// is hidden). It lives in the crew manager's own GUI frame, so the game updates and draws it with the crew list and
/// hides it whenever the crew list is hidden (health window, campaign map...).
/// </summary>
internal sealed class MonitorButton
{
    const string Tag = "llmagents.monitorbutton";
    readonly Action open;
    GUIButton? button;
    CrewManager? owner;

    public MonitorButton(Action open) { this.open = open; }

    public void Update()
    {
        var crew = GameMain.GameSession?.CrewManager;
        var frame = crew?.guiFrame;
        var area = crew?.crewArea;
        if (crew == null || frame == null || area == null || GameMain.Client == null)
        {
            Remove();
            return;
        }
        if (owner != crew || button == null || button.Parent != frame)
        {
            Remove();
            owner = crew;
            button = new GUIButton(new RectTransform(new Point(GUI.IntScale(130), GUI.IntScale(30)), frame.RectTransform, Anchor.TopLeft),
                "ИИ: монитор", style: "GUIButtonSmall")
            {
                UserData = Tag,
                ToolTip = "Монитор ботов: чем заняты, цели, планы, действия, статистика (Ctrl+Shift+M или !llm monitor)",
                OnClicked = (_, _) => { open(); return true; },
            };
        }
        button.Visible = area.Visible;
        // next to the top of the crew list, following it when the list slides out
        var r = area.Rect;
        int x = Math.Max(HUDLayoutSettings.Padding, r.Right + HUDLayoutSettings.Padding);
        button.RectTransform.AbsoluteOffset = new Point(x, r.Y);
    }

    public void Remove()
    {
        if (button?.Parent != null) button.Parent.RemoveChild(button);
        button = null;
        owner = null;
    }
}
