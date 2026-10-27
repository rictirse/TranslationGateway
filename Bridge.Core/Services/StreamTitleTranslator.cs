using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 直播／影片標題翻譯（Dashboard 頁首）：一鍵開台後把這場的標題用知識庫模型翻成繁體中文。
/// 用 <see cref="ILorebookLlm"/>（沒設定時它自己會改用翻譯模型），30 秒逾時；
/// 失敗一律回 null、不擲例外（呼叫端的取消除外），不拖慢開台流程。
/// </summary>
public sealed class StreamTitleTranslator(
    ILorebookLlm llm,
    GlossaryState glossary,
    ITextProcessingService text,
    ILogger<StreamTitleTranslator> logger)
{
    /// <summary>模型的逾時時間：標題翻譯沒有人在等，超過就放棄。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>把標題翻成繁體中文；標題空白、模型失敗、回覆格式不對時回 null，不擲例外（呼叫端取消除外）。</summary>
    public async Task<string?> TranslateAsync(string title, CancellationToken cancellationToken = default)
    {
        var original = (title ?? string.Empty).Trim();
        if (original.Length == 0)
        {
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);
            LlmJsonResult reply;
            using (LlmCallPurposeScope.Enter(LlmCallPurposes.TitleTranslation))
            {
                reply = await llm.CompleteJsonAsync(BuildSystemPrompt(), original, timeoutCts.Token).ConfigureAwait(false);
            }

            var parsed = ParseReply(reply.Content);
            if (parsed is null)
            {
                logger.LogWarning("直播標題翻譯：模型回覆無法解析（標題：{Title}，回覆：{Reply}）", original, reply.Content);
                return null;
            }

            var result = text.PostProcess(parsed, original).Trim();
            if (result.Length == 0)
            {
                logger.LogWarning("直播標題翻譯：譯文是空的（標題：{Title}）", original);
                return null;
            }

            logger.LogInformation("直播標題翻譯：{Title} → {Translation}", original, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is LorebookLlmException or JsonException or OperationCanceledException)
        {
            // 含 30 秒逾時（linked CTS 觸發的取消）：放棄譯文，只記 log
            logger.LogWarning(ex, "直播標題翻譯失敗：{Title}", original);
            return null;
        }
    }

    /// <summary>組 system prompt（含生效中詞彙卡的名詞區段）；給測試用。</summary>
    internal string BuildSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("你是日文直播與影片標題的翻譯。把使用者給的標題翻成台灣慣用的繁體中文。");
        builder.AppendLine();
        builder.AppendLine("規則：");
        builder.AppendLine("- 保留【】、#hashtag、/、|、（）等符號與原有的排列。");
        builder.AppendLine("- hashtag 與英文專有名詞照抄不翻。");
        builder.AppendLine("- 人名、團體名、作品名優先使用詞彙表的譯名；詞彙表沒有的用台灣常用的譯名。");
        builder.AppendLine("- 其餘部分翻得自然流暢，不要逐字直譯。");
        builder.AppendLine();
        builder.AppendLine("只輸出 JSON 物件，格式：");
        builder.AppendLine("""{"translation": "譯文"}""");
        var section = glossary.PromptSection;
        if (!string.IsNullOrEmpty(section))
        {
            builder.AppendLine();
            builder.AppendLine("## 詞彙表");
            builder.AppendLine(section);
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>從模型回覆解析 {"translation": "..."}；不是 JSON、沒有該欄位或為空白時回 null。
    /// 容忍模型在 JSON 外面包 ```json 圍欄（去掉後再解析）。</summary>
    internal static string? ParseReply(string content)
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
            if (!doc.RootElement.TryGetProperty("translation", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var translation = value.GetString()?.Trim();
            return string.IsNullOrEmpty(translation) ? null : translation;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
