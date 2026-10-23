using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>開台後判斷還缺哪種詞彙卡（要不要自動資料探勘）：主播沒有角色卡、或遊戲直播的遊戲沒有作品卡。</summary>
public static class StreamMiningDecider
{
    /// <summary>Twitch 上不算遊戲直播的分類（實況閒聊、音樂、繪圖、脫口秀）。</summary>
    private static readonly string[] TwitchNonGameCategories = ["Just Chatting", "Music", "Art", "Talk Shows & Podcasts"];

    /// <summary>回傳需要資料探勘的原因；不需要時為 null。判定順序：主播角色卡 → 遊戲直播的作品卡。</summary>
    public static string? MissingCardReason(IReadOnlyList<GlossaryCardMatch> matches, StreamPlatform? platform, string? category)
    {
        // 主播卡（只比對到 HostGroup 團體卡也算沒有）
        if (!matches.Any(m => m.Reason == StreamCardReason.Host))
        {
            return "主播沒有角色卡";
        }

        if (IsGameStream(platform, category) && !matches.Any(m => m.Reason == StreamCardReason.TitleGame))
        {
            return "這款遊戲沒有作品卡";
        }

        return null;
    }

    /// <summary>是否為遊戲直播：YouTube 看 category 是否為 Gaming；
    /// Twitch 的 category 有值而且不是閒聊／音樂等非遊戲分類；沒有平台資訊時不是遊戲直播。</summary>
    private static bool IsGameStream(StreamPlatform? platform, string? category)
    {
        switch (platform)
        {
            case StreamPlatform.YouTube:
                return string.Equals(category, "Gaming", StringComparison.OrdinalIgnoreCase);
            case StreamPlatform.Twitch:
                return !string.IsNullOrWhiteSpace(category) &&
                    !TwitchNonGameCategories.Any(c => string.Equals(category, c, StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }
}
