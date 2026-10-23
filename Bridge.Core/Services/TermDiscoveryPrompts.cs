using System.Text;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>遊戲用語探勘的 GLM prompt：判斷直播中重複出現的未知片假名是不是這款遊戲的專有名詞。</summary>
public static class TermDiscoveryPrompts
{
    public const string System = """
        你是直播字幕的名詞審查員。下面是一場日文直播的語音辨識字幕中，重複出現的片假名詞，以及它們出現的例句。
        判斷每個詞是不是「這款遊戲特有、翻譯時需要固定譯名」的名詞：
        - 是：遊戲裡的角色、英雄、地圖、地點、道具、技能名稱，以及這類遊戲玩家圈特有的術語（例如 MOBA 的路線與地圖目標、FPS 的報點與點位名稱、遊戲模式名稱）。這類詞就算本身也是普通外來語，在這款遊戲裡有固定說法時也算「是」。
        - 否：一般外來語（平常生活也會用的詞，例如ゲーム、スキル、ジャンプ、ボタン、ダメージ）、擬聲詞、口頭禪、看不懂的辨識錯字。
        - 非遊戲直播一律判「否」，除非是明確的專有名詞。
        - 不確定就判「否」。
        判「是」時，給台灣玩家常用的繁體中文譯名，角色和英雄要用台灣官方譯名；不知道就留空。
        只輸出一個 JSON 物件，不要其他文字，每個候選詞都要列出：
        {"terms":[{"word":"候選詞","isTerm":true,"target":"譯名"}]}
        判「否」時 isTerm 是 false、target 是空字串。
        """;

    /// <summary>組探勘的 user 訊息：遊戲名、簡介（去頭尾空白後最多 100 字，空白時整行省略）與候選詞清單。</summary>
    public static string BuildUser(GlossaryCard game, IReadOnlyList<TermDiscoveryCandidate> candidates)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"遊戲：{game.Name}");
        var description = (game.Description ?? string.Empty).Trim();
        if (description.Length > 100)
        {
            description = description[..100];
        }
        if (description.Length > 0)
        {
            builder.AppendLine($"簡介：{description}");
        }

        builder.AppendLine();
        builder.AppendLine("# 候選詞");
        foreach (var candidate in candidates)
        {
            builder.AppendLine($"## {candidate.Word}（出現 {candidate.Count} 次）");
            foreach (var example in candidate.Examples)
            {
                builder.AppendLine($"- {example}");
            }
            builder.AppendLine();
        }

        builder.AppendLine("# Output（JSON）");
        return builder.ToString().TrimEnd('\r', '\n');
    }
}
