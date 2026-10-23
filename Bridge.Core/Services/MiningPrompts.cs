using System.Text;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>資料探勘的 GLM prompt：拆名詞與建詞彙卡兩段。</summary>
public static class MiningPrompts
{
    public const string ExtractSystem = """
        你是直播字幕翻譯的專有名詞分析師。從情境文字中挑出專有名詞。

        規則：
        - 只挑專有名詞：人名（VTuber 名）、團體、遊戲、活動、道具、地名；不挑一般詞彙。
        - 每個名詞給 importance 1～5：對理解這場直播內容的重要性，5 最重要。
        - kind 只能是：person（人名）、group（團體）、game（遊戲）、event（活動）、item（道具）、place（地名）、other（其他）。
        - surface 用情境文字中的原始寫法。

        只輸出 JSON，格式：
        {"nouns":[{"surface":"…","kind":"person|group|game|event|item|place|other","importance":1-5]}
        """;

    public const string ComposeSystem = """
        你是直播字幕翻譯的詞彙卡整理員。依提供的資料（Wikidata、Wikipedia、Fandom）建立或比對詞彙卡。

        規則：
        - 只使用資料中出現的事實，不要編造資料中沒有的內容。
        - 人名：漢字保留，假名轉成首字母大寫的羅馬拼音，例如 花芽すみれ→花芽Sumire、英リサ→英Lisa。有英文官方名時用它的拼法，例如官方英文名 Nemukumo Tsukuri → 眠雲Tsukuri。暱稱的「ちゃん」寫成「醬」，例如 リサちゃん→Lisa醬。
        - 團體：名稱照官方寫法。
        - 遊戲：名稱用資料中的台灣或繁體中文官方名稱（zh-tw 優先，其次 zh-hant，再其次 zh 經繁體化），沒有就保留原名。summary 是 100 字以內的繁體中文簡介，說明遊戲類型與主要內容。terms 只放遊戲名稱 1 個。
        - terms 的 source 用字幕裡會出現的原始寫法，target 用中文譯名，category 用：人名、暱稱、粉絲名、團體、遊戲。
        - aliases：直播標題、hashtag、聊天室常用的簡稱與其他寫法，最多 5 個，例如 Apex Legends → APEX、エペ、エーペックス；League of Legends → LoL、英雄聯盟。沒有常見簡稱就給空陣列。不要放和 name 相同的寫法。
        - 覺得和 similarCards 中的某一張是同一個東西時，回 existing 並給 cardId。
        - 需要新的卡時，Character 的 terms 最多 5 個（名字、暱稱、粉絲名），Group 最多 3 個，Game 只放遊戲名稱 1 個。

        只輸出 JSON，兩種格式擇一：
        {"decision":"existing","cardId":123}
        {"decision":"new","kind":"Character|Group|Game","name":"…","summary":"…","aliases":["…"],"parentGroup":"…或 null","terms":[{"source":"…","target":"…","category":"…"}]}
        """;

    /// <summary>組拆名詞的 user 訊息：情境文字與目前生效中的詞彙卡名稱（避免重複挑已經有的）。</summary>
    public static string BuildExtractUser(string situation, IReadOnlyList<string> activeCardNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 情境");
        builder.AppendLine(situation);
        if (activeCardNames.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 已有詞彙卡（這些已經處理過，不必再挑）");
            builder.AppendLine(string.Join("、", activeCardNames));
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>組開台資料探勘拆名詞的 user 訊息：頻道名稱、直播標題與說明欄（沒有值的那一段省略），
    /// 加上目前生效中的詞彙卡名稱（避免重複挑已經有的；空清單時省略整段）。
    /// 說明欄先經 <see cref="StreamDescriptionFilter.Filter"/> 去掉連結與工商等雜訊行，過濾後是空的時省略整段。</summary>
    public static string BuildStreamExtractUser(string? channelName, string? title, string? description,
        IReadOnlyList<string> activeCardNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 直播資訊");
        if (!string.IsNullOrWhiteSpace(channelName))
        {
            builder.AppendLine($"頻道名稱：{channelName.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.AppendLine($"直播標題：{title.Trim()}");
        }
        var filteredDescription = StreamDescriptionFilter.Filter(description);
        if (filteredDescription.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 說明欄（已去掉連結與工商等行，可能仍有雜訊）");
            builder.AppendLine(filteredDescription);
        }
        builder.AppendLine();
        builder.AppendLine("## 注意");
        builder.AppendLine("頻道名稱通常是主播本人的名字（可能同時有日文與英文，例如「銀城サイネ / Ginjo Saine」是同一個人），最重要。");
        builder.AppendLine("標題裡的「雑談」「歌枠」「ゲーム実況」等節目類型、顏文字、#標籤符號本身都不是專有名詞；#後面的名字可能是團體或人名。");
        if (filteredDescription.Length > 0)
        {
            builder.AppendLine("說明欄只挑「參與這場直播的人」（連動、コラボ、ゲスト、w/ 後面的名字）和這場直播玩的作品；工商商品、贊助商、繪師、Live2D、BGM、規則、周邊都不要挑。");
            builder.AppendLine("只出現在說明欄、沒出現在頻道名稱和標題的名詞，importance 最多給 2。");
        }
        if (activeCardNames.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 已有詞彙卡（這些已經處理過，不必再挑）");
            builder.AppendLine(string.Join("、", activeCardNames));
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>組建卡的 user 訊息：名詞、查詢到的資料與名稱相近的既有卡片。</summary>
    public static string BuildComposeUser(string surface, string kind, WikidataEntity? entity,
        WikiPage? wikiPage, WikiPage? fandomPage, IReadOnlyList<GlossaryCard> similarCards)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 要處理的專有名詞");
        builder.AppendLine($"surface：{surface}");
        builder.AppendLine($"kind：{kind}");
        builder.AppendLine();

        if (entity is not null)
        {
            builder.AppendLine("## Wikidata");
            builder.AppendLine($"id：{entity.Id}（{entity.Url}）");
            AppendDictionary(builder, "labels", entity.Labels);
            AppendDictionary(builder, "descriptions", entity.Descriptions);
            if (entity.Aliases.Count > 0)
            {
                builder.AppendLine("aliases：");
                foreach (var (language, aliases) in entity.Aliases)
                {
                    builder.AppendLine($"  {language}：{aliases.Replace("\n", "、")}");
                }
            }
            builder.AppendLine();
        }

        if (wikiPage is not null)
        {
            builder.AppendLine("## Wikipedia");
            AppendWikiPage(builder, wikiPage);
        }

        if (fandomPage is not null)
        {
            builder.AppendLine("## Fandom");
            AppendWikiPage(builder, fandomPage);
        }

        if (similarCards.Count > 0)
        {
            builder.AppendLine("## similarCards（既有的詞彙卡，可能是同一個東西）");
            foreach (var card in similarCards)
            {
                builder.AppendLine($"- id {card.Id}：{card.Name}（{card.Kind}）");
            }
            builder.AppendLine();
        }

        builder.AppendLine("請依資料判斷 existing 或 new，只輸出 JSON。");
        return builder.ToString();
    }

    private static void AppendDictionary(StringBuilder builder, string title, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
        {
            return;
        }
        builder.AppendLine($"{title}：");
        foreach (var (language, value) in map)
        {
            builder.AppendLine($"  {language}：{value}");
        }
    }

    private static void AppendWikiPage(StringBuilder builder, WikiPage page)
    {
        builder.AppendLine($"title：{page.Title}");
        builder.AppendLine($"url：{page.Url}");
        if (!string.IsNullOrWhiteSpace(page.ZhTitle))
        {
            builder.AppendLine($"zh_title：{page.ZhTitle}");
        }
        if (!string.IsNullOrWhiteSpace(page.Extract))
        {
            var extract = page.Extract.Trim();
            builder.AppendLine(extract.Length <= 1500 ? extract : extract[..1500]);
        }
        builder.AppendLine();
    }
}
