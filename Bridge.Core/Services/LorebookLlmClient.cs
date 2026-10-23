using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>呼叫知識庫模型（設定頁的 AppSettings.Lorebook，例如 Z.AI 的 GLM）取得 JSON 回覆。
/// 每次呼叫都重新讀設定，端點套用 OpenAiEndpoint 的規則（網址沒有路徑才補 /v1）。
/// selectSettings 可改讀其他設定區（備援用翻譯模型跑知識庫工作時改讀 AppSettings.Translation）；
/// disableThinking 為 true 時不送 thinking／reasoning_budget_tokens，改送 reasoning_effort = none（與翻譯模型 OpenAiClient 相同）。</summary>
public sealed class LorebookLlmClient(
    HttpClient httpClient,
    ISettingsService settings,
    ILogger<LorebookLlmClient> logger,
    Func<AppSettings, TranslationSettings>? selectSettings = null,
    bool disableThinking = false) : ILorebookLlm
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private const int ResponseHeadLength = 300;

    private readonly Func<AppSettings, TranslationSettings> _selectSettings = selectSettings ?? (s => s.Lorebook);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 中文不跳脫，讓 log 與 API 追蹤可直接閱讀
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await SendAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "知識庫模型 {Model} 回覆：{ElapsedMs} ms（prompt {PromptTokens}、completion {CompletionTokens} tokens）",
                result.Model, result.ElapsedMs, result.PromptTokens, result.CompletionTokens);
            return result;
        }
        catch (LorebookLlmException ex)
        {
            logger.LogWarning("知識庫模型呼叫失敗：{Message}", ex.Message);
            throw;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await CompleteJsonAsync("你只輸出 JSON。", "請回覆 {\"ok\": true}", cancellationToken).ConfigureAwait(false);
            return (true, $"連線成功：{result.Model}，{result.ElapsedMs} ms");
        }
        catch (Exception ex) when (ex is LorebookLlmException or OperationCanceledException)
        {
            return (false, $"連線失敗：{ex.Message}");
        }
    }

    private async Task<LlmJsonResult> SendAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var lorebook = _selectSettings(settings.Current);
        var model = (lorebook.Model ?? string.Empty).Trim();
        var endpoint = OpenAiEndpoint.TryBuild(lorebook);
        if (endpoint is null || model.Length == 0)
        {
            throw new LorebookLlmException("尚未設定知識庫模型（設定 → 知識庫模型）");
        }

        var body = JsonSerializer.Serialize(BuildRequestBody(lorebook, model, systemPrompt, userPrompt, disableThinking, out var thinkingDescription), JsonOptions);
        logger.LogDebug(
            "知識庫模型參數：{Model}，temperature {Temperature}，top_p {TopP}，思考 {Thinking}",
            model, lorebook.Temperature, lorebook.TopP, thinkingDescription);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var apiKey = (lorebook.ApiKey ?? string.Empty).Trim();
        if (apiKey.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var stopwatch = Stopwatch.StartNew();
        string responseBody;
        int statusCode;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(Timeout);
            try
            {
                using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                statusCode = (int)response.StatusCode;
                responseBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 使用者主動取消（外部 token）時例外照常往外拋；其餘視為逾時
                throw new LorebookLlmException("知識庫模型逾時（120 秒）", isTransient: true);
            }
            catch (HttpRequestException ex)
            {
                throw new LorebookLlmException($"知識庫模型連線失敗：{ex.Message}", ex, isTransient: true);
            }
        }

        if (statusCode is < 200 or > 299)
        {
            // 429（限流）與 5xx（伺服器錯誤）視為暫時性錯誤：連續發生時可改用翻譯模型
            throw new LorebookLlmException(
                $"知識庫模型回傳 HTTP {statusCode}：{Head(responseBody)}",
                isTransient: statusCode == 429 || statusCode >= 500);
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var content = StripCodeFence(root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
            var promptTokens = 0;
            var completionTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                promptTokens = GetInt32(usage, "prompt_tokens");
                completionTokens = GetInt32(usage, "completion_tokens");
            }
            var responseModel = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : null;
            return new LlmJsonResult(content, promptTokens, completionTokens, stopwatch.ElapsedMilliseconds, responseModel ?? model);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new LorebookLlmException($"知識庫模型回應格式不正確：{Head(responseBody)}", ex);
        }
    }

    /// <summary>依設定組 chat/completions 的請求 body：
    /// 一律送 model、messages、response_format、temperature；TopP 有值時送 top_p。
    /// GLM 依思考強度送 thinking 與 reasoning_effort（none 時送 disabled、不送 reasoning_effort）；
    /// 其他模型在 ReasoningBudgetTokens 大於 0 時送 reasoning_budget_tokens；
    /// disableThinking 時一律不送這三者，改送 reasoning_effort = none。</summary>
    private static Dictionary<string, object?> BuildRequestBody(
        TranslationSettings lorebook, string model, string systemPrompt, string userPrompt, bool disableThinking, out string thinkingDescription)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            ["temperature"] = lorebook.Temperature,
            ["response_format"] = new { type = "json_object" },
        };
        if (lorebook.TopP is { } topP)
        {
            body["top_p"] = topP;
        }

        if (disableThinking)
        {
            // 與翻譯模型 OpenAiClient 相同的破玻璃解法：完全關閉思考
            body["reasoning_effort"] = "none";
            thinkingDescription = "關閉";
        }
        else if (LorebookModelKind.IsGlm(model))
        {
            var effort = LorebookModelKind.NormalizeReasoningEffort(lorebook.ReasoningEffort);
            if (effort == "none")
            {
                body["thinking"] = new Dictionary<string, object?> { ["type"] = "disabled" };
                thinkingDescription = "關閉";
            }
            else
            {
                body["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled" };
                body["reasoning_effort"] = effort;
                thinkingDescription = effort;
            }
        }
        else if (lorebook.ReasoningBudgetTokens > 0)
        {
            body["reasoning_budget_tokens"] = lorebook.ReasoningBudgetTokens;
            thinkingDescription = $"上限 {lorebook.ReasoningBudgetTokens} tokens";
        }
        else
        {
            thinkingDescription = "未限制";
        }

        return body;
    }

    /// <summary>回應開頭 300 字，供錯誤訊息顯示。</summary>
    private static string Head(string body) =>
        body.Length <= ResponseHeadLength ? body : body[..ResponseHeadLength];

    private static int GetInt32(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : 0;

    /// <summary>模型輸出若被 ```json … ``` 包住，剝掉 code fence 只留內容。</summary>
    internal static string StripCodeFence(string? content)
    {
        var trimmed = (content ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var lineEnd = trimmed.IndexOf('\n');
        if (lineEnd < 0)
        {
            return trimmed;
        }

        var body = trimmed[(lineEnd + 1)..].Trim();
        if (body.EndsWith("```", StringComparison.Ordinal))
        {
            body = body[..^3].Trim();
        }
        return body;
    }
}
