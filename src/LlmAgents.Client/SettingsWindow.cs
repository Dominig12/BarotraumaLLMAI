using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Barotrauma;
using LlmAgents.SettingsUi;
using Microsoft.Xna.Framework;

namespace LlmAgents.Client;

/// <summary>
/// The settings window. It knows nothing about the mod's settings: the server sends a list of fields
/// (label, type, value, tab) and the window draws them, collects the edits and sends them back on "Apply".
/// Built on a GUIMessageBox, so the game shows and updates it on every screen (lobby and round).
/// </summary>
internal sealed class SettingsWindow
{
    readonly Action<SettingsRequest> send;
    GUIMessageBox? box;
    GUILayoutGroup? tabs;
    GUIListBox? list;
    GUITextBlock? status;
    SettingsResponse? state;
    string category = "";
    readonly Dictionary<string, SettingChange> pending = new();

    static readonly Color ErrorColor = new(255, 110, 110);
    static readonly Color OkColor = new(150, 230, 150);
    static readonly Color HeaderColor = new(120, 200, 255);

    readonly Action? openMonitor;

    public SettingsWindow(Action<SettingsRequest> send, Action? openMonitor = null)
    {
        this.send = send;
        this.openMonitor = openMonitor;
    }

    public bool IsOpen => box != null && !box.Closed;

    public void Toggle()
    {
        if (IsOpen) Close(); else Open();
    }

    public void Open()
    {
        if (!IsOpen) Build();
        SetStatus("Загрузка настроек с сервера...");
        send(new SettingsRequest { Op = "get" });
    }

    public void Close()
    {
        if (box != null && !box.Closed) box.Close();
        box = null;
        pending.Clear();
    }

    public void OnResponse(SettingsResponse r)
    {
        if (r.Type == "open")
        {
            Open();
            return;
        }
        if (!IsOpen) return;
        if (!r.CanEdit)
        {
            SetStatus(r.Message ?? "Нет прав", error: true);
            return;
        }
        state = r;
        pending.Clear();
        if (!r.Categories.Contains(category)) category = r.Categories.FirstOrDefault() ?? "";
        RebuildTabs();
        RebuildFields();
        SetStatus(r.Message ?? $"Настройки загружены ({r.Fields.Count})", r.Error);
    }

    public void SetStatus(string text, bool error = false)
    {
        if (status == null) return;
        status.Text = text;
        status.TextColor = error ? ErrorColor : OkColor;
    }

    // ------------------------------------------------------------------ layout

    void Build()
    {
        box = new GUIMessageBox("", "", Array.Empty<LocalizedString>(), relativeSize: new Vector2(0.62f, 0.82f), minSize: new Point(860, 560));
        box.Content.Visible = false; // the stock header/buttons; we draw our own layout

        var main = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.94f), box.InnerFrame.RectTransform, Anchor.Center))
        {
            Stretch = true,
            RelativeSpacing = 0.01f,
        };
        new GUITextBlock(new RectTransform(new Vector2(1f, 0.05f), main.RectTransform), "LLM-агенты: настройки", font: GUIStyle.SubHeadingFont);
        tabs = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.05f), main.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.004f };
        list = new GUIListBox(new RectTransform(new Vector2(1f, 0.74f), main.RectTransform)) { Spacing = 4 };
        status = new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), main.RectTransform), "", wrap: true, font: GUIStyle.SmallFont);

        var buttons = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.06f), main.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterRight)
        {
            RelativeSpacing = 0.01f,
        };
        new GUIButton(new RectTransform(new Vector2(0.13f, 1f), buttons.RectTransform), "Обновить")
        {
            ToolTip = "Загрузить свежие значения и статус ботов с сервера (несохранённые правки пропадут).",
            OnClicked = (_, _) => { send(new SettingsRequest { Op = "get" }); SetStatus("Обновляю..."); return true; },
        };
        new GUIButton(new RectTransform(new Vector2(0.16f, 1f), buttons.RectTransform), "Применить")
        {
            OnClicked = (_, _) => { Apply(); return true; },
        };
        new GUIButton(new RectTransform(new Vector2(0.19f, 1f), buttons.RectTransform), "Отменить изменения")
        {
            OnClicked = (_, _) => { pending.Clear(); RebuildFields(); SetStatus("Изменения отменены"); return true; },
        };
        new GUIButton(new RectTransform(new Vector2(0.17f, 1f), buttons.RectTransform), "Перечитать файлы")
        {
            ToolTip = "Перечитать llm_config.json и personas.json с диска (если правили их руками).",
            OnClicked = (_, _) => { send(new SettingsRequest { Op = "reload" }); SetStatus("Перечитываю..."); return true; },
        };
        if (openMonitor != null)
            new GUIButton(new RectTransform(new Vector2(0.15f, 1f), buttons.RectTransform), "Монитор ботов")
            {
                ToolTip = "Чем заняты боты: цели, планы, действия, статистика (Ctrl+Shift+M).",
                OnClicked = (_, _) => { openMonitor(); return true; },
            };
        new GUIButton(new RectTransform(new Vector2(0.11f, 1f), buttons.RectTransform), "Закрыть")
        {
            OnClicked = (_, _) => { Close(); return true; },
        };
    }

    void RebuildTabs()
    {
        if (tabs == null || state == null) return;
        tabs.ClearChildren();
        foreach (var c in state.Categories)
        {
            var cat = c;
            new GUIButton(new RectTransform(new Vector2(1f / Math.Max(1, state.Categories.Count), 1f), tabs.RectTransform), cat, style: "GUITabButton")
            {
                Selected = cat == category,
                OnClicked = (b, _) =>
                {
                    category = cat;
                    foreach (var child in tabs.Children.OfType<GUIButton>()) child.Selected = child == b;
                    RebuildFields();
                    return true;
                },
            };
        }
        tabs.Recalculate();
    }

    void RebuildFields()
    {
        if (list == null || state == null) return;
        list.ClearChildren();
        string? group = null;
        foreach (var f in state.Fields.Where(f => f.Category == category))
        {
            if (f.Group != group)
            {
                group = f.Group;
                if (group != null)
                {
                    var header = new GUITextBlock(new RectTransform(new Vector2(1f, 0.055f), list.Content.RectTransform), group,
                        font: GUIStyle.SubHeadingFont, textColor: HeaderColor) { CanBeFocused = false };
                    header.RectTransform.MinSize = new Point(0, GUI.IntScale(30));
                }
            }
            AddRow(f);
        }
        if (state.Fields.All(f => f.Category != category))
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), list.Content.RectTransform), "Здесь пока ничего нет.");
        list.RecalculateChildren();
        list.UpdateScrollBarSize();
    }

    JsonNode? Current(SettingField f) => pending.TryGetValue(f.Key, out var c) ? c.Value : f.Value;

    void Change(SettingField f, JsonNode? value)
    {
        if (JsonNode.DeepEquals(value, f.Value)) pending.Remove(f.Key);
        else pending[f.Key] = new SettingChange { Target = f.Target, Path = f.Path, Value = value };
        SetStatus(pending.Count == 0 ? "Нет несохранённых изменений" : $"Несохранённых изменений: {pending.Count}. Нажмите «Применить».");
    }

    void Apply()
    {
        if (pending.Count == 0)
        {
            SetStatus("Нет изменений");
            return;
        }
        send(new SettingsRequest { Op = "set", Changes = pending.Values.ToList() });
        SetStatus("Сохраняю...");
    }

    void AddRow(SettingField f)
    {
        bool tall = f.Type == FieldType.Text;
        var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, tall ? 0.24f : 0.06f), list!.Content.RectTransform),
            isHorizontal: true, childAnchor: tall ? Anchor.TopLeft : Anchor.CenterLeft)
        {
            Stretch = true,
            RelativeSpacing = 0.02f,
        };
        row.RectTransform.MinSize = new Point(0, GUI.IntScale(tall ? 120 : 32));
        var tip = f.Help ?? "";

        var label = new GUITextBlock(new RectTransform(new Vector2(0.38f, 1f), row.RectTransform), f.Label, wrap: true,
            textAlignment: tall ? Alignment.TopLeft : Alignment.CenterLeft);
        if (tip.Length > 0) label.ToolTip = tip;

        var area = new RectTransform(new Vector2(0.6f, 1f), row.RectTransform);
        var value = Current(f);
        switch (f.Type)
        {
            case FieldType.Bool:
            {
                var tick = new GUITickBox(area, "") { Selected = Bool(value) };
                if (tip.Length > 0) tick.ToolTip = tip;
                tick.OnSelected = t => { Change(f, JsonValue.Create(t.Selected)); return true; };
                break;
            }
            case FieldType.Int:
            {
                var n = new GUINumberInput(area, NumberType.Int);
                if (f.Min != null) n.MinValueInt = (int)f.Min.Value;
                if (f.Max != null) n.MaxValueInt = (int)f.Max.Value;
                n.IntValue = (int)Math.Round(Num(value));
                if (tip.Length > 0) n.ToolTip = tip;
                n.OnValueChanged = x => Change(f, JsonValue.Create(x.IntValue));
                break;
            }
            case FieldType.Float:
            {
                var n = new GUINumberInput(area, NumberType.Float) { DecimalsToDisplay = 2 };
                if (f.Min != null) n.MinValueFloat = (float)f.Min.Value;
                if (f.Max != null) n.MaxValueFloat = (float)f.Max.Value;
                n.FloatValue = (float)Num(value);
                if (tip.Length > 0) n.ToolTip = tip;
                n.OnValueChanged = x => Change(f, JsonValue.Create(Math.Round((double)x.FloatValue, 3)));
                break;
            }
            case FieldType.Enum:
            {
                var options = f.Options ?? new List<string>();
                var labels = f.OptionLabels ?? options;
                var dd = new GUIDropDown(area, elementCount: Math.Clamp(options.Count, 1, 10));
                for (int i = 0; i < options.Count; i++)
                    dd.AddItem(i < labels.Count && labels[i].Length > 0 ? labels[i] : (options[i].Length > 0 ? options[i] : "—"), options[i]);
                int sel = options.IndexOf(Str(value));
                if (sel >= 0) dd.Select(sel);
                if (tip.Length > 0) dd.ToolTip = tip;
                dd.OnSelected = (_, obj) => { Change(f, JsonValue.Create(obj as string ?? "")); return true; };
                break;
            }
            case FieldType.String:
            case FieldType.Text:
            {
                var tb = new GUITextBox(area, Str(value), wrap: tall, textAlignment: tall ? Alignment.TopLeft : Alignment.CenterLeft)
                {
                    MaxTextLength = tall ? 1500 : 200,
                };
                if (tip.Length > 0) tb.ToolTip = tip;
                tb.OnTextChangedDelegate = (t, text) => { Change(f, JsonValue.Create(text)); return true; };
                break;
            }
            default:
                new GUITextBlock(area, Str(value), wrap: true) { ToolTip = tip };
                break;
        }
    }

    static bool Bool(JsonNode? v) => v is JsonValue j && j.TryGetValue<bool>(out var b) && b;

    static double Num(JsonNode? v)
    {
        if (v is not JsonValue j) return 0;
        if (j.TryGetValue<double>(out var d)) return d;
        if (j.TryGetValue<int>(out var i)) return i;
        return double.TryParse(j.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    static string Str(JsonNode? v) => v switch
    {
        null => "",
        JsonValue j when j.TryGetValue<string>(out var s) => s,
        _ => v.ToJsonString(),
    };
}
