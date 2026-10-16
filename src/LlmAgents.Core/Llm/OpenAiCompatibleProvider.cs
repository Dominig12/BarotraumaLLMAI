using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    volatile bool jsonFormatUnsupported; // the server rejected response_format once; don't send it again

    public string Name => cfg.Name;

    public OpenAiCompatibleProvider(ProviderConfig cfg, HttpClient http)
    {
        this.cfg = cfg;
        this.http = http;
    }

    public static JsonObject BuildBody(ProviderConfig cfg, LlmRequest req) => BuildBody(cfg, req, allowJsonFormat: true);

    /// <summary>The server rejected the "reasoning" switch (official OpenAI and some others do): not sent again.</summary>
    bool reasoningSwitchUnsupported;

    /// <summary>thinking "off" for gateways that pass reasoning models through (OpenRouter style): the system prompt
    /// switch alone was ignored, and replies spent all their tokens thinking. Not for api.openai.com, and not when
    /// extraBody already says something about reasoning.</summary>
    static bool SendReasoningOff(ProviderConfig cfg) =>
        cfg.Thinking == "off" && !cfg.ExtraBody.ContainsKey("reasoning") && !cfg.ExtraBody.ContainsKey("reasoning_effort")
        && !cfg.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

    static JsonObject BuildBody(ProviderConfig cfg, LlmRequest req, bool allowJsonFormat, bool reasoningSwitch = true)
    {
        bool promptMode = cfg.ToolMode == "prompt" && req.Tools.Count > 0;
        var system = promptMode ? ToolPrompt.Build(req.System, req.Tools) : req.System;
        if (cfg.Thinking == "off") system += "\n" + Thinking.NoThinkSwitch;
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
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

        if (promptMode)
        {
            if (cfg.JsonResponseFormat && allowJsonFormat) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        else if (req.Tools.Count > 0)
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
        if (reasoningSwitch && SendReasoningOff(cfg)) body["reasoning"] = new JsonObject { ["enabled"] = false };
        JsonUtil.Merge(body, cfg.ExtraBody);
        return body;
    }

    public static LlmResponse ParseResponse(string raw, IReadOnlyList<ToolSpec> tools)
    {
        var root = JsonNode.Parse(raw) as JsonObject ?? throw new FormatException("response is not a JSON object");
        if (root["error"] is JsonNode err) throw new FormatException("provider error: " + JsonUtil.Trim(err.ToJsonString()));

        var choice = root["choices"]?[0];
        var message = choice?["message"];
        var text = Thinking.Strip(JsonUtil.Str(message?["content"]) ?? "");

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
        if (calls.Count == 0) calls = TextToolCallParser.Parse(text, tools);

        return new LlmResponse(text, calls,
            JsonUtil.Int(root["usage"]?["prompt_tokens"]),
            JsonUtil.Int(root["usage"]?["completion_tokens"]),
            Truncated: JsonUtil.Str(choice?["finish_reason"]) == "length",
            ReasoningTokens: JsonUtil.Int(root["usage"]?["completion_tokens_details"]?["reasoning_tokens"]));
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        string raw;
        try
        {
            var body = BuildBody(cfg, req, allowJsonFormat: !jsonFormatUnsupported, reasoningSwitch: !reasoningSwitchUnsupported);
            var (status, text) = await SendAsync(body, cts.Token).ConfigureAwait(false);
            if (status == HttpStatusCode.BadRequest && body.ContainsKey("reasoning") && text.Contains("reasoning", StringComparison.OrdinalIgnoreCase))
            {
                reasoningSwitchUnsupported = true;
                Log.Warn($"provider '{cfg.Name}' does not accept the reasoning switch; thinking: off now relies on the prompt only");
                body = BuildBody(cfg, req, allowJsonFormat: !jsonFormatUnsupported, reasoningSwitch: false);
                (status, text) = await SendAsync(body, cts.Token).ConfigureAwait(false);
            }
            if (status == HttpStatusCode.BadRequest && body.ContainsKey("response_format") &&
                text.Contains("response_format", StringComparison.OrdinalIgnoreCase))
            {
                // e.g. LM Studio only accepts json_schema: retry once without it and remember
                jsonFormatUnsupported = true;
                Log.Warn($"provider '{cfg.Name}' does not support response_format json_object; continuing without it");
                (status, text) = await SendAsync(BuildBody(cfg, req, allowJsonFormat: false, reasoningSwitch: !reasoningSwitchUnsupported), cts.Token).ConfigureAwait(false);
            }
            if ((int)status < 200 || (int)status > 299)
                throw new HttpRequestException($"HTTP {(int)status}: {JsonUtil.Trim(JsonUtil.OneLine(text))}", null, status);
            raw = text;
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

    async Task<(HttpStatusCode Status, string Body)> SendAsync(JsonObject body, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var key = cfg.ResolvedKey;
        if (!string.IsNullOrEmpty(key)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        foreach (var kv in cfg.ExtraHeaders) msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadCappedAsync(ct).ConfigureAwait(false);
        return (resp.StatusCode, text);
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
