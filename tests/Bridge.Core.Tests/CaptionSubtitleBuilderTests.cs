using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>CaptionSubtitleBuilder：前處理句子與原始 cue 合併成「一個 cue 一整句」的字幕行、輸出 WebVTT。</summary>
public class CaptionSubtitleBuilderTests
{
    /// <summary>直播 23 的真實滾動 cue（原始 cue 文字彼此不重疊，只是時間重疊）。</summary>
    private static readonly CaptionCue[] RollingCues =
    [
        new CaptionCue(556000, 7040, "うん。コピー禁止。プレビルト材料の中に"),
        new CaptionCue(560120, 6440, "は提供したり変更できるものもある。全"),
        new CaptionCue(563040, 6120, "消去ボタンを使うとん。何?プレビルド"),
        new CaptionCue(566560, 6760, "材料って。え、テックしたら変更できる"),
        new CaptionCue(569160, 5936, "ものはプレイビルとなり元にもコピー禁止"),
        new CaptionCue(573320, 6840, "。"),
    ];

    private static readonly CaptionSentence[] TwoSentences =
    [
        new CaptionSentence(556000, 569160, "raw1", "うん。コピー禁止。プレビルト材料の中には提供したり変更できるものもある。", "m"),
        new CaptionSentence(563040, 573320, "raw2", "全消去ボタンを使うとん。何？プレビルド材料って。", "m"),
    ];

    [Fact]
    public void Build_RollingCuesWithoutSentences_MergesIntoFiveSentences()
    {
        var lines = CaptionSubtitleBuilder.Build(RollingCues, []);

        Assert.Equal(
        [
            "うん。コピー禁止。",
            "プレビルト材料の中には提供したり変更できるものもある。",
            "全消去ボタンを使うとん。",
            "何?プレビルド材料って。",
            "え、テックしたら変更できるものはプレイビルとなり元にもコピー禁止。",
        ], lines.Select(l => l.Text).ToArray());
        // 每句的 StartMs 是它開頭那段 cue 的 StartMs（殘段的 StartMs 屬於殘段開始的那段 cue）
        Assert.Equal([556000L, 556000L, 560120L, 563040L, 566560L], lines.Select(l => l.StartMs).ToArray());
        Assert.All(lines, l => Assert.False(l.FromSentence));
    }

    [Fact]
    public void Build_SentencesCoverAllCues_ReturnsSentenceLinesOnly()
    {
        var lines = CaptionSubtitleBuilder.Build(RollingCues, TwoSentences);

        // 6 段 cue 的 StartMs 都落在兩句的範圍內（573320 等於第二句的 EndMs，含兩端）
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.FromSentence));
        Assert.Equal(TwoSentences.Select(s => s.Text).ToArray(), lines.Select(l => l.Text).ToArray());
        Assert.Equal([556000L, 563040L], lines.Select(l => l.StartMs).ToArray());
    }

    [Fact]
    public void Build_UncoveredCueAfterSentences_MergesIntoExtraLine()
    {
        var cues = RollingCues.Append(new CaptionCue(600000, 2000, "次の話。これで終わり。")).ToArray();

        var lines = CaptionSubtitleBuilder.Build(cues, TwoSentences);

        Assert.Equal(3, lines.Count);
        Assert.True(lines[0].FromSentence);
        Assert.True(lines[1].FromSentence);
        var extra = lines[2];
        Assert.False(extra.FromSentence);
        Assert.Equal("次の話。これで終わり。", extra.Text);
        Assert.Equal(600000, extra.StartMs);
        Assert.Equal(602000, extra.EndMs);
    }

    [Fact]
    public void Build_BracketTagsRemoved_MarkerOnlyCueProducesNoLine()
    {
        var withText = CaptionSubtitleBuilder.Build(
        [
            new CaptionCue(1000, 2000, "[音楽]"),
            new CaptionCue(3000, 2000, "[音楽] こんにちは。"),
        ], []);

        var line = Assert.Single(withText);
        Assert.Equal("こんにちは。", line.Text);

        // 只剩方括號標記的 cue 不產生任何行
        var markerOnly = CaptionSubtitleBuilder.Build([new CaptionCue(1000, 2000, "[音楽]")], []);
        Assert.Empty(markerOnly);
    }

    [Fact]
    public void Build_NoSentenceEndSymbols_BreaksAtCueBoundaryWhenOver40Chars()
    {
        var a = new string('あ', 25);
        var b = new string('い', 25);
        var c = new string('う', 25);

        var lines = CaptionSubtitleBuilder.Build(
        [
            new CaptionCue(0, 1000, a),
            new CaptionCue(1000, 1000, b),
            new CaptionCue(2000, 1000, c),
        ], []);

        // 前兩段接起來 50 字（≥ 40）：下一段 cue 邊界斷句
        Assert.Equal([a + b, c], lines.Select(l => l.Text).ToArray());
        Assert.Equal([0L, 2000L], lines.Select(l => l.StartMs).ToArray());
    }

    [Fact]
    public void Build_CueStartGapOver3000ms_BreaksAtCueBoundary()
    {
        var lines = CaptionSubtitleBuilder.Build(
        [
            new CaptionCue(0, 2000, "あいうえお"),
            new CaptionCue(6000, 2000, "かきくけこ"),
        ], []);

        Assert.Equal(["あいうえお", "かきくけこ"], lines.Select(l => l.Text).ToArray());
        Assert.Equal([0L, 6000L], lines.Select(l => l.StartMs).ToArray());
    }

    [Fact]
    public void Build_EndMsClampedToNextStartAndAtLeast500ms()
    {
        // 下一行的 StartMs（563040）比原 EndMs（569160）早：EndMs 裁到下一行的 StartMs
        var overlapped = CaptionSubtitleBuilder.Build([], TwoSentences);
        Assert.Equal(563040L, overlapped[0].EndMs);

        // 原結束時間太短：至少 StartMs + 500
        var shortLine = CaptionSubtitleBuilder.Build([], [new CaptionSentence(1000, 1200, "r", "あ。", "m")]);
        Assert.Equal(1500L, Assert.Single(shortLine).EndMs);
    }

    [Fact]
    public void ToWebVtt_CueFormat()
    {
        var vtt = CaptionSubtitleBuilder.ToWebVtt([new SubtitleLine(2619079, 2619500, "テスト --> 置換", false)]);

        Assert.StartsWith("WEBVTT\n\n", vtt);
        Assert.Equal("00:43:39.079 --> 00:43:39.500\nテスト -> 置換\n", vtt["WEBVTT\n\n".Length..]);
        Assert.DoesNotContain('\r', vtt);
    }

    [Fact]
    public void ToWebVtt_EmptyLines_ReturnsHeaderOnly()
    {
        Assert.Equal("WEBVTT\n", CaptionSubtitleBuilder.ToWebVtt([]));
    }

    [Fact]
    public void ToSrt_CueFormat()
    {
        var srt = CaptionSubtitleBuilder.ToSrt([new SubtitleLine(2619079, 2619500, "テスト --> 置換", false)]);

        // 編號從 1 開始、時間用逗號、換行用 \r\n；文字的換行與 --> 處理和 ToWebVtt 相同
        Assert.Equal("1\r\n00:43:39,079 --> 00:43:39,500\r\nテスト -> 置換\r\n", srt);
    }

    [Fact]
    public void ToSrt_MultipleCues_NumberedSequentiallyAndSeparatedByBlankLine()
    {
        var srt = CaptionSubtitleBuilder.ToSrt(
        [
            new SubtitleLine(0, 1000, "一句", true),
            new SubtitleLine(2000, 3000, "二句", false),
        ]);

        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:01,000\r\n一句\r\n" +
            "\r\n" +
            "2\r\n00:00:02,000 --> 00:00:03,000\r\n二句\r\n",
            srt);
    }

    [Fact]
    public void ToSrt_EmptyLines_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, CaptionSubtitleBuilder.ToSrt([]));
    }

    [Fact]
    public void BuildChapterOverlays_TextAndTiming()
    {
        var items = new[]
        {
            new ChapterOverlayItem(0, "10位 星座になれたら ／ 結束バンド", "第10名 若能成為星座 ／ 結束樂團"),
            new ChapterOverlayItem(60000, "09位 Booo! ／ TOKOTOKO", null),
            new ChapterOverlayItem(120000, "08位 アイドル ／ 新井圭一", "08位 アイドル ／ 新井圭一"),
        };

        var overlays = CaptionSubtitleBuilder.BuildChapterOverlays(items, videoEndMs: 300000, fontSize: 12, fallbackDurationMs: 300000);

        Assert.Equal(3, overlays.Count);
        // cue 只放日文原文（不論有沒有譯文）：PotPlayer 送來翻譯時由 TranslationManager 直接回傳已翻好的中文，雙語顯示交給 PotPlayer
        Assert.Equal("{\\an7\\fs12}10位 星座になれたら ／ 結束バンド", overlays[0].Text);
        Assert.Equal("{\\an7\\fs12}09位 Booo! ／ TOKOTOKO", overlays[1].Text);
        Assert.Equal("{\\an7\\fs12}08位 アイドル ／ 新井圭一", overlays[2].Text);
        // 結束時間 = 下一章的開始；最後一章用 videoEndMs
        Assert.Equal((0L, 60000L), (overlays[0].StartMs, overlays[0].EndMs));
        Assert.Equal((60000L, 120000L), (overlays[1].StartMs, overlays[1].EndMs));
        Assert.Equal((120000L, 300000L), (overlays[2].StartMs, overlays[2].EndMs));
    }

    [Fact]
    public void BuildChapterOverlays_VideoEndAtOrBeforeStart_UsesOneMinute()
    {
        // 最後一章的 videoEndMs ≤ StartMs：結束時間用 StartMs + 60000；fontSize ≤ 0 視為 12
        var overlays = CaptionSubtitleBuilder.BuildChapterOverlays([new ChapterOverlayItem(120000, "終章", null)], 120000, 0, 300000);

        var overlay = Assert.Single(overlays);
        Assert.Equal(120000, overlay.StartMs);
        Assert.Equal(180000, overlay.EndMs);
        Assert.Equal("{\\an7\\fs12}終章", overlay.Text);
    }

    // 查到原曲長度：結束時間 = 開始＋長度＋5 秒收尾（不超過下一章／影片結尾）。
    [Fact]
    public void BuildChapterOverlays_WithDuration_StartPlusDurationPlus5Seconds()
    {
        var overlays = CaptionSubtitleBuilder.BuildChapterOverlays(
            [new ChapterOverlayItem(60000, "曲A", null, DurationMs: 200000)],
            videoEndMs: 900000, fontSize: 12, fallbackDurationMs: 300000);

        var overlay = Assert.Single(overlays);
        Assert.Equal(60000, overlay.StartMs);
        Assert.Equal(60000 + 200000 + 5000, overlay.EndMs);
    }

    // 開始＋長度＋5 秒超過下一章開始：截在下一章（最後一章截在 videoEndMs）。
    [Fact]
    public void BuildChapterOverlays_WithDuration_CappedAtNextChapterOrVideoEnd()
    {
        var overlays = CaptionSubtitleBuilder.BuildChapterOverlays(
            [
                new ChapterOverlayItem(0, "曲A", null, DurationMs: 300000),
                new ChapterOverlayItem(60000, "曲B", null, DurationMs: 900000),
            ],
            videoEndMs: 700000, fontSize: 12, fallbackDurationMs: 300000);

        // 曲A唱 5 分鐘但下一章 1 分鐘就開始：截在下一章
        Assert.Equal(60000, overlays[0].EndMs);
        // 曲B是最後一章：截在 videoEndMs
        Assert.Equal(700000, overlays[1].EndMs);
    }

    // 查不到原曲長度（DurationMs = null）：顯示 fallback 的長度（不加收尾秒數）。
    [Fact]
    public void BuildChapterOverlays_WithoutDuration_UsesFallback()
    {
        var overlays = CaptionSubtitleBuilder.BuildChapterOverlays(
            [new ChapterOverlayItem(60000, "配信開始", null)],
            videoEndMs: 900000, fontSize: 12, fallbackDurationMs: 120000);

        var overlay = Assert.Single(overlays);
        Assert.Equal(60000 + 120000, overlay.EndMs);
    }

    // fallbackDurationMs ≤ 0 視為 5 分鐘。
    [Fact]
    public void BuildChapterOverlays_NonPositiveFallback_TreatedAs5Minutes()
    {
        var overlays = CaptionSubtitleBuilder.BuildChapterOverlays(
            [new ChapterOverlayItem(0, "配信開始", null)],
            videoEndMs: 900000, fontSize: 12, fallbackDurationMs: 0);

        var overlay = Assert.Single(overlays);
        Assert.Equal(300000, overlay.EndMs);
    }

    [Fact]
    public void ToSrt_WithOverlays_MergedAndRenumbered()
    {
        var lines = new[]
        {
            new SubtitleLine(1000, 3000, "こんにちは。", true),
            new SubtitleLine(3000, 5000, "さようなら。", true),
        };
        var overlays = CaptionSubtitleBuilder.BuildChapterOverlays(
            [new ChapterOverlayItem(0, "10位 星座になれたら ／ 結束バンド", "第10名 若能成為星座 ／ 結束樂團")],
            videoEndMs: 5000, fontSize: 12, fallbackDurationMs: 300000);

        var srt = CaptionSubtitleBuilder.ToSrt(lines, overlays);

        // overlay 與一般字幕依開始時間合併、重新編號；overlay 只放日文原文（{\an7\fs12} 原樣）
        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:05,000\r\n{\\an7\\fs12}10位 星座になれたら ／ 結束バンド\r\n" +
            "\r\n" +
            "2\r\n00:00:01,000 --> 00:00:03,000\r\nこんにちは。\r\n" +
            "\r\n" +
            "3\r\n00:00:03,000 --> 00:00:05,000\r\nさようなら。\r\n",
            srt);
    }

    [Fact]
    public void ToSrt_WithOverlays_SameStart_SubtitleLineFirst()
    {
        var srt = CaptionSubtitleBuilder.ToSrt(
            [new SubtitleLine(0, 1000, "字幕", true)],
            [new SubtitleOverlay(0, 1000, "{\\an7\\fs12}章節")]);

        // 同時間：一般字幕在前、overlay 在後
        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:01,000\r\n字幕\r\n" +
            "\r\n" +
            "2\r\n00:00:00,000 --> 00:00:01,000\r\n{\\an7\\fs12}章節\r\n",
            srt);
    }

    [Fact]
    public void ToSrt_WithoutOverlays_UnchangedBehavior()
    {
        var lines = new[] { new SubtitleLine(0, 1000, "一句", true) };

        Assert.Equal(CaptionSubtitleBuilder.ToSrt(lines), CaptionSubtitleBuilder.ToSrt(lines, []));
    }
}
