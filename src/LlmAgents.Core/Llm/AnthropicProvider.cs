using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LlmAgents.Llm;

/// <summary>Anthropic Messages API with tool use.</summary>
public sealed class AnthropicProvider : ILlmProvider
{
    public const string ApiVersion = "2023-06-01";

    readonly ProviderConfig cfg;
    readonly HttpClient http;

    public string Name => cfg.Name;

    public AnthropicProvider(ProviderConfig cfg, HttpClient http)
    {
        this.cfg = cfg;
        this.http = http;
    }

    public static JsonObject BuildBody(ProviderConfig cfg, LlmRequest req)
    {
        var messages = new JsonArray();
        foreach (var t in NormalizeTurns(req.Turns))
            messages.Add(new JsonObject { ["role"] = t.Role, ["content"] = t.Text });

        bool promptMode = cfg.ToolMode == "prompt" && req.Tools.Count > 0;
        var body = new JsonObject
        {
            ["model"] = cfg.Model,
            ["max_tokens"] = req.MaxOutputTokens,
            ["temperature"] = req.Temperature,
            // the system prompt and the tools are the same for every turn of a bot: cached by the API (cheaper, faster)
            ["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = promptMode ? ToolPrompt.Build(req.System, req.Tools) : req.System,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                },
            },
            ["messages"] = messages,
        };

        if (req.Tools.Count > 0 && !promptMode)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.Parameters.DeepClone(),
                });
            }
            if (tools.Count > 0 && tools[^1] is JsonObject lastTool) lastTool["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            body["tools"] = tools;
        }
        JsonUtil.Merge(body, cfg.ExtraBody);
        return body;
    }

    public static LlmResponse ParseResponse(string raw, IReadOnlyList<ToolSpec> tools)
    {
        var root = JsonNode.Parse(raw) as JsonObject ?? throw new FormatException("response is not a JSON object");
        if (JsonUtil.Str(root["type"]) == "error")
            throw new FormatException("provider error: " + JsonUtil.Trim(root["error"]?.ToJsonString() ?? raw));

        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        if (root["content"] is JsonArray blocks)
        {
            foreach (var b in blocks)
            {
                switch (JsonUtil.Str(b?["type"]))
                {
                    case "text":
                        text.Append(JsonUtil.Str(b?["text"]));
                        break;
                    case "tool_use":
                        var name = JsonUtil.Str(b?["name"]);
                        if (string.IsNullOrEmpty(name)) throw new FormatException("tool call is missing its name");
                        calls.Add(new ToolCall(name!, NativeToolArguments.Read(b?["input"], allowJsonString: false)));
                        break;
                }
            }
        }
        if (calls.Count == 0) calls = TextToolCallParser.Parse(text.ToString(), tools);

        return new LlmResponse(text.ToString(), calls,
            JsonUtil.Int(root["usage"]?["input_tokens"]),
            JsonUtil.Int(root["usage"]?["output_tokens"]),
            Truncated: JsonUtil.Str(root["stop_reason"]) == "max_tokens");
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest req, CancellationToken ct)
    {
        var body = BuildBody(cfg, req);
        using var msg = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.TryAddWithoutValidation("x-api-key", cfg.ResolvedKey);
        msg.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        foreach (var kv in cfg.ExtraHeaders) msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        string raw;
        try
        {
            using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            raw = await resp.Content.ReadCappedAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {JsonUtil.Trim(JsonUtil.OneLine(raw))}", null, resp.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"timed out after {cfg.TimeoutSeconds}s");
        }

        try
        {
            return ParseResponse(raw, req.Understood);
        }
        catch (JsonException e)
        {
            throw new FormatException("invalid JSON response: " + e.Message);
        }
    }

    /// <summary>The Messages API wants strictly alternating roles starting with "user".</summary>
    public static List<LlmTurn> NormalizeTurns(IReadOnlyList<LlmTurn> turns)
    {
        var result = new List<LlmTurn>();
        foreach (var t in turns)
        {
            if (result.Count == 0 && t.Role != "user") continue;
            if (result.Count > 0 && result[^1].Role == t.Role)
                result[^1] = new LlmTurn(t.Role, result[^1].Text + "\n" + t.Text);
            else
                result.Add(t);
        }
        return result;
    }
}
