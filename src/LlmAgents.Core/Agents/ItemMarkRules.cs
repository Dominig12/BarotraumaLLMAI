using System;
using System.Globalization;

namespace LlmAgents.Agents;

/// <summary>Exact marker request contract, shared with the game backend. No interpretation of item words.</summary>
public static class ItemMarkRules
{
    public const int MaxNoteLength = 300;

    public static string? Validate(string? item, string? mark, bool on, string? note)
    {
        if (item == null || item.Length < 2 || item[0] != '#' ||
            !ushort.TryParse(item.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return "item must be an exact current #ID; inspect items before marking";
        if (mark is not ("attention" or "deconstruct")) return "mark must be attention or deconstruct";
        if (!string.IsNullOrWhiteSpace(note) && (mark != "attention" || !on))
            return "note is allowed only when setting an attention marker";
        if (note?.Length > MaxNoteLength) return $"note must be at most {MaxNoteLength} characters";
        return null;
    }
}
