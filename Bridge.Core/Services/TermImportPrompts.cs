using System.Text;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>貼上資料整理的 GLM prompt：把使用者貼上的網路資料整理成詞彙卡名詞。</summary>
public static class TermImportPrompts
{
    public const string System = """
        你是直播字幕翻譯的詞彙整理員。使用者貼上一段網路資料（例如遊戲英雄列表、地圖、技能、譯名對照表、用語表），請整理成詞彙卡的名詞。

        規則：
        - 只整理資料中出現的專有名詞或用語，不要加入資料中沒有的項目。
        - source：日文直播字幕裡會出現的寫法（片假名、漢字或日文常用的英文寫法）。資料裡有日文就照抄；資料只有英文或中文時，可以推測日文的常見寫法。
        - target：台灣繁體中文譯名。資料裡有台灣官方譯名（台服、zh-tw）就照用；沒有時用台灣玩家的常用叫法；都沒有才自己翻譯，人名可保留英文。
        - aliases：其他寫法，例如英文原名、簡稱、日文的另一種寫法；沒有就給空陣列。不要放和 source 相同的寫法。
        - category：簡短分類，例如 英雄、角色、地圖、技能、道具、武器、模式、用語、人名。
        - note：補充說明（例如出處、譯名是官方或玩家叫法），沒有就給空字串。
        - 已有名詞清單裡的 source 不要再給。

        只輸出 JSON：
        {"terms":[{"source":"…","target":"…","aliases":["…"],"category":"…","note":"…"}]}
        """;

    /// <summary>組整理的 user 訊息：詞彙卡、使用者說明（空白時省略整段）、已有名詞（最多 200 個，空的時候省略整段）與資料分段（只有 1 段時標題不帶段號）。</summary>
    public static string BuildUser(string cardName, string cardKind, string? instruction,
        IReadOnlyList<string> existingSources, string chunk, int chunkIndex, int chunkCount)
    {
        var builder = new StringBuilder();
        builder.Append("## 詞彙卡").Append('\n');
        builder.Append($"卡名：{cardName}（{KindText(cardKind)}）").Append('\n');
        builder.Append('\n');

        var instructionText = (instruction ?? string.Empty).Trim();
        if (instructionText.Length > 0)
        {
            builder.Append("## 使用者說明").Append('\n');
            builder.Append(instructionText).Append('\n');
            builder.Append('\n');
        }

        if (existingSources.Count > 0)
        {
            builder.Append("## 已有名詞（不要再給）").Append('\n');
            builder.Append(string.Join("、", existingSources.Take(200))).Append('\n');
            builder.Append('\n');
        }

        builder.Append(chunkCount > 1 ? $"## 資料（第 {chunkIndex}/{chunkCount} 段）" : "## 資料").Append('\n');
        builder.Append(chunk);
        return builder.ToString();
    }

    /// <summary>卡片種類的中文顯示（未知種類原樣顯示）。</summary>
    private static string KindText(string kind) => kind switch
    {
        GlossaryKind.Character => "角色",
        GlossaryKind.Group => "團體",
        GlossaryKind.Game => "遊戲",
        GlossaryKind.Common => "共通用語",
        _ => kind,
    };
}
