using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 章節標題翻譯（錄影章節名稱疊加）：把整支影片的章節標題（多半是歌名）一次用知識庫模型翻成繁體中文。
/// 用 <see cref="ILorebookLlm"/>（沒設定時它自己會改用翻譯模型），60 秒逾時；
/// 整體失敗回 null、不擲例外（呼叫端的取消除外），單一標題翻不出來就那個位置是 null。
/// </summary>
public sealed class ChapterTitleTranslator(
    ILorebookLlm llm,
    GlossaryState glossary,
    ITextProcessingService text,
    ILogger<ChapterTitleTranslator> logger)
{
    /// <summary>模型的逾時時間：章節標題沒有人在等，超過就放棄。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>一次翻譯多個章節標題；回傳與輸入同長度、同順序的譯文（某個翻不出來為 null）。
    /// 整體失敗（模型錯誤、格式不對、數量不符）回 null，不擲例外（呼叫端取消除外）。</summary>
    public async Task<IReadOnlyList<string?>?> TranslateAsync(IReadOnlyList<string> titles, CancellationToken cancellationToken = default)
    {
        if (titles.Count == 0)
        {
            return [];
        }

        var userPrompt = JsonSerializer.Serialize(titles);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);
            LlmJsonResult reply;
            using (LlmCallPurposeScope.Enter(LlmCallPurposes.ChapterTitleTranslation))
            {
                reply = await llm.CompleteJsonAsync(BuildSystemPrompt(), userPrompt, timeoutCts.Token).ConfigureAwait(false);
            }

            var parsed = ParseReply(reply.Content, titles.Count);
            if (parsed is null)
            {
                logger.LogWarning("章節標題翻譯：模型回覆無法解析（{Count} 個標題，回覆：{Reply}）", titles.Count, reply.Content);
                return null;
            }

            // 每個譯文經後處理（繁簡轉換等）；空的維持 null
            var results = new string?[titles.Count];
            for (var i = 0; i < titles.Count; i++)
            {
                if (parsed[i] is null)
                {
                    continue;
                }

                results[i] = text.PostProcess(parsed[i]!, titles[i]).Trim();
                if (results[i]!.Length == 0)
                {
                    results[i] = null;
                }
            }

            logger.LogInformation("章節標題翻譯：{Total} 個標題、{Translated} 個譯成繁體中文", titles.Count, results.Count(static r => r is not null));
            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is LorebookLlmException or JsonException or OperationCanceledException)
        {
            // 含 60 秒逾時（linked CTS 觸發的取消）：放棄譯文，只記 log
            logger.LogWarning(ex, "章節標題翻譯失敗（{Count} 個標題）", titles.Count);
            return null;
        }
    }

    /// <summary>組 system prompt（含生效中詞彙卡的名詞區段）；給測試用。</summary>
    internal string BuildSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("你是日文歌枠／直播影片的章節標題翻譯。使用者會給一個 JSON 陣列，裡面是章節標題，多半是「名次 曲名 ／ 歌手」的寫法。請逐一翻成台灣慣用的繁體中文。");
        builder.AppendLine();
        builder.AppendLine("規則：");
        builder.AppendLine("- 名次寫成「第N名」（例：10位 → 第10名）。");
        builder.AppendLine("- 曲名有台灣通行的中文譯名就用通行譯名，沒有就意譯；英文曲名照抄不翻。");
        builder.AppendLine("- 歌手名有通行中文名就用，沒有就照抄原文。");
        builder.AppendLine("- 保留「／」「-」等分隔符號與原有的排列。");
        builder.AppendLine("- 不要加括號說明或任何註解。");
        builder.AppendLine();
        builder.AppendLine("只輸出 JSON 物件，格式（數量與順序與輸入相同）：");
        builder.AppendLine("""{"translations": ["…", …]}""");
        var section = glossary.PromptSection;
        if (!string.IsNullOrEmpty(section))
        {
            builder.AppendLine();
            builder.AppendLine("## 詞彙表");
            builder.AppendLine(section);
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>從模型回覆解析 {"translations": ["…", …]}；不是 JSON、沒有該欄位、不是陣列或長度不符時回 null；
    /// 個別元素不是字串或空白時那個位置是 null。容忍模型在 JSON 外面包 ```json 圍欄（去掉後再解析）。</summary>
    internal static IReadOnlyList<string?>? ParseReply(string content, int expectedCount)
    {
        var text = (content ?? string.Empty).Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("translations", out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var results = new List<string?>();
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    results.Add(null);
                    continue;
                }

                var translation = element.GetString()?.Trim();
                results.Add(string.IsNullOrEmpty(translation) ? null : translation);
            }

            return results.Count == expectedCount ? results : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
