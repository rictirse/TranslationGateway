using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class MishearingFixStateTests
{
    private static MishearingFix Fix(long id, long cardId, string wrong, string right,
        string status = MishearingStatus.Approved, string kind = MishearingKind.Pre) => new()
        {
            Id = id,
            CardId = cardId,
            TermId = 1,
            Kind = kind,
            Wrong = wrong,
            Right = right,
            Status = status,
        };

    /// <summary>記錄格式化後訊息的假 logger。</summary>
    private sealed class RecordingLogger : ILogger<MishearingFixState>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>所有方法都擲例外的假 store（模擬資料庫掛掉）。</summary>
    private sealed class ThrowingGlossaryStore : IGlossaryStore
    {
        public IReadOnlyList<GlossaryCard> GetGlossaryCards() => throw new InvalidOperationException("資料庫讀取失敗");

        public IReadOnlyList<GlossaryTerm> GetGlossaryTerms() => throw new InvalidOperationException("資料庫讀取失敗");

        public IReadOnlyList<GlossaryCard> GetAllGlossaryCards() => throw new InvalidOperationException("資料庫讀取失敗");

        public IReadOnlyList<GlossaryTerm> GetAllGlossaryTerms() => throw new InvalidOperationException("資料庫讀取失敗");

        public void SaveGlossaryChanges(IReadOnlyList<GlossaryCard> cards, IReadOnlyList<GlossaryTerm> terms) =>
            throw new InvalidOperationException("資料庫讀取失敗");

        public void SaveGlossaryTermEdits(IReadOnlyList<GlossaryTerm> updated, IReadOnlyList<GlossaryTerm> inserted) =>
            throw new InvalidOperationException("資料庫讀取失敗");

        public long CreateGlossaryCard(GlossaryCard card, IReadOnlyList<GlossaryTerm> terms) =>
            throw new InvalidOperationException("資料庫讀取失敗");

        public GlossaryMergeResult MergeGlossaryCards(long sourceId, long targetId) =>
            throw new InvalidOperationException("資料庫讀取失敗");

        public void DeleteGlossaryCard(long cardId) =>
            throw new InvalidOperationException("資料庫讀取失敗");

        public void DeleteGlossaryTerms(IReadOnlyList<long> termIds) =>
            throw new InvalidOperationException("資料庫讀取失敗");

        public void UpsertTermCandidates(IReadOnlyList<TermCandidate> candidates) =>
            throw new InvalidOperationException("資料庫讀取失敗");

        public IReadOnlyList<MishearingFix> GetMishearingFixes() => throw new InvalidOperationException("資料庫讀取失敗");

        public int UpsertMishearingFixes(IReadOnlyList<MishearingProposal> proposals) =>
            throw new InvalidOperationException("資料庫讀取失敗");

        public void ApproveMishearingFixes(IReadOnlyList<long> ids) =>
            throw new InvalidOperationException("資料庫寫入失敗");

        public void DeleteMishearingFixes(IReadOnlyList<long> ids) =>
            throw new InvalidOperationException("資料庫寫入失敗");
    }

    [Fact]
    public void Apply_WrongContainsRight_ReplacesLongerWrongSpelling()
    {
        var state = new MishearingFixState();
        state.SetFixes([Fix(1, 10, "ツクリー", "ツクリ")]);

        // 錯字比正確寫法長（長音多了）：照樣替換；本來就正確的「ツクリ」不動
        Assert.Equal("ツクリだね、ツクリ", state.Apply("ツクリーだね、ツクリ", [10]));
    }

    [Fact]
    public void Apply_RightContainsWrong_DoesNotReplaceCorrectSpelling()
    {
        var state = new MishearingFixState();
        state.SetFixes([Fix(1, 10, "クリ", "ツクリ")]);

        // 已經是正確寫法的「ツクリ」不動，只換單獨出現的「クリ」
        Assert.Equal("ツクリちゃんとツクリ", state.Apply("ツクリちゃんとクリ", [10]));
        const string correct = "ツクリちゃん";
        Assert.Same(correct, state.Apply(correct, [10]));
    }

    [Fact]
    public void Apply_OnlyAppliesFixesOfCardsInActiveIds()
    {
        var sut = new MishearingFixState();
        sut.SetFixes(
        [
            Fix(1, 10, "バンダル", "ヴァンダル"),
            Fix(2, 20, "ライフル", "步槍"),
        ]);

        var result = sut.Apply("バンダルとライフル", [10L]);

        // 卡 20 不在生效清單：ライフル 不替換
        Assert.Equal("ヴァンダルとライフル", result);
    }

    [Fact]
    public void Apply_LongestWrongFirst_DoesNotDoubleReplace()
    {
        var sut = new MishearingFixState();
        sut.SetFixes(
        [
            Fix(1, 10, "ツクリー", "ツクルミー"),
            Fix(2, 10, "ツクリーちゃん", "ツクルミンちゃん"),
        ]);

        var result = sut.Apply("ツクリーちゃん登場", [10L]);

        // 長的「ツクリーちゃん」先換：短的「ツクリー」不會先吃掉一部分，也不會再對結果重換一次
        Assert.Equal("ツクルミンちゃん登場", result);
    }

    [Fact]
    public void SetFixes_FiltersPendingPostAndTooShortWrong()
    {
        var sut = new MishearingFixState();
        sut.SetFixes(
        [
            Fix(1, 10, "バンダル", "ヴァンダル", status: MishearingStatus.Pending),
            Fix(2, 10, "ライフル", "步槍", kind: MishearingKind.Post),
            Fix(3, 10, "　", "去頭尾空白後 0 個字"),
            Fix(4, 10, " x ", "去頭尾空白後只剩 1 個字"),
            Fix(5, 10, "ヴァンダール", "ヴァンダル"),
        ]);

        // SetFixes 時就過濾：只剩 Approved + Pre 而且修剪後 ≥ 2 字的那筆
        Assert.Equal(1, sut.Count);
        Assert.Equal("ヴァンダルだ", sut.Apply("ヴァンダールだ", [10L]));
    }

    [Fact]
    public void Apply_NoHitOrEmptyActiveIds_ReturnsSameInstance()
    {
        var sut = new MishearingFixState();
        sut.SetFixes([Fix(1, 10, "バンダル", "ヴァンダル")]);
        var text = "関係ない文";

        Assert.Same(text, sut.Apply(text, [10L]));
        Assert.Same(text, sut.Apply(text, Array.Empty<long>()));
        Assert.Same(string.Empty, sut.Apply(string.Empty, [10L]));
    }

    [Fact]
    public void Reload_StoreThrows_KeepsOldFixesAndLogsWarning()
    {
        var logger = new RecordingLogger();
        var sut = new MishearingFixState(logger);
        sut.SetFixes([Fix(1, 10, "バンダル", "ヴァンダル")]);
        Assert.Equal(1, sut.Count);

        sut.Reload(new ThrowingGlossaryStore());

        // 讀取失敗：保留舊清單，只寫 Warning，不擲例外
        Assert.Equal(1, sut.Count);
        Assert.Equal("ヴァンダル強い", sut.Apply("バンダル強い", [10L]));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Reload_LoadsApprovedPreFromStore_WithCardNames()
    {
        using var db = new TempDatabase();
        var cardId = db.Service.CreateGlossaryCard(
            new GlossaryCard { Kind = GlossaryKind.Game, Name = "VALORANT" },
            [new GlossaryTerm { Source = "ヴァンダル", Target = "暴徒" }]);
        var termId = db.Service.GetGlossaryTerms().Single().Id;
        Assert.Equal(1, db.Service.UpsertMishearingFixes(
            [new MishearingProposal(cardId, termId, MishearingKind.Pre, "バンダル", "ヴァンダル", "バンダル強いね")]));
        // 第二筆（ヴァンダール）新增後維持 Pending：不核准，Reload 時應被過濾
        Assert.Equal(1, db.Service.UpsertMishearingFixes(
            [new MishearingProposal(cardId, termId, MishearingKind.Pre, "ヴァンダール", "ヴァンダル", "ヴァンダールだ")]));
        db.Service.ApproveMishearingFixes([1L]);

        var logger = new RecordingLogger();
        var sut = new MishearingFixState(logger);
        sut.Reload(db.Service);

        // Pending 的 ヴァンダール 不載入，只載入核准的 バンダル；套用時 log 帶卡名
        Assert.Equal(1, sut.Count);
        Assert.Equal("ヴァンダル強い", sut.Apply("バンダル強い", [cardId]));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("聽錯字修正：バンダル → ヴァンダル（VALORANT）", entry.Message);
    }

    [Fact]
    public void Apply_SameFixTwiceInText_ReplacesBothOccurrences_LogsOnce()
    {
        var logger = new RecordingLogger();
        var sut = new MishearingFixState(logger);
        sut.SetFixes(
            [Fix(1, 10, "バンダル", "ヴァンダル")],
            new Dictionary<long, string> { [10] = "VALORANT" });

        var result = sut.Apply("バンダル強い、バンダル買った", [10L]);

        // 同一句裡同一筆修正出現兩次：兩處都換掉，但 log 只記一次
        Assert.Equal("ヴァンダル強い、ヴァンダル買った", result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("聽錯字修正：バンダル → ヴァンダル（VALORANT）", entry.Message);
    }

    // ===== 回溯紀錄：appliedFixIds 收集實際命中的修正 Id =====

    [Fact]
    public void Apply_CollectsAppliedFixIds_OnlyHits()
    {
        var sut = new MishearingFixState();
        sut.SetFixes(
        [
            Fix(5, 10, "バンダル", "ヴァンダル"),
            Fix(6, 10, "ライフル", "步槍"),
        ]);

        var appliedIds = new List<long>();
        var withIds = sut.Apply("バンダル強い", [10L], appliedIds);

        // 只收集實際命中的修正 Id（ライフル 沒出現）
        Assert.Equal("ヴァンダル強い", withIds);
        Assert.Equal([5L], appliedIds);

        // 不傳 collector：替換結果相同
        Assert.Equal(withIds, sut.Apply("バンダル強い", [10L]));
    }

    [Fact]
    public void Apply_CollectsAppliedFixIds_MultipleHitsInOrder()
    {
        var sut = new MishearingFixState();
        sut.SetFixes(
        [
            Fix(5, 10, "バンダル", "ヴァンダル"),
            Fix(6, 10, "ツクリー", "ツクリ"),
        ]);

        var appliedIds = new List<long>();
        sut.Apply("ツクリーとバンダル", [10L], appliedIds);

        // 依載入順序（Wrong 長度遞減、同長度依 Id）收集每一筆命中
        Assert.Equal([5L, 6L], appliedIds);
    }
}
