using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>GlossaryMerger.Plan 的純邏輯測試：衝突判斷、搬移（改 Pending）、別名與卡片欄位合併規則。</summary>
public class GlossaryMergerTests
{
    private static GlossaryCard Card(long id, string name, string kind = GlossaryKind.Character,
        long? parentId = null, string? channelId = null, string? channelHandle = null,
        string? aliases = null, string? description = null, string? sourceUrl = null,
        string status = "Approved") => new()
        {
            Id = id,
            Kind = kind,
            Name = name,
            ParentId = parentId,
            ChannelId = channelId,
            ChannelHandle = channelHandle,
            Aliases = aliases,
            Description = description,
            SourceUrl = sourceUrl,
            Status = status,
        };

    private static GlossaryTerm Term(long id, long cardId, string source, string status = "Approved") => new()
    {
        Id = id,
        CardId = cardId,
        Source = source,
        Target = $"{source}譯",
        Status = status,
    };

    [Fact]
    public void Plan_HiraganaKatakanaAndFullWidthDifferences_CountAsConflicts()
    {
        var target = Card(6, "英リサ");
        var targetTerms = new List<GlossaryTerm>
        {
            Term(61, 6, "ウルハ"),      // 片假名
            Term(62, 6, "ＡＰＥＸ"),    // 全形
            Term(63, 6, "Hanabusa Lisa"),
        };
        var source = Card(40, "うるは先輩");
        var sourceTerms = new List<GlossaryTerm>
        {
            Term(401, 40, "うるは"),        // 平假名：與 ウルハ SameName → 衝突
            Term(402, 40, "APEX"),          // 半形：與 ＡＰＥＸ SameName → 衝突
            Term(403, 40, "ｈａｎａｂｕｓａ ｌｉｓａ"),  // 全形：與 Hanabusa Lisa SameName（全半形＋大小寫）→ 衝突
            Term(404, 40, "すぺくたー"),    // 不衝突：搬過去
        };

        var plan = GlossaryMerger.Plan(source, sourceTerms, target, targetTerms);

        Assert.Equal([404L], plan.TermsToMove.Select(t => t.Id).ToList());
        // 衝突以既有（target）為主，記下對應的既有名詞 Id
        Assert.Equal([(401L, 61L), (402L, 62L), (403L, 63L)], plan.DroppedTerms);
    }

    [Fact]
    public void Plan_SourceInternalDuplicate_DropsSecondMapsToFirst()
    {
        // target 沒有對應名詞，但 source 卡內部兩個名詞對到同一個新寫法（平假名／片假名）
        var target = Card(6, "英リサ");
        var source = Card(40, "うるは先輩");
        var sourceTerms = new List<GlossaryTerm>
        {
            Term(401, 40, "ウルハ", status: "Pending"),
            Term(402, 40, "うるは", status: "Approved"),
        };

        var plan = GlossaryMerger.Plan(source, sourceTerms, target, []);

        Assert.Equal([401L], plan.TermsToMove.Select(t => t.Id).ToList());
        // 第二個對應到第一個（搬過去後的同一個 Id）
        Assert.Equal([(402L, 401L)], plan.DroppedTerms);
    }

    [Fact]
    public void Plan_MovedTerms_KeepIdAndBecomePending()
    {
        var target = Card(6, "英リサ");
        var source = Card(40, "うるは先輩");
        var sourceTerms = new List<GlossaryTerm>
        {
            new()
            {
                Id = 401,
                CardId = 40,
                Source = "うるは",
                Target = "潤羽露西亞",
                Aliases = "Uruha",
                Category = "人名",
                Note = "備註",
                Status = "Approved",
            },
            new() { Id = 402, CardId = 40, Source = " filament ", Target = "菲拉門特", Status = "Rejected" },
        };

        var plan = GlossaryMerger.Plan(source, sourceTerms, target, []);

        // Status 一律 Pending（Rejected 也照搬），其他欄位與原 Id 不動
        Assert.All(plan.TermsToMove, t => Assert.Equal("Pending", t.Status));
        var moved = Assert.Single(plan.TermsToMove, t => t.Id == 401);
        Assert.Equal("うるは", moved.Source);
        Assert.Equal("潤羽露西亞", moved.Target);
        Assert.Equal("Uruha", moved.Aliases);
        Assert.Equal("人名", moved.Category);
        Assert.Equal("備註", moved.Note);
    }

    [Fact]
    public void Plan_Aliases_AddsSourceNameAndAliasesWithoutDuplicates()
    {
        var target = Card(6, "英リサ", aliases: "Hanabusa\nえいりさ");
        var source = Card(39, "Hanabusa Lisa", aliases: "hanabusa\nリサ\n英リサ\nエリサ");

        var plan = GlossaryMerger.Plan(source, [], target, []);

        // source 卡名與別名併進去；重複（不分大小寫）與 target 卡名相同的不加
        Assert.Equal("Hanabusa\nえいりさ\nHanabusa Lisa\nリサ\nエリサ", plan.NewTargetAliases);
        Assert.Equal(3, plan.AddedAliases);
    }

    [Fact]
    public void Plan_Aliases_TargetNullAliases_StillCountsCorrectly()
    {
        var target = Card(6, "英リサ");
        var source = Card(39, "Hanabusa Lisa", aliases: "リサ");

        var plan = GlossaryMerger.Plan(source, [], target, []);

        Assert.Equal("Hanabusa Lisa\nリサ", plan.NewTargetAliases);
        Assert.Equal(2, plan.AddedAliases);
    }

    [Fact]
    public void Plan_Aliases_AllDuplicates_AddsNothing()
    {
        var target = Card(6, "英リサ", aliases: "リサ");
        var source = Card(39, "リサ", aliases: "リサ");

        var plan = GlossaryMerger.Plan(source, [], target, []);

        Assert.Equal("リサ", plan.NewTargetAliases);
        Assert.Equal(0, plan.AddedAliases);
    }

    [Fact]
    public void Plan_CardFields_TargetValuesWin_BlankFilledFromSource()
    {
        var target = Card(6, "英リサ", kind: GlossaryKind.Character, parentId: 3,
            channelId: "UC123", channelHandle: "  ", description: null, sourceUrl: "https://target",
            status: "Approved");
        var source = Card(39, "Hanabusa Lisa", kind: GlossaryKind.Game, parentId: 9,
            channelId: "UC999", channelHandle: "@lisahanabusa", description: "說明", sourceUrl: "https://source",
            status: "Pending");

        var plan = GlossaryMerger.Plan(source, [], target, []);

        // target 有值的保留（ChannelId、ParentId、SourceUrl），空白或 null 才補 source 的（ChannelHandle、Description）
        Assert.Equal(3L, plan.NewTargetParentId);
        Assert.Equal("UC123", plan.NewTargetChannelId);
        Assert.Equal("@lisahanabusa", plan.NewTargetChannelHandle);
        Assert.Equal("說明", plan.NewTargetDescription);
        Assert.Equal("https://target", plan.NewTargetSourceUrl);
    }

    [Fact]
    public void Plan_CardFields_TargetBlank_FilledFromSource()
    {
        var target = Card(6, "英リサ");
        var source = Card(39, "Hanabusa Lisa", parentId: 9, channelId: "UC999",
            channelHandle: "@lisahanabusa", description: "說明", sourceUrl: "https://source");

        var plan = GlossaryMerger.Plan(source, [], target, []);

        Assert.Equal(9L, plan.NewTargetParentId);
        Assert.Equal("UC999", plan.NewTargetChannelId);
        Assert.Equal("@lisahanabusa", plan.NewTargetChannelHandle);
        Assert.Equal("說明", plan.NewTargetDescription);
        Assert.Equal("https://source", plan.NewTargetSourceUrl);
    }

    [Fact]
    public void Plan_ParentIdPointingToEitherCard_BecomesNull()
    {
        // target 的上層是 source：合併後不能指到自己
        var target = Card(6, "英リサ", parentId: 39);
        var source = Card(39, "Hanabusa Lisa");
        Assert.Null(GlossaryMerger.Plan(source, [], target, []).NewTargetParentId);

        // target 沒有上層、source 的上層是 target：也不能指到自己
        var target2 = Card(6, "英リサ");
        var source2 = Card(39, "Hanabusa Lisa", parentId: 6);
        Assert.Null(GlossaryMerger.Plan(source2, [], target2, []).NewTargetParentId);

        // 指向其他卡時照用 source 的上層
        var source3 = Card(39, "Hanabusa Lisa", parentId: 5);
        Assert.Equal(5L, GlossaryMerger.Plan(source3, [], target2, []).NewTargetParentId);
    }
}
