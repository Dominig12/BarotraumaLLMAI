using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Barotrauma;
using LlmAgents.SettingsUi;
using Microsoft.Xna.Framework;

namespace LlmAgents.Client;

/// <summary>
/// The bot monitor (Ctrl+Shift+M or "!llm monitor"): every AI bot with what it is doing, and for the selected one its
/// goal, plan, recent actions, notes and counters; one more tab for the whole crew (requests, models, director,
/// conversation). While the window is open it asks the server for a fresh snapshot about once a second; the lists
/// are only rebuilt when something in them changed, so scrolling is not reset all the time.
/// </summary>
internal sealed class MonitorWindow
{
    readonly Action<MonitorRequest> send;
    GUIMessageBox? box;
    GUITextBlock? header, status;
    GUIListBox? bots, content;
    GUILayoutGroup? tabs;
    MonitorSnapshot? snap;
    int selected;
    string tab = Overview;
    bool paused;
    double nextAsk, lastAnswer = double.NegativeInfinity;
    string botsKey = "", contentKey = "";

    const string Overview = "Обзор", Actions = "Действия", Memory = "Память и ошибки", Stats = "Статистика", Crew = "Весь экипаж";
    static readonly string[] Tabs = { Overview, Actions, Memory, Stats, Crew };
    const double AskEvery = 1.0;

    static readonly Color Head = new(120, 200, 255);
    static readonly Color Dim = new(160, 160, 160);
    static readonly Color Good = new(150, 230, 150);
    static readonly Color Warn = new(255, 200, 90);
    static readonly Color Bad = new(255, 110, 110);
    static readonly Color Text = new(225, 225, 225);

    public MonitorWindow(Action<MonitorRequest> send) { this.send = send; }

    public bool IsOpen => box != null && !box.Closed;

    public void Toggle()
    {
        if (IsOpen) Close(); else Open();
    }

    public void Open()
    {
        if (!IsOpen) Build();
        nextAsk = 0;
    }

    public void Close()
    {
        if (box != null && !box.Closed) box.Close();
        box = null;
        botsKey = contentKey = "";
    }

    public void Update()
    {
        if (!IsOpen) return;
        double now = Timing.TotalTime;
        if (GameMain.Client == null)
        {
            SetStatus("Монитор работает только в мультиплеере, на сервере с модом LlmAgents.", Bad);
            return;
        }
        if (!paused && now >= nextAsk)
        {
            nextAsk = now + AskEvery;
            send(new MonitorRequest { Bot = selected, Activity = tab == Actions });
        }
        if (snap == null) SetStatus("Жду ответа сервера...", Dim);
        else if (now - lastAnswer > 6) SetStatus("Сервер давно не отвечает: данные могут быть устаревшими.", Warn);
    }

    public void OnSnapshot(MonitorSnapshot s)
    {
        if (!IsOpen) return;
        snap = s;
        lastAnswer = Timing.TotalTime;
        if (selected == 0 || s.Bots.All(b => b.Id != selected))
        {
            int first = s.Bots.FirstOrDefault()?.Id ?? 0;
            if (first != selected)
            {
                selected = first;
                nextAsk = 0;   // ask again for this bot's details
            }
        }
        Redraw();
        SetStatus(s.Message ?? (paused ? "Обновление на паузе" : $"Обновлено · LlmAgents {s.Version}"), s.Message != null ? Bad : Dim);
    }

    // ------------------------------------------------------------------ layout

    void Build()
    {
        box = new GUIMessageBox("", "", Array.Empty<LocalizedString>(), relativeSize: new Vector2(0.72f, 0.85f), minSize: new Point(980, 600));
        box.Content.Visible = false;

        var main = new GUILayoutGroup(new RectTransform(new Vector2(0.96f, 0.95f), box.InnerFrame.RectTransform, Anchor.Center))
        {
            Stretch = true,
            RelativeSpacing = 0.008f,
        };
        new GUITextBlock(new RectTransform(new Vector2(1f, 0.045f), main.RectTransform), "LLM-агенты: монитор ботов", font: GUIStyle.SubHeadingFont);
        header = new GUITextBlock(new RectTransform(new Vector2(1f, 0.04f), main.RectTransform), "", font: GUIStyle.SmallFont, textColor: Dim);

        var body = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.8f), main.RectTransform), isHorizontal: true)
        {
            Stretch = true,
            RelativeSpacing = 0.01f,
        };
        bots = new GUIListBox(new RectTransform(new Vector2(0.3f, 1f), body.RectTransform)) { Spacing = 3 };
        var right = new GUILayoutGroup(new RectTransform(new Vector2(0.69f, 1f), body.RectTransform)) { Stretch = true, RelativeSpacing = 0.008f };
        tabs = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.06f), right.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.004f };
        foreach (var t in Tabs)
        {
            var name = t;
            new GUIButton(new RectTransform(new Vector2(1f / Tabs.Length, 1f), tabs.RectTransform), name, style: "GUITabButton")
            {
                Selected = name == tab,
                OnClicked = (b, _) =>
                {
                    tab = name;
                    foreach (var child in tabs.Children.OfType<GUIButton>()) child.Selected = child == b;
                    contentKey = "";
                    nextAsk = 0;
                    Redraw(resetScroll: true);
                    return true;
                },
            };
        }
        content = new GUIListBox(new RectTransform(new Vector2(1f, 0.93f), right.RectTransform)) { Spacing = 2 };

        var bottom = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.055f), main.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
        {
            Stretch = true,
            RelativeSpacing = 0.01f,
        };
        status = new GUITextBlock(new RectTransform(new Vector2(0.6f, 1f), bottom.RectTransform), "", font: GUIStyle.SmallFont, textColor: Dim);
        new GUIButton(new RectTransform(new Vector2(0.2f, 1f), bottom.RectTransform), "Пауза")
        {
            ToolTip = "Остановить обновление, чтобы спокойно прочитать (данные перестанут меняться).",
            OnClicked = (b, _) =>
            {
                paused = !paused;
                b.Text = paused ? "Продолжить" : "Пауза";
                nextAsk = 0;
                SetStatus(paused ? "Обновление на паузе" : "Обновляю...", Dim);
                return true;
            },
        };
        new GUIButton(new RectTransform(new Vector2(0.18f, 1f), bottom.RectTransform), "Закрыть")
        {
            OnClicked = (_, _) => { Close(); return true; },
        };
    }

    void SetStatus(string text, Color color)
    {
        if (status == null) return;
        status.Text = text;
        status.TextColor = color;
    }

    // ------------------------------------------------------------------ drawing

    void Redraw(bool resetScroll = false)
    {
        if (snap == null || bots == null || content == null) return;
        if (header != null) header.Text = HeaderLine(snap);
        DrawBots(snap);
        var lines = new List<Line>();
        var bot = snap.Bots.FirstOrDefault(b => b.Id == selected);
        var d = snap.Details != null && snap.Details.Id == selected ? snap.Details : null;
        switch (tab)
        {
            case Overview: OverviewLines(lines, bot, d); break;
            case Actions: ActionLines(lines, bot, d); break;
            case Memory: MemoryLines(lines, bot, d); break;
            case Stats: StatLines(lines, bot, d); break;
            default: CrewLines(lines, snap); break;
        }
        var key = tab + "\n" + string.Join("\n", lines.Select(l => l.Text));
        if (key == contentKey && !resetScroll) return;
        contentKey = key;
        float scroll = content.BarScroll;
        content.ClearChildren();
        foreach (var l in lines) AddLine(content, l);
        content.RecalculateChildren();
        content.UpdateScrollBarSize();
        content.BarScroll = resetScroll ? 0 : scroll;
    }

    static string HeaderLine(MonitorSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append($"Раунд: {Duration(s.RoundSeconds)} · запросов: {s.RequestsLastMinute}/мин, {s.RequestsToday}");
        if (s.DailyBudget > 0) sb.Append($" из {s.DailyBudget}");
        sb.Append($" за сутки · токены: {Tokens(s.TokensIn)} вход, {Tokens(s.TokensOut)} выход");
        if (s.DirectorEnabled) sb.Append(s.DirectorActive ? (s.DirectorThinking ? " · директор думает" : " · директор работает") : " · директор на паузе");
        if (!s.Enabled) sb.Append(" · МОД ВЫКЛЮЧЕН");
        if (s.Problems.Count > 0) sb.Append($" · проблем: {s.Problems.Count}");
        return sb.ToString();
    }

    void DrawBots(MonitorSnapshot s)
    {
        var key = string.Join("\n", s.Bots.Select(b => $"{b.Id}|{b.Name}|{b.State}|{b.Goal}|{b.Doing}|{b.Health}|{b.AiControl}|{b.Directed}|{b.Errors}")) + "|" + selected;
        if (key == botsKey) return;
        botsKey = key;
        float scroll = bots!.BarScroll;
        bots.ClearChildren();
        if (s.Bots.Count == 0)
        {
            AddLine(bots, new Line("Ботов с LLM сейчас нет.", Dim));
        }
        foreach (var b in s.Bots)
        {
            var id = b.Id;
            var button = new GUIButton(new RectTransform(new Vector2(1f, 0.12f), bots.Content.RectTransform), "", style: "ListBoxElement")
            {
                Selected = id == selected,
                OnClicked = (_, _) =>
                {
                    selected = id;
                    nextAsk = 0;
                    botsKey = contentKey = "";
                    Redraw(resetScroll: true);
                    return true;
                },
            };
            button.RectTransform.MinSize = new Point(0, GUI.IntScale(58));
            var col = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.9f), button.RectTransform, Anchor.Center)) { CanBeFocused = false, Stretch = true };
            var title = b.Name + $"  ({b.Job})" + (b.AiControl ? "  ★ полный контроль" : b.Directed ? "  · директор" : "");
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.34f), col.RectTransform), title, font: GUIStyle.SmallFont, textColor: Head) { CanBeFocused = false };
            var state = StateText(b.State) + (b.Health >= 0 && b.Health < 100 ? $" · здоровье {b.Health}%" : "") + (b.Room != null ? $" · {b.Room}" : "");
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.33f), col.RectTransform), state, font: GUIStyle.SmallFont, textColor: StateColor(b.State)) { CanBeFocused = false };
            var what = b.Goal ?? b.Task ?? b.Doing ?? "—";
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.33f), col.RectTransform), Clip(what, 70), font: GUIStyle.SmallFont, textColor: Text) { CanBeFocused = false };
            button.ToolTip = $"{b.Name}: {StateText(b.State)}\nЦель: {b.Goal ?? "нет"}\nЗанят: {b.Doing ?? "—"}\nЗадача: {b.Task ?? "—"}\nПриказ: {b.Order ?? "—"}\nРешений: {b.Decisions}, ошибок: {b.Errors}";
        }
        bots.RecalculateChildren();
        bots.UpdateScrollBarSize();
        bots.BarScroll = scroll;
    }

    // ------------------------------------------------------------------ tabs

    static void OverviewLines(List<Line> l, MonitorBot? b, MonitorBotDetails? d)
    {
        if (b == null) { l.Add(new Line("Выберите бота слева.", Dim)); return; }
        l.Add(new Line($"{b.Name} ({b.Job})", Head, Big: true));
        l.Add(new Line("Состояние: " + StateText(b.State), StateColor(b.State)));
        if (b.AiControl) l.Add(new Line($"Полный контроль ИИ (выдал: {d?.ControlBy ?? "?"})", Head));
        else if (b.Directed) l.Add(new Line("Работу выдаёт директор", Head));
        if (d?.NotAssignable != null) l.Add(new Line("Директор не трогает: " + d.NotAssignable, Dim));

        Section(l, "Цель");
        if (b.Goal == null) l.Add(new Line("нет", Dim));
        else l.Add(new Line(b.Goal + (d?.GoalSource != null ? $"  ({d.GoalSource}{(d.GoalAgo != null ? ", " + Ago(d.GoalAgo.Value) : "")})" : ""), Text));

        Section(l, "План");
        if (d == null) l.Add(new Line("загрузка...", Dim));
        else if (d.PlanNow == null && d.PlanSteps.Count == 0 && d.PlanTree == null) l.Add(new Line("плана нет", Dim));
        else
        {
            if (d.PlanAgo != null) l.Add(new Line($"начат {Ago(d.PlanAgo.Value)}", Dim));
            if (d.PlanNow != null) l.Add(new Line("▶ сейчас: " + d.PlanNow, Good));
            if (d.PlanTree != null) foreach (var part in TreeLines(d.PlanTree)) l.Add(new Line(part, part.Contains("<NOW>") ? Good : part.Contains("(done)") ? Dim : Text));
            for (int i = 0; i < d.PlanSteps.Count; i++) l.Add(new Line($"{i + 1}. {d.PlanSteps[i]}", Text));
        }

        Section(l, "В игре");
        l.Add(new Line("Занят: " + (b.Doing ?? "—"), Text));
        l.Add(new Line("Задача мода: " + (b.Task ?? "—"), Text));
        l.Add(new Line("Ванильный приказ: " + (b.Order ?? "—"), Text));
        l.Add(new Line("Отсек: " + (b.Room ?? "снаружи / неизвестно"), Text));
        if (d != null)
        {
            var hp = b.Health >= 0 ? $"здоровье {b.Health}%" : "здоровье ?";
            l.Add(new Line($"{hp}, кислород {(d.Oxygen >= 0 ? d.Oxygen + "%" : "?")}" + (d.SeenAgo != null ? $"  (осмотр {Ago(d.SeenAgo.Value)})" : ""), b.Health is >= 0 and < 50 ? Bad : Text));
            if (d.Injuries != null) l.Add(new Line("Травмы: " + d.Injuries, Warn));
            if (d.Gear != null) l.Add(new Line("Снаряжение: " + d.Gear, Text));
            if (d.Equipped.Count > 0) l.Add(new Line("В руках и на себе: " + string.Join(", ", d.Equipped), Text));
            if (d.Inventory.Count > 0) l.Add(new Line("В инвентаре: " + string.Join(", ", d.Inventory), Text));
        }

        Section(l, "Последнее решение");
        if (d?.LastDecision == null) l.Add(new Line("ещё не было", Dim));
        else
        {
            if (d.LastDecisionAgo != null) l.Add(new Line(Ago(d.LastDecisionAgo.Value) + $", модель {d.Provider}{(d.Persona ? ", с характером" : "")}", Dim));
            if (d.LastWhy != null) l.Add(new Line("Почему: " + d.LastWhy, Dim));
            l.Add(new Line(d.LastDecision, Text));
        }
        if (d != null && d.PendingEvents.Count > 0)
        {
            Section(l, "Ждёт решения");
            foreach (var e in d.PendingEvents) l.Add(new Line("• " + e, Warn));
        }
    }

    static void ActionLines(List<Line> l, MonitorBot? b, MonitorBotDetails? d)
    {
        if (b == null) { l.Add(new Line("Выберите бота слева.", Dim)); return; }
        l.Add(new Line($"{b.Name}: последние действия (новые сверху)", Head, Big: true));
        if (d == null) { l.Add(new Line("загрузка...", Dim)); return; }
        if (d.Activity.Count == 0) { l.Add(new Line("пока ничего", Dim)); return; }
        foreach (var a in d.Activity)
        {
            var (label, color) = KindOf(a.Kind);
            var when = a.Ago < 0 ? "" : Ago(a.Ago) + " · ";
            l.Add(new Line($"{when}{label}: {a.Text}", color));
        }
    }

    static void MemoryLines(List<Line> l, MonitorBot? b, MonitorBotDetails? d)
    {
        if (b == null) { l.Add(new Line("Выберите бота слева.", Dim)); return; }
        l.Add(new Line($"{b.Name}: память", Head, Big: true));
        if (d == null) { l.Add(new Line("загрузка...", Dim)); return; }
        Section(l, "Заметки (remember)");
        if (d.Notes.Count == 0) l.Add(new Line("нет", Dim));
        foreach (var n in d.Notes) l.Add(new Line("• " + n, Text));
        Section(l, "Прежние приказы");
        if (d.PastOrders.Count == 0) l.Add(new Line("нет", Dim));
        foreach (var o in d.PastOrders) l.Add(new Line("• " + o, Text));
        Section(l, "Последние ошибки");
        if (d.Errors.Count == 0) l.Add(new Line("нет", Good));
        foreach (var e in d.Errors) l.Add(new Line("• " + e, Bad));
    }

    static void StatLines(List<Line> l, MonitorBot? b, MonitorBotDetails? d)
    {
        if (b == null) { l.Add(new Line("Выберите бота слева.", Dim)); return; }
        l.Add(new Line($"{b.Name}: статистика за раунд", Head, Big: true));
        if (d == null) { l.Add(new Line("загрузка...", Dim)); return; }
        Section(l, "Запросы к модели");
        l.Add(new Line($"Модель: {d.Provider}", Text));
        l.Add(new Line($"Запросов: {d.Requests}, неудачных: {d.FailedRequests}" + (d.Requests > 0 ? $" ({100.0 * d.FailedRequests / d.Requests:0}%)" : ""), d.FailedRequests > 0 ? Warn : Text));
        l.Add(new Line($"Решений: {b.Decisions}, среднее время ответа: {d.AvgLatency:0.0} с", Text));
        l.Add(new Line($"Токены: {Tokens(d.TokensIn)} вход, {Tokens(d.TokensOut)} выход" + (d.ReasoningTokens > 0 ? $", из них на размышления {Tokens(d.ReasoningTokens)}" : "") +
            (b.Decisions > 0 ? $"; в среднем {Tokens(d.TokensIn / Math.Max(1, d.Requests - d.FailedRequests))} на запрос" : ""), Text));
        Section(l, "Работа");
        l.Add(new Line($"Вызовов инструментов: {d.ToolCalls}, с ошибкой: {d.ToolErrors}" + (d.ToolCalls > 0 ? $" ({100.0 * d.ToolErrors / d.ToolCalls:0}%)" : ""), d.ToolErrors > 0 ? Warn : Text));
        l.Add(new Line($"Задач выполнено: {d.TasksDone}, не удалось: {d.TasksFailed}", d.TasksFailed > 0 ? Warn : Text));
        l.Add(new Line($"Приказов получено: {d.OrdersReceived}" + (d.DirectorAssignments > 0 ? $", заданий от директора: {d.DirectorAssignments}" : ""), Text));
        Section(l, "Чаще всего вызывал");
        if (d.TopTools.Count == 0) l.Add(new Line("пока ничего", Dim));
        else l.Add(new Line(string.Join(", ", d.TopTools), Text));
    }

    static void CrewLines(List<Line> l, MonitorSnapshot s)
    {
        l.Add(new Line("Весь экипаж", Head, Big: true));
        l.Add(new Line($"Раунд идёт {Duration(s.RoundSeconds)}; боты думают: {s.Autonomy}", Text));
        if (!s.Enabled) l.Add(new Line("Мод выключен в настройках.", Bad));

        Section(l, "Боты");
        if (s.Bots.Count == 0) l.Add(new Line("нет", Dim));
        foreach (var g in s.Bots.GroupBy(b => b.State).OrderBy(g => g.Key))
            l.Add(new Line($"{StateText(g.Key)}: {string.Join(", ", g.Select(b => b.Name))}", StateColor(g.Key)));
        l.Add(new Line($"Решений всего: {s.Bots.Sum(b => b.Decisions)}, ошибок: {s.Bots.Sum(b => b.Errors)}", Text));

        Section(l, "Запросы");
        l.Add(new Line($"За минуту: {s.RequestsLastMinute}; за сутки: {s.RequestsToday}" + (s.DailyBudget > 0 ? $" из {s.DailyBudget}" : ""), Text));
        l.Add(new Line($"Токены за раунд: {Tokens(s.TokensIn)} вход, {Tokens(s.TokensOut)} выход", Text));
        l.Add(new Line($"Диспетчер чата: запросов {s.DispatchRequests}, сообщений в очереди {s.PendingChats}", Text));

        Section(l, "Модели");
        foreach (var p in s.Providers)
        {
            var line = $"{p.Name} ({p.Model}): ответов {p.Ok}, ошибок {p.Errors}, в среднем {p.AvgSeconds:0.0} с, сейчас {p.Active}, за сутки {p.UsedToday}";
            if (p.PausedFor > 0) line += $", ПАУЗА {p.PausedFor:0} с";
            l.Add(new Line(line, p.PausedFor > 0 ? Bad : p.Errors > 0 ? Warn : Text));
            if (p.LastError != null) l.Add(new Line("   последняя ошибка: " + p.LastError, Dim));
        }

        Section(l, "Директор");
        if (!s.DirectorEnabled) l.Add(new Line("выключен", Dim));
        else
        {
            l.Add(new Line((s.DirectorActive ? (s.DirectorThinking ? "думает" : "работает") : "на паузе после ошибок: боты думают сами") + $"; решений: {s.DirectorDecisions}",
                s.DirectorActive ? Good : Warn));
            if (s.DirectorLast != null) l.Add(new Line((s.DirectorLastAgo != null ? Ago(s.DirectorLastAgo.Value) + ": " : "") + s.DirectorLast, Text));
            foreach (var n in s.DirectorNotes) l.Add(new Line("• " + n, Dim));
        }

        if (s.Problems.Count > 0)
        {
            Section(l, "Проблемы");
            foreach (var p in s.Problems) l.Add(new Line("• " + p, Bad));
        }

        Section(l, "Разговор (последние 15 минут)");
        if (s.Conversation.Count == 0) l.Add(new Line("тишина", Dim));
        foreach (var c in s.Conversation) l.Add(new Line(c, Text));
    }

    // ------------------------------------------------------------------ helpers

    sealed record Line(string Text, Color Color, bool Big = false);

    static void Section(List<Line> l, string title) => l.Add(new Line(title, Head, Big: true));

    static void AddLine(GUIListBox list, Line line)
    {
        var tb = new GUITextBlock(new RectTransform(new Vector2(1f, 0f), list.Content.RectTransform), line.Text, textColor: line.Color,
            font: line.Big ? GUIStyle.SubHeadingFont : GUIStyle.SmallFont, wrap: true)
        {
            CanBeFocused = false,
        };
        tb.CalculateHeightFromText(padding: GUI.IntScale(line.Big ? 6 : 2));
    }

    /// <summary>A plan tree "n1 go_to_room(...); n2 if ... then [...]" one step per line.</summary>
    static IEnumerable<string> TreeLines(string tree)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < tree.Length; i++)
        {
            char c = tree[i];
            if (c is '[' or '(') depth++;
            else if (c is ']' or ')') depth--;
            else if (c == ';' && depth == 0)
            {
                parts.Add(tree.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        parts.Add(tree.Substring(start).Trim());
        return parts.Where(p => p.Length > 0).Select(p => "• " + p);
    }

    static string StateText(string s) => s switch
    {
        "thinking" => "думает (запрос к модели)",
        "task" => "выполняет задачу",
        "walking" => "идёт",
        "waiting" => "ждёт условия",
        "plan" => "выполняет план",
        "retry" => "ошибка запроса, повтор позже",
        "queued" => "ждёт своей очереди подумать",
        "dead" => "мёртв или без сознания",
        "idle" => "свободен",
        _ => s,
    };

    static Color StateColor(string s) => s switch
    {
        "thinking" => Warn,
        "task" or "walking" or "plan" => Good,
        "waiting" or "queued" => Head,
        "retry" or "dead" => Bad,
        _ => Dim,
    };

    static (string, Color) KindOf(string kind) => kind switch
    {
        "think" => ("решил", Head),
        "do" => ("сделал", Text),
        "task" => ("задача готова", Good),
        "task-failed" => ("задача не удалась", Bad),
        "order" => ("приказ", Warn),
        "director" => ("директор", Head),
        "error" => ("ошибка", Bad),
        _ => (kind, Text),
    };

    static string Ago(double s) => s < 1 ? "только что" : s < 90 ? $"{s:0} с назад" : s < 5400 ? $"{s / 60:0} мин назад" : $"{s / 3600:0.0} ч назад";

    static string Duration(double s) => s < 60 ? $"{s:0} с" : s < 3600 ? $"{(int)(s / 60)} мин" : $"{(int)(s / 3600)} ч {(int)(s % 3600 / 60)} мин";

    static string Tokens(long n) => n < 10_000 ? n.ToString() : n < 1_000_000 ? $"{n / 1000.0:0.#}k" : $"{n / 1_000_000.0:0.##}M";

    static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
