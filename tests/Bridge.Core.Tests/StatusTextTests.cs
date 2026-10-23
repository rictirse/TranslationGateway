using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>StatusText：狀態列文字的組合（前處理進度同一行更新，其他訊息附加）。</summary>
public class StatusTextTests
{
    [Fact]
    public void AppendOrReplace_PreprocessProgress_ReplacesLastPreprocessLine()
    {
        var status = "YouTube 字幕：已載入 250 段\n生成式字幕前處理：開始（共 3 批，已完成 0 批）";

        var result = StatusText.AppendOrReplace(status, "生成式字幕前處理：進行中（1/3 批）");

        Assert.Equal("YouTube 字幕：已載入 250 段\n生成式字幕前處理：進行中（1/3 批）", result);
    }

    [Fact]
    public void AppendOrReplace_LastLineIsOtherMessage_AppendsNewLine()
    {
        var status = "生成式字幕前處理：進行中（1/3 批）\n已套用詞彙卡：宙科そぴあ";

        var result = StatusText.AppendOrReplace(status, "生成式字幕前處理：進行中（2/3 批）");

        Assert.Equal(status + "\n生成式字幕前處理：進行中（2/3 批）", result);
    }

    [Fact]
    public void AppendOrReplace_EmptyStatus_ReturnsMessage()
    {
        Assert.Equal("YouTube 字幕：已載入 3 段", StatusText.AppendOrReplace(string.Empty, "YouTube 字幕：已載入 3 段"));
    }

    [Fact]
    public void AppendOrReplace_MessageNotPreprocess_AlwaysAppends()
    {
        // 最後一行是前處理、但新訊息不是：附加
        var status = "生成式字幕前處理：進行中（1/3 批）";
        Assert.Equal(
            status + "\nYouTube 字幕：已載入 3 段",
            StatusText.AppendOrReplace(status, "YouTube 字幕：已載入 3 段"));

        // 兩者都不是前處理：附加
        var status2 = "開啟中…";
        Assert.Equal(
            status2 + "\n已套用詞彙卡：宙科そぴあ",
            StatusText.AppendOrReplace(status2, "已套用詞彙卡：宙科そぴあ"));
    }

    [Fact]
    public void AppendOrReplace_MultiplePreprocessLines_OnlyLastLineReplaced()
    {
        var status = "生成式字幕前處理：完成，共 250 句\n其他訊息\n生成式字幕前處理：開始（共 3 批，已完成 0 批）";

        var result = StatusText.AppendOrReplace(status, "生成式字幕前處理：進行中（3/3 批）");

        Assert.Equal("生成式字幕前處理：完成，共 250 句\n其他訊息\n生成式字幕前處理：進行中（3/3 批）", result);
    }
}
