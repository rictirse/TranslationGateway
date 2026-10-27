using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class TextProcessingServiceTests
{
    private readonly TextProcessingService _sut = new();

    [Fact]
    public void PreProcess_ReturnsInputUnchanged()
    {
        Assert.Equal("こんにちは", _sut.PreProcess("こんにちは"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PostProcess_BlankInput_ReturnedAsIs(string input)
    {
        Assert.Equal(input, _sut.PostProcess(input, "原文"));
    }

    [Fact]
    public void PostProcess_TrimsSurroundingWhitespace()
    {
        Assert.Equal("你好", _sut.PostProcess("  你好\n", "こんにちは"));
    }

    [Fact]
    public void PostProcess_NoChineseCharacters_OnlyTrims()
    {
        Assert.Equal("Hello", _sut.PostProcess(" Hello ", "こんにちは"));
    }

    [Fact]
    public void PostProcess_ConvertsSimplifiedToTraditional()
    {
        var result = _sut.PostProcess("这是一个测试", "これはテスト");

        Assert.Contains("這", result);
        Assert.Contains("個", result);
        Assert.Contains("測試", result);
    }

    [Theory]
    [InlineData("不到這個程度是不行的。", "不到這個程度是不行的")]
    [InlineData("沒有。啊。", "沒有。啊")]
    [InlineData("好。。", "好")]
    [InlineData("好．", "好")]
    [InlineData("真的嗎？", "真的嗎？")]
    [InlineData("好厲害！", "好厲害！")]
    [InlineData("然後……", "然後……")]
    [InlineData("他說「好。」", "他說「好。」")]
    [InlineData("v1.2.", "v1.2.")]
    [InlineData("。", "。")]
    [InlineData("这是测试。", "這是測試")]
    public void PostProcess_TrailingFullWidthPeriod_RemovedAtEndOnly(string input, string expected)
    {
        Assert.Equal(expected, _sut.PostProcess(input, "これはテスト"));
    }

    // 真實案例（錄影 lgOFmApuI5A）：模型自己加的註解要整段括號移除（含括號內的巢狀「」）。
    [Fact]
    public void PostProcess_RemovesFullWidthNote()
    {
        var input = "時鐘即將報時 在那裡踩上千番帽（註：辨識錯誤，應為「千番の」） 想要迷失在開滿花的小徑上";

        var result = _sut.PostProcess(input, "もうすぐ時計は悪く時もうそこに");

        Assert.Equal("時鐘即將報時 在那裡踩上千番帽 想要迷失在開滿花的小徑上", result);
    }

    [Fact]
    public void PostProcess_RemovesHalfWidthEnglishNote()
    {
        Assert.Equal("好想聽", _sut.PostProcess("好想聽(Note: maybe lyrics)", "聴きたい"));
        Assert.Equal("好想聽", _sut.PostProcess("好想聽 (note: maybe lyrics)", "聴きたい"));
        Assert.Equal("好想聽", _sut.PostProcess("好想聽（NOTE: maybe lyrics）", "聴きたい"));
        Assert.Equal("好想聽", _sut.PostProcess("好想聽（TN: maybe lyrics）", "聴きたい"));
    }

    // 括號未閉合、一路到字串結尾的註解也算。
    [Fact]
    public void PostProcess_RemovesUnclosedNoteAtEnd()
    {
        Assert.Equal("你好", _sut.PostProcess("你好（註：這裡可能是歌詞", "こんにちは"));
        Assert.Equal("你好", _sut.PostProcess("你好 (Note: maybe lyrics", "こんにちは"));
    }

    // 不是註解的括號一律保留。
    [Fact]
    public void PostProcess_KeepsNonNoteParentheses()
    {
        Assert.Equal("《孤獨搖滾》（Bocchi the Rock!）", _sut.PostProcess("《孤獨搖滾》（Bocchi the Rock!）", "ぼっち・ざ・ろっく"));
        Assert.Equal("小翼（Tsuba-chan）", _sut.PostProcess("小翼（Tsuba-chan）", "つばちゃん"));
        Assert.Equal("（笑）", _sut.PostProcess("（笑）", "www"));
        // Notes 後面不是冒號、單獨的 TN 字樣，都不是註解
        Assert.Equal("筆記（Notes about the song）", _sut.PostProcess("筆記（Notes about the song）", "メモ"));
    }

    // 整句只有註解：移除後會變空字串，保留移除前的文字。
    [Fact]
    public void PostProcess_OnlyNote_KeepsOriginalText()
    {
        Assert.Equal("（註：這裡可能是歌詞）", _sut.PostProcess("（註：這裡可能是歌詞）", "たぶん歌詞"));
        Assert.Equal("(Note: lyrics)", _sut.PostProcess("(Note: lyrics)", "lyrics"));
    }

    // 簡體寫法的註解標記（繁簡轉換之前移除，所以簡體也要認得）。
    [Fact]
    public void PostProcess_SimplifiedNoteMarker_Removed()
    {
        Assert.Equal("想要迷失", _sut.PostProcess("想要迷失（注：识别错误）", "迷いたくなる"));
        Assert.Equal("想要迷失", _sut.PostProcess("想要迷失（译注：识别错误）", "迷いたくなる"));
        Assert.Equal("想要迷失", _sut.PostProcess("想要迷失（备注：识别错误）", "迷いたくなる"));
    }

    // T-2106：實際資料（lgOFmApuI5A，493 句裡 25 句）沒有「註：」標記的說明括號也要移除；正常的括號一律保留。
    [Theory]
    [InlineData("…", "（歌詞辨識不清，疑似連續兩局/段）")]
    [InlineData("...我明明能愛自己一次的", "...我明明能愛自己一次的（歌詞辨識不清）")]
    [InlineData("染上紅色的日子柔軟地...", "染上紅色的日子柔軟地...（原文斷句不清）")]
    [InlineData("3好，第9位Ball", "3好，第9位Ball（？）")]
    [InlineData("很契合，這首關於錆的相談曲", "很契合，這首關於錆（指代特定歌曲或情境）的相談曲")]
    [InlineData("よいしょ", "よいしょ（搬運/舉起）")]
    [InlineData("冰箱裡有牛奶 viewpoint", "冰箱裡有牛奶 viewpoint（視角/觀點）")]
    [InlineData("《孤獨搖滾》（Bocchi the Rock）", "《孤獨搖滾》（Bocchi the Rock）")]
    [InlineData("ヨルシカ（Yorushika）", "ヨルシカ（Yorushika）")]
    [InlineData("Nemota（涼海Nemo）", "Nemota（涼海Nemo）")]
    [InlineData("小翼（Tsuba-chan）", "小翼（Tsuba-chan）")]
    [InlineData("（笑）", "（笑）")]
    [InlineData("（趣旨）", "（趣旨）")]
    public void PostProcess_ExplanationParentheses_RemovedAndNormalKept(string expected, string input)
    {
        Assert.Equal(expected, _sut.PostProcess(input, "たぶん歌詞"));
    }

    // 整句只剩說明（或（？）這類符號）：回傳省略號，不要把說明秀在字幕上。
    [Fact]
    public void PostProcess_OnlyExplanation_ReturnsEllipsis()
    {
        Assert.Equal("…", _sut.PostProcess("（歌詞辨識不清，疑似連續兩局/段）", "たぶん歌詞"));
        Assert.Equal("…", _sut.PostProcess("（疑似歌詞）", "たぶん歌詞"));
        Assert.Equal("…", _sut.PostProcess("（？）", "えーと"));
        Assert.Equal("…", _sut.PostProcess("(?)", "えーと"));
    }

    // 詞義註解的段裡有英文字母或數字時不算註解，保留。
    [Fact]
    public void PostProcess_SlashGlossWithLatin_Kept()
    {
        Assert.Equal("喜歡的樂團（AC/DC）", _sut.PostProcess("喜歡的樂團（AC/DC）", "好きなバンド"));
        Assert.Equal("尺寸（S/M/L）", _sut.PostProcess("尺寸（S/M/L）", "サイズ"));
        Assert.Equal("視角（viewpoint/觀點）", _sut.PostProcess("視角（viewpoint/觀點）", "視点"));
    }

    [Theory]
    [InlineData("", "原文")]
    [InlineData("  ", "原文")]
    public void Validate_EmptyTranslation_IsInvalid(string translated, string original)
    {
        var result = _sut.ValidateTranslation(translated, original);

        Assert.False(result.IsValid);
        Assert.Equal(0, result.Ratio);
    }

    [Fact]
    public void Validate_EmptyOriginal_IsInvalid()
    {
        var result = _sut.ValidateTranslation("翻譯", "");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_NormalLength_IsValid()
    {
        var result = _sut.ValidateTranslation("今天天氣很好", "今日はいい天気です");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_TranslationTooShort_IsInvalid()
    {
        var result = _sut.ValidateTranslation("好", new string('あ', 20));

        Assert.False(result.IsValid);
        Assert.True(result.Ratio < 0.3);
    }

    [Fact]
    public void Validate_TranslationTooLong_IsInvalid()
    {
        var result = _sut.ValidateTranslation(new string('好', 40), "あいう");

        Assert.False(result.IsValid);
        Assert.True(result.Ratio > 3.0);
    }

    [Fact]
    public void Validate_ManyLinesMostlyMissing_IsInvalid()
    {
        var original = string.Join("\n", Enumerable.Repeat("あいうえお", 10));
        var translated = "好好好好好好好好好好\n好好好好好好好好好好\n好好好好好好好好好好";

        var result = _sut.ValidateTranslation(translated, original);

        Assert.False(result.IsValid);
        Assert.Contains("行數", result.Message);
    }

    [Fact]
    public void Validate_ManyLinesAllPresent_IsValid()
    {
        var original = string.Join("\n", Enumerable.Repeat("あいうえお", 8));
        var translated = string.Join("\n", Enumerable.Repeat("好好好好好", 8));

        var result = _sut.ValidateTranslation(translated, original);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_FewLines_SkipsLineCountCheck()
    {
        var original = "あ\nい\nう";
        var translated = "好好好好好好";

        var result = _sut.ValidateTranslation(translated, original);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("♪")]
    [InlineData("...")]
    [InlineData("！？")]
    [InlineData("123")]
    [InlineData("ああああああああ")]
    [InlineData("ーーーーーーーー")]
    public void ShouldSkip_MeaninglessInput_ReturnsTrue(string? input)
    {
        Assert.True(_sut.ShouldSkipTranslation(input!));
    }

    [Theory]
    [InlineData("ああああ")]
    [InlineData("はい")]
    [InlineData("うん")]
    [InlineData("こんにちは")]
    [InlineData("Hello")]
    [InlineData("えええ")]
    [InlineData("ご視聴ありがとうございました")]
    public void ShouldSkip_NormalText_ReturnsFalse(string input)
    {
        Assert.False(_sut.ShouldSkipTranslation(input));
    }
}
