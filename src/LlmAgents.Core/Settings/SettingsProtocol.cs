using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// Shared by the server plugin and the client settings window (the client project compiles this file too),
// so it must not depend on anything else in Core.
namespace LlmAgents.SettingsUi;

/// <summary>Field types the settings window knows how to draw.</summary>
public static class FieldType
{
    public const string Bool = "bool";
    public const string Int = "int";
    public const string Float = "float";
    public const string Enum = "enum";
    public const string String = "string";
    /// <summary>Multi-line text (prompts).</summary>
    public const string Text = "text";
    /// <summary>Read-only line.</summary>
    public const string Info = "info";
}

/// <summary>One editable value. The server builds the list; the window draws it generically.</summary>
public sealed class SettingField
{
    /// <summary>"config" (llm_config.json) or "personas" (personas.json).</summary>
    public string Target { get; set; } = "config";
    public string[] Path { get; set; } = Array.Empty<string>();
    /// <summary>Tab in the window.</summary>
    public string Category { get; set; } = "";
    /// <summary>Optional group header inside the tab.</summary>
    public string? Group { get; set; }
    public string Label { get; set; } = "";
    public string? Help { get; set; }
    public string Type { get; set; } = FieldType.String;
    public List<string>? Options { get; set; }
    public List<string>? OptionLabels { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public JsonNode? Value { get; set; }

    [JsonIgnore]
    public string Key => Target + ":" + string.Join("\u001f", Path);
}

public sealed class SettingChange
{
    public string Target { get; set; } = "config";
    public string[] Path { get; set; } = Array.Empty<string>();
    public JsonNode? Value { get; set; }
}

/// <summary>Client -> server.</summary>
public sealed class SettingsRequest
{
    /// <summary>"get" or "set".</summary>
    public string Op { get; set; } = "get";
    public List<SettingChange>? Changes { get; set; }
}

/// <summary>Server -> client.</summary>
public sealed class SettingsResponse
{
    /// <summary>"state" (fields to show) or "open" (please open the window and ask for the state).</summary>
    public string Type { get; set; } = "state";
    public bool CanEdit { get; set; }
    public string? Message { get; set; }
    public bool Error { get; set; }
    public List<string> Categories { get; set; } = new();
    public List<SettingField> Fields { get; set; } = new();
}

public static class SettingsNet
{
    /// <summary>LuaCs network message id (both directions).</summary>
    public const string MessageId = "llmagents.settings";
    public const int ProtocolVersion = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}
