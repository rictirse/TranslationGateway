using System.Diagnostics;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>知識庫模型的回溯紀錄裝飾器：包在 ILorebookLlm 的最外層（DI 註冊時包住 ScheduledLorebookLlm），
/// 每次 CompleteJsonAsync 成功或失敗都寫一筆 llm_calls——這樣臨時改用翻譯模型（備援）時也會被記到，
/// Model 欄位會顯示實際使用的模型。purpose 取自 <see cref="LlmCallPurposeScope"/>（各知識庫工作進入 scope）。
/// 取消（OperationCanceledException）不記錄；TestConnectionAsync 直接轉發、不記錄。</summary>
public sealed class RecordingLorebookLlm(ILorebookLlm inner, LlmCallRecorder recorder) : ILorebookLlm
{
    public async Task<LlmJsonResult> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await inner.CompleteJsonAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            recorder.Record(new LlmCallInput
            {
                Purpose = LlmCallPurposeScope.Current,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                ResponseRaw = result.Content,
                Model = result.Model,
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                LatencyMs = result.ElapsedMs,
                Outcome = LlmCallOutcomes.Ok,
            });
            return result;
        }
        catch (LorebookLlmException)
        {
            // 知識庫模型失敗（設定不完整、HTTP 錯誤、逾時、格式不對）：記一筆 Error 後原樣重擲
            stopwatch.Stop();
            recorder.Record(new LlmCallInput
            {
                Purpose = LlmCallPurposeScope.Current,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Outcome = LlmCallOutcomes.Error,
            });
            throw;
        }
        // OperationCanceledException（外部取消）：不記錄，原樣往外拋
    }

    /// <summary>連線測試不經過模型回答翻譯流程，直接轉給 inner，不記錄。</summary>
    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        inner.TestConnectionAsync(cancellationToken);
}
