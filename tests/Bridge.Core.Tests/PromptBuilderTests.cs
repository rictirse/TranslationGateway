using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class PromptBuilderTests
{
    private static AppSettings Settings(string template = "你是翻譯專家") => new()
    {
        Translation = new TranslationSettings { SystemPromptTemplate = template }
    };

    [Fact]
    public void BuildSystemPrompt_NoSituationOrCharacterTable_ReturnsTemplateAsIs()
    {
        // 含結尾空白：不得被 Trim 或改動
        var template = "  你是翻譯專家 \n";

        Assert.Equal(template, PromptBuilder.BuildSystemPrompt(Settings(template)));
    }

    [Fact]
    public void BuildSystemPrompt_WithSituation_AppendsSituationSection()
    {
        var settings = Settings("你是翻譯專家");
        settings.StreamSituation = "  佩可拉在玩 Minecraft  ";

        var prompt = PromptBuilder.BuildSystemPrompt(settings);

        Assert.StartsWith("你是翻譯專家", prompt);
        Assert.Contains("# 直播情境（參考資料）", prompt);
        Assert.Contains("佩可拉在玩 Minecraft", prompt);
        Assert.DoesNotContain("  佩可拉在玩 Minecraft  ", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_WithCharacterTable_AppendsTableAndSkipsCommentsAndBlankLines()
    {
        var settings = Settings("你是翻譯專家");
        settings.CharacterTable = "# 主要角色\n\nペコラ=佩可拉\n カフ=卡芙 \n";

        var prompt = PromptBuilder.BuildSystemPrompt(settings);

        Assert.Contains("# 角色表（參考資料）", prompt);
        Assert.Contains("ペコラ=佩可拉\nカフ=卡芙", prompt);
        Assert.DoesNotContain("主要角色", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_CharacterTableOnlyCommentsAndBlanks_NoTableSection()
    {
        var settings = Settings("你是翻譯專家");
        settings.CharacterTable = "# 註解\n\n   \n";

        Assert.Equal("你是翻譯專家", PromptBuilder.BuildSystemPrompt(settings));
    }

    [Fact]
    public void BuildSystemPrompt_NullGlossarySection_SameAsNoSection()
    {
        var settings = Settings("你是翻譯專家");
        settings.StreamSituation = "遊戲實況";
        settings.CharacterTable = "アリス=愛麗絲";

        Assert.Equal(
            PromptBuilder.BuildSystemPrompt(settings),
            PromptBuilder.BuildSystemPrompt(settings, null));
    }

    [Fact]
    public void BuildSystemPrompt_BlankGlossarySection_SameAsNoSection()
    {
        var settings = Settings("你是翻譯專家");
        settings.CharacterTable = "アリス=愛麗絲";

        Assert.Equal(
            PromptBuilder.BuildSystemPrompt(settings),
            PromptBuilder.BuildSystemPrompt(settings, "   \n"));
    }

    [Fact]
    public void BuildSystemPrompt_WithGlossarySection_AppendedAfterCharacterTable()
    {
        var settings = Settings("你是翻譯專家");
        settings.StreamSituation = "遊戲實況";
        settings.CharacterTable = "アリス=愛麗絲";
        const string section = "# 詞彙表（參考資料，原文=譯名，譯名請照用）\n## 英リサ\n英リサ=英莉莎";

        var prompt = PromptBuilder.BuildSystemPrompt(settings, section);

        var characterIndex = prompt.IndexOf("# 角色表（參考資料）", StringComparison.Ordinal);
        var glossaryIndex = prompt.IndexOf("# 詞彙表", StringComparison.Ordinal);
        Assert.True(characterIndex >= 0 && glossaryIndex > characterIndex);
        Assert.EndsWith("# 詞彙表（參考資料，原文=譯名，譯名請照用）\n## 英リサ\n英リサ=英莉莎", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_GlossarySectionTrimmed_OnlyOneBlankLineBefore()
    {
        var prompt = PromptBuilder.BuildSystemPrompt(Settings("你是翻譯專家"), "  ## 卡\n詞=譯  ");

        Assert.Equal("你是翻譯專家\n\n## 卡\n詞=譯", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_BlankTemplate_FallsBackToDefaultTemplate()
    {
        var settings = Settings("   \n");

        var prompt = PromptBuilder.BuildSystemPrompt(settings);

        // 模板空白時以預設模板開頭（附加段落接在後面）
        Assert.StartsWith(PromptBuilder.DefaultSystemTemplate, prompt);
        settings.StreamSituation = "遊戲實況";
        Assert.StartsWith(PromptBuilder.DefaultSystemTemplate, PromptBuilder.BuildSystemPrompt(settings));
    }

    [Fact]
    public void BuildSystemPrompt_CustomTemplate_UsedAsIs()
    {
        // 使用者自己填過的模板原樣使用，不覆蓋成預設模板
        Assert.StartsWith("你是翻譯專家", PromptBuilder.BuildSystemPrompt(Settings("你是翻譯專家")));
    }

    [Fact]
    public void TranslationSettings_Default_SystemPromptTemplateEqualsDefaultTemplate()
    {
        Assert.Equal(PromptBuilder.DefaultSystemTemplate, new TranslationSettings().SystemPromptTemplate);
    }

    [Fact]
    public void SystemTemplates_ContainUpdatedPeriodAndNamingRules()
    {
        // 規則改版：句尾不加句號（原文有「。」也一樣）、譯名不再要求官方英文名
        Assert.Contains("原文有「。」也一樣", PromptBuilder.DefaultSystemTemplate);
        Assert.Contains("不要自己拼成英文", PromptBuilder.DefaultSystemTemplate);
        Assert.DoesNotContain("官方英文名", PromptBuilder.DefaultSystemTemplate);

        Assert.Contains("原文有「。」也一樣", PromptBuilder.BatchSystemTemplate);
        Assert.Contains("不要自己拼成英文", PromptBuilder.BatchSystemTemplate);
        Assert.DoesNotContain("官方英文名", PromptBuilder.BatchSystemTemplate);

        Assert.Contains(
            "2. 口語、自然、符合台灣用法，只用繁體中文字。句尾不加句號（原文有「。」也一樣）；一句裡有好幾段時用空格或逗號隔開，不用句號。問號和驚嘆號用全形。",
            PromptBuilder.DefaultSystemTemplate);
        Assert.Contains(
            "4. 人名、團名，以及遊戲的地圖、角色、道具、造型名稱：詞彙表有的一律照詞彙表；沒有的用台灣常見的中文譯名；都不確定就保留原文，不要自己拼成英文。",
            PromptBuilder.DefaultSystemTemplate);
        Assert.Contains(
            "3. 口語、自然、符合台灣用法，只用繁體中文字。句尾不加句號（原文有「。」也一樣）；一行裡有好幾段時用空格或逗號隔開，不用句號。問號和驚嘆號用全形。",
            PromptBuilder.BatchSystemTemplate);
        Assert.Contains(
            "5. 人名、團名，以及遊戲的地圖、角色、道具、造型名稱：詞彙表有的一律照詞彙表；沒有的用台灣常見的中文譯名；都不確定就保留原文，不要自己拼成英文。",
            PromptBuilder.BatchSystemTemplate);
    }

    [Fact]
    public void BatchSystemTemplate_ForbidsExplanations()
    {
        // T-2106：錄影批次加第 9 條規則，從源頭禁止模型加括號說明；直播用的預設模板不加這條
        Assert.EndsWith(
            "9. 只輸出譯文，不要加任何括號說明、註解或「辨識不清」之類的備註；原文看不懂時照字面盡量翻，不要解釋。",
            PromptBuilder.BatchSystemTemplate);
        Assert.DoesNotContain("不要加任何括號說明", PromptBuilder.DefaultSystemTemplate);
    }

    [Fact]
    public void BuildSingleTurnUserPrompt_MatchesTranslationManagerFormat()
    {
        var request = new PotplayerRequest { Current = "c", Context = ["a", "b"] };

        var expected =
            "# Context（前面幾句，只用來理解，不要翻譯）\n" +
            "a\nb\n" +
            "# Current（只翻譯這句）\n" +
            "c\n" +
            "# Output（直接輸出繁體中文譯文）";

        Assert.Equal(expected, PromptBuilder.BuildSingleTurnUserPrompt(request));
    }

    [Fact]
    public void BuildSingleTurnUserPrompt_WithContextTranslations_TranslatedLinesUseArrow()
    {
        var request = new PotplayerRequest { Current = "c", Context = ["a", "b", "d"] };

        var expected =
            "# Context（前面幾句，只用來理解，不要翻譯）\n" +
            "a → 甲\n" +
            "b\n" +
            "d → 丁\n" +
            "# Current（只翻譯這句）\n" +
            "c\n" +
            "# Output（直接輸出繁體中文譯文）";

        Assert.Equal(expected, PromptBuilder.BuildSingleTurnUserPrompt(request, ["甲", null, "丁"]));
    }

    [Fact]
    public void BuildSingleTurnUserPrompt_NoContext_OmitsContextSection()
    {
        var prompt = PromptBuilder.BuildSingleTurnUserPrompt(new PotplayerRequest { Current = "c" });

        Assert.Equal(
            "# Current（只翻譯這句）\nc\n# Output（直接輸出繁體中文譯文）",
            prompt);
    }

    // ===== 批次（錄影預先翻譯）=====

    [Fact]
    public void BuildBatchUserPrompt_NumbersEachLine()
    {
        Assert.Equal("1. 甲\n2. 乙\n3. 丙", PromptBuilder.BuildBatchUserPrompt(["甲", "乙", "丙"]));
    }

    [Fact]
    public void ParseBatchReply_ParsesVariousSeparators()
    {
        var result = PromptBuilder.ParseBatchReply("1. 你好\n2．早安\n3、晚安\n4) 午安");

        Assert.Equal(4, result.Count);
        Assert.Equal("你好", result[1]);
        Assert.Equal("早安", result[2]);
        Assert.Equal("晚安", result[3]);
        Assert.Equal("午安", result[4]);
    }

    [Fact]
    public void ParseBatchReply_EmptyTranslationNotCollected()
    {
        var result = PromptBuilder.ParseBatchReply("1. 你好\n2.    \n3.\n沒有編號的行");

        var only = Assert.Single(result);
        Assert.Equal(new KeyValuePair<int, string>(1, "你好"), only);
    }

    [Fact]
    public void ParseBatchReply_DuplicateNumber_KeepsFirst()
    {
        var result = PromptBuilder.ParseBatchReply("1. 甲\n1. 乙");

        var only = Assert.Single(result);
        Assert.Equal(new KeyValuePair<int, string>(1, "甲"), only);
    }

    [Fact]
    public void BuildBatchSystemPrompt_StartsWithBatchTemplate_AndAppendsSections()
    {
        var settings = Settings("自訂模板");
        settings.CharacterTable = "ペコラ=佩可拉";

        var prompt = PromptBuilder.BuildBatchSystemPrompt(settings, "# 詞彙表（參考資料）\n英リサ=英莉莎");

        // 基底是批次模板（不用使用者的模板），之後同樣依序附加角色表與詞彙表
        Assert.StartsWith(PromptBuilder.BatchSystemTemplate, prompt);
        Assert.DoesNotContain("自訂模板", prompt);
        Assert.Contains("# 角色表（參考資料）\nペコラ=佩可拉", prompt);
        Assert.EndsWith("# 詞彙表（參考資料）\n英リサ=英莉莎", prompt);
    }
}
