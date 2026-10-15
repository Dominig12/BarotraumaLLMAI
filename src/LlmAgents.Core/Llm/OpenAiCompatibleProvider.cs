using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LlmAgents.Llm;

/// <summary>Chat Completions API with tools. Works with OpenAI, Ollama (/v1), LM Studio, vLLM, OpenRouter, etc.</summary>
public sealed class OpenAiCompatibleProvider : ILlmProvider
{
    readonly ProviderConfig cfg;
    readonly HttpClient http;

    public string Name => cfg.Name;

    public OpenAiCompatibleProvider(ProviderConfig cfg, HttpClient http)
    {
        this.cfg = cfg;
        this.http = http;
    }

    public static JsonObject BuildBody(ProviderConfig cfg, LlmRequest req)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = req.System } };
        foreach (var t in req.Turns)
            messages.Add(new JsonObject { ["role"] = t.Role, ["content"] = t.Text });

        var body = new JsonObject
        {
            ["model"] = cfg.Model,
            ["messages"] = messages,
            ["max_tokens"] = req.MaxOutputTokens,
            ["temperature"] = req.Temperature,
            ["stream"] = false,
        };

        if (req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.Parameters.DeepClone(),
                    },
                });
            }
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        return body;
    }

    public static LlmResponse ParseResponse(string raw, ICollection<string> knownTools)
    {
        var root = JsonNode.Parse(raw) as JsonObject ?? throw new FormatException("response is not a JSON object");
        if (root["error"] is JsonNode err) throw new FormatException("provider error: " + JsonUtil.Trim(err.ToJsonString()));

        var message = root["choices"]?[0]?["message"];
        var text = JsonUtil.Str(message?["content"]) ?? "";

        var calls = new List<ToolCall>();
        if (message?["tool_calls"] is JsonArray arr)
        {
            foreach (var c in arr)
            {
                var fn = c?["function"];
                var name = JsonUtil.Str(fn?["name"]);
                if (string.IsNullOrEmpty(name)) continue;
                calls.Add(new ToolCall(name!, ParseArgs(fn?["arguments"])));
            }
        }
        if (calls.Count == 0) calls = TextToolCallParser.Parse(text, knownTools);

        return new LlmResponse(text, calls,
            JsonUtil.Int(root["usage"]?["prompt_tokens"]),
            JsonUtil.Int(root["usage"]?["completion_tokens"]));
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest req, CancellationToken ct)
    {
        var body = BuildBody(cfg, req);
        using var msg = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var key = cfg.ResolvedKey;
        if (!string.IsNullOrEmpty(key)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        foreach (var kv in cfg.ExtraHeaders) msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        string raw;
        try
        {
            using var resp = await http.SendAsync(msg, cts.Token).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {JsonUtil.Trim(raw)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"timed out after {cfg.TimeoutSeconds}s");
        }

        try
        {
            return ParseResponse(raw, req.Tools.Select(t => t.Name).ToHashSet());
        }
        catch (JsonException e)
        {
            throw new FormatException("invalid JSON response: " + e.Message);
        }
    }

    // OpenAI returns arguments as a JSON *string*; some servers (e.g. Ollama) return an object. Accept both.
    static JsonObject ParseArgs(JsonNode? node)
    {
        try
        {
            if (node is JsonObject o) return (JsonObject)o.DeepClone();
            var s = JsonUtil.Str(node);
            if (string.IsNullOrWhiteSpace(s)) return new JsonObject();
            return JsonNode.Parse(s!) as JsonObject ?? new JsonObject();
        }
        catch (JsonException) { return new JsonObject(); }
    }
}
