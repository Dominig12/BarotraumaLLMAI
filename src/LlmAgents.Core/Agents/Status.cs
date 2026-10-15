using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LlmAgents.Llm;

namespace LlmAgents
{
    public static class ModInfo
    {
        public const string Version = "0.4.0";
    }
}

namespace LlmAgents.Agents
{
    public sealed record FeatureStatus(string Name, bool Ok, string? Note = null);

    public sealed record BotStatus(int Id, string Name, string Job, string Provider, bool AiControl, bool HasPersona,
        string Orders, string? Task, string? LastDecision, double? SecondsSinceDecision, IReadOnlyList<string> RecentErrors,
        bool Thinking, int Decisions, string? Goal = null, string? Plan = null);

    /// <summary>Everything !llm status and the settings window's status tab show.</summary>
    public sealed record ModStatus(string Version, bool Enabled, IReadOnlyList<FeatureStatus> Features,
        IReadOnlyList<LlmRouter.ProviderStatus> Providers, int RequestsToday, int DailyBudget,
        IReadOnlyList<BotStatus> Bots, IReadOnlyList<string> DisabledCharacters, string? MemoryScope)
    {
        public static string Ago(double? s) => s == null ? "never" : s < 90 ? $"{s:0}s ago" : $"{s / 60:0} min ago";

        /// <summary>Plain text for the chat (several short lines).</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append($"LlmAgents {Version}{(Enabled ? "" : " (DISABLED)")}");
            if (MemoryScope != null) sb.Append($", memory: {MemoryScope}");
            sb.Append('\n');
            var bad = Features.Where(f => !f.Ok).ToList();
            sb.Append(bad.Count == 0 ? "Features: all OK" : "PROBLEMS: " + string.Join("; ", bad.Select(f => f.Name + (f.Note != null ? $" ({f.Note})" : ""))));
            sb.Append('\n');
            sb.Append($"Requests today: {RequestsToday}{(DailyBudget > 0 ? $"/{DailyBudget}" : "")}\n");
            foreach (var p in Providers)
            {
                sb.Append($"{p.Name} ({p.Model}): ok {p.Ok}, errors {p.Errors}, avg {p.AvgSeconds:0.0}s, busy {p.Active}");
                if (p.PausedForSeconds > 0) sb.Append($", PAUSED {p.PausedForSeconds:0}s");
                if (p.LastError != null) sb.Append($", last error: {p.LastError}");
                sb.Append('\n');
            }
            foreach (var b in Bots)
            {
                sb.Append($"{b.Name} ({b.Job}{(b.AiControl ? ", FULL CONTROL" : "")}): {b.Orders}");
                if (b.Task != null) sb.Append($"; task: {b.Task}");
                if (b.Goal != null) sb.Append($"; goal: {Tools_Clip(b.Goal, 80)}");
                if (b.Plan != null) sb.Append($"; plan: {Tools_Clip(b.Plan, 90)}");
                sb.Append($"; {(b.Thinking ? "thinking now" : "last decision " + Ago(b.SecondsSinceDecision))}");
                if (b.LastDecision != null) sb.Append($": {Tools_Clip(b.LastDecision, 90)}");
                if (b.RecentErrors.Count > 0) sb.Append($"; last error: {Tools_Clip(b.RecentErrors[^1], 90)}");
                sb.Append('\n');
            }
            if (DisabledCharacters.Count > 0) sb.Append("Vanilla AI only: " + string.Join(", ", DisabledCharacters) + "\n");
            return sb.ToString().TrimEnd();
        }

        static string Tools_Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
