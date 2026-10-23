using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 資料探勘流程（DI singleton）：情境文字或頻道名稱＋直播標題 → GLM 拆名詞 → 比對詞彙卡 → 查 Wikidata／Wikipedia／Fandom
/// → GLM 建卡 → 生效。只有三種名詞建卡（人名、團體、遊戲），其餘存進名詞候選；
/// 情境探勘由使用者手動觸發、開台探勘由開台流程自動觸發，不寫 SubtitleLog／StreamCards，每一步都記 Information log。
/// </summary>
public sealed class MiningService(
    ILorebookLlm llm,
    IKnowledgeSources knowledge,
    IGlossaryStore glossaryStore,
    GlossaryState glossaryState,
    ILogger<MiningService> logger)
{
    /// <summary>重要性前幾名才進一步查詢與建卡，其餘存進名詞候選。</summary>
    private const int MaxProcessedNouns = 5;

    /// <summary>名詞候選的 Context 上限（字）。</summary>
    private const int CandidateContextLength = 200;

    /// <summary>卡片簡介（summary）與 prompt 簡介行共用上限（字）。</summary>
    private const int SummaryMaxLength = 100;

    /// <summary>GLM 給的卡片別名上限（個）。</summary>
    private const int MaxCardAliases = 5;

    /// <summary>名詞候選的 Origin：情境探勘。</summary>
    private const string SituationOrigin = "Situation";

    /// <summary>名詞候選的 Origin：開台資料探勘（頻道名稱＋直播標題）。</summary>
    private const string StreamInfoOrigin = "StreamInfo";

    /// <summary>名詞候選的 Origin：團體卡成員探勘（抽屜成員區塊的一鍵探勘）。</summary>
    private const string MemberOrigin = "Member";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // GLM 偶爾把數字回成字串（"importance": "5"）
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>採掘直播情境的文字：拆名詞 → 依重要性處理前 5 個 → 查詢知識來源 → 比對／建立詞彙卡；
    /// 其餘名詞與不建卡的名詞存進 TermCandidates。每一步回報 progress 並記 Information log。</summary>
    public Task<MiningResult> MineSituationAsync(string situation, IReadOnlyList<string> activeCardNames,
        long? streamId, IProgress<string>? progress, CancellationToken ct)
    {
        logger.LogInformation("資料探勘開始：拆解情境名詞（{Length} 字）", situation.Length);
        return MineAsync(
            MiningPrompts.BuildExtractUser(situation, activeCardNames),
            SituationOrigin,
            Truncate(situation.Trim(), CandidateContextLength),
            situation,
            streamId,
            progress,
            ct);
    }

    /// <summary>開台資料探勘：主播沒有角色卡時，以頻道名稱、直播標題與說明欄代替情境文字採礦，
    /// 流程與 <see cref="MineSituationAsync"/> 相同；頻道名稱與直播標題都空白時不呼叫 GLM，
    /// 直接回傳空結果（只有說明欄不算）。</summary>
    public Task<MiningResult> MineStreamAsync(string? channelName, string? title, string? description,
        IReadOnlyList<string> activeCardNames, long? streamId, IProgress<string>? progress, CancellationToken ct)
    {
        var channel = (channelName ?? string.Empty).Trim();
        var streamTitle = (title ?? string.Empty).Trim();
        if (channel.Length == 0 && streamTitle.Length == 0)
        {
            logger.LogInformation("開台資料探勘略過：頻道名稱與直播標題都是空白");
            return Task.FromResult(new MiningResult([], 0, 0, null));
        }

        logger.LogInformation("開台資料探勘開始：頻道「{Channel}」，標題「{Title}」，說明欄 {Length} 字（過濾後）",
            channel, streamTitle, StreamDescriptionFilter.Filter(description).Length);
        return MineAsync(
            MiningPrompts.BuildStreamExtractUser(channel, streamTitle, description, activeCardNames),
            StreamInfoOrigin,
            Truncate($"頻道：{channel}／標題：{streamTitle}", CandidateContextLength),
            $"{channel} {streamTitle}",
            streamId,
            progress,
            ct);
    }

    /// <summary>兩種探勘共用的主流程：拆名詞 → 前 5 名四道關卡 → 建卡／候選。
    /// 差異只有拆名詞的 user 訊息、候選的 Origin 與 Context，由參數傳入。</summary>
    private async Task<MiningResult> MineAsync(string extractUser, string origin, string context, string positionText,
        long? streamId, IProgress<string>? progress, CancellationToken ct)
    {
        // 資料探勘是使用者在等的工作：以 Mining 優先度排進知識庫模型的隊伍
        using var _ = LlmPriorityScope.Enter(LlmPriority.Mining);
        var stopwatch = Stopwatch.StartNew();
        var run = new RunState();
        LoadRejectedCards(run);

        // ===== 第 1 步：拆名詞 =====
        progress?.Report("拆解名詞中…");
        LlmJsonResult extractJson;
        using (var extractPurpose = LlmCallPurposeScope.Enter(LlmCallPurposes.Mining))
        {
            extractJson = await llm.CompleteJsonAsync(
                MiningPrompts.ExtractSystem, extractUser, ct).ConfigureAwait(false);
        }
        run.TotalTokens += extractJson.PromptTokens + extractJson.CompletionTokens;

        var nouns = ParseNouns(extractJson.Content);
        if (nouns is null)
        {
            logger.LogWarning("資料探勘：拆名詞的 AI 回覆無法解析");
            return new MiningResult([], run.TotalTokens, stopwatch.ElapsedMilliseconds, "AI 回覆無法解析");
        }

        return await ProcessNounsAsync(
            (nouns.Nouns ?? [])
                .Select(n => new MinedNoun((n.Surface ?? string.Empty).Trim(), (n.Kind ?? string.Empty).Trim(), n.Importance ?? 0)),
            origin, context, positionText, streamId, progress, ct, run, stopwatch).ConfigureAwait(false);
    }

    /// <summary>用一個名字探勘（不拆名詞）：當作 person 名詞直接進入四道關卡；團體卡成員的情況由第 2 關的 memberOfGroup 處理，
    /// 建出的角色卡上層會是那個團體。名稱修剪後少於 2 個字時直接回傳空結果，不呼叫任何模型。</summary>
    public async Task<MiningResult> MineNameAsync(string name, long? streamId, IProgress<string>? progress, CancellationToken ct)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length < 2)
        {
            logger.LogInformation("成員探勘略過：名稱「{Name}」太短", trimmed);
            return new MiningResult([], 0, 0, null);
        }

        // 資料探勘是使用者在等的工作：以 Mining 優先度排進知識庫模型的隊伍
        using var _ = LlmPriorityScope.Enter(LlmPriority.Mining);
        var stopwatch = Stopwatch.StartNew();
        var run = new RunState();
        LoadRejectedCards(run);
        logger.LogInformation("成員探勘開始：{Name}", trimmed);
        return await ProcessNounsAsync(
            [new MinedNoun(trimmed, "person", 10)],
            MemberOrigin, $"成員：{trimmed}", trimmed, streamId, progress, ct, run, stopwatch).ConfigureAwait(false);
    }

    /// <summary>拆名詞之後的共用流程（<see cref="MineAsync"/> 與 <see cref="MineNameAsync"/> 共用）：
    /// 修剪排序 → 前 5 名四道關卡 → 建卡／候選，完成時記 log。
    /// run 與 stopwatch 由呼叫端建立（中途失敗回傳時也要帶累計 tokens 與耗時）。</summary>
    private async Task<MiningResult> ProcessNounsAsync(IEnumerable<MinedNoun> nouns, string origin, string context,
        string positionText, long? streamId, IProgress<string>? progress, CancellationToken ct, RunState run, Stopwatch stopwatch)
    {
        var items = new List<MiningItem>();

        // 修剪、丟掉太短的、同一個 surface 只留一次；依 importance 由高到低排序，相同時依出現位置
        var ordered = nouns
            .Select(n => new MinedNoun(n.Surface.Trim(), n.Kind.Trim(), n.Importance))
            .Where(n => n.Surface.Length >= 2)
            .GroupBy(n => n.Surface, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(n => n.Importance)
            .ThenBy(n => positionText.IndexOf(n.Surface, StringComparison.Ordinal))
            .ToList();
        logger.LogInformation("資料探勘：拆出 {Count} 個名詞，處理重要性前 {Max} 個", ordered.Count, MaxProcessedNouns);

        // 前 5 名以外全部存進名詞候選
        var overflow = ordered.Skip(MaxProcessedNouns).ToList();
        if (overflow.Count > 0)
        {
            glossaryStore.UpsertTermCandidates(overflow
                .Select(n => new TermCandidate(n.Surface, n.Kind, n.Importance, origin, streamId, context))
                .ToList());
            items.AddRange(overflow.Select(n =>
                new MiningItem(n.Surface, MiningOutcome.Candidate, null, null, "重要性 5 名以後")));
        }

        var toProcess = ordered.Take(MaxProcessedNouns).ToList();
        for (var i = 0; i < toProcess.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var noun = toProcess[i];
            try
            {
                items.Add(await ProcessNounAsync(noun, origin, streamId, context, i + 1, toProcess.Count, run, progress, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "資料探勘：處理 {Surface} 失敗，繼續下一個", noun.Surface);
                items.Add(new MiningItem(noun.Surface, MiningOutcome.Failed, null, null, ex.Message));
            }
        }

        stopwatch.Stop();
        logger.LogInformation(
            "資料探勘完成：{Total} 個名詞（新增 {Created}、併入 {Alias}、比對 {Matched}、候選 {Candidate}、查不到 {NotFound}、已拒絕 {Rejected}、失敗 {Failed}），{Tokens} tokens，{ElapsedMs} ms",
            items.Count,
            items.Count(item => item.Outcome == MiningOutcome.Created),
            items.Count(item => item.Outcome == MiningOutcome.AddedAlias),
            items.Count(item => item.Outcome == MiningOutcome.Matched),
            items.Count(item => item.Outcome == MiningOutcome.Candidate),
            items.Count(item => item.Outcome == MiningOutcome.NotFound),
            items.Count(item => item.Outcome == MiningOutcome.Rejected),
            items.Count(item => item.Outcome == MiningOutcome.Failed),
            run.TotalTokens,
            stopwatch.ElapsedMilliseconds);
        return new MiningResult(items, run.TotalTokens, stopwatch.ElapsedMilliseconds, null);
    }

    /// <summary>單一名詞的處理：種類過濾 → 第 2 關比對 → 查詢知識來源 → 第 1 關比對 → 第 3 關建卡。</summary>
    private async Task<MiningItem> ProcessNounAsync(MinedNoun noun, string origin, long? streamId, string context,
        int index, int total, RunState run, IProgress<string>? progress, CancellationToken ct)
    {
        var surface = noun.Surface;
        var catalog = glossaryState.Catalog;

        // ===== 第 2 步：只有 person、group、game 建卡，其他存進名詞候選 =====
        if (noun.Kind is not ("person" or "group" or "game"))
        {
            StoreCandidate(noun, origin, streamId, context);
            logger.LogInformation("資料探勘：{Surface} 是 {Kind}，不建卡，存進名詞候選", surface, noun.Kind);
            return new MiningItem(surface, MiningOutcome.Candidate, null, null, $"種類 {noun.Kind} 不建卡");
        }

        // ===== 拒絕的卡當黑名單（第 2 關之前）：surface（含去掉稱謂後）與被拒絕卡的卡名或別名相同時，
        // 不查 Wiki、不建卡、不存候選 =====
        if (FindRejectedBySurface(run.RejectedCards, surface) is { } rejected)
        {
            logger.LogInformation("資料探勘：{Surface} 比對到已拒絕的卡「{CardName}」，略過", surface, rejected.Name);
            return new MiningItem(surface, MiningOutcome.Rejected, null, null, $"已拒絕的卡：{rejected.Name}");
        }

        // ===== 第 3 步：第 2 關（比對寫法，含去掉結尾稱謂） =====
        // 人名比對到團體卡、而且不是卡名本身（只是團體卡名詞裡的成員名）時，不算已經有角色卡：
        // 記下這張團體卡，繼續查詢知識來源以建立角色卡
        GlossaryCard? memberOfGroup = null;
        var directMatch = catalog.FindByNormalizedName(surface);
        if ((directMatch ?? catalog.FindByNormalizedNameOrStripped(surface)) is { } firstMatch)
        {
            // 先命中的是團體卡時，再排除團體卡比一次：人名也是某張角色卡的名詞或別名時，算比對到那張角色卡
            var matched = firstMatch.Kind == GlossaryKind.Group && noun.Kind == "person"
                ? catalog.FindByNormalizedNameOrStripped(surface, excludeGroups: true) ?? firstMatch
                : firstMatch;
            if (noun.Kind == "person" && matched.Kind == GlossaryKind.Group &&
                !GlossaryNameNormalizer.SameName(matched.Name, surface))
            {
                memberOfGroup = matched;
                logger.LogInformation("資料探勘：{Surface} 只出現在團體卡「{GroupName}」的名詞裡，繼續查詢以建立角色卡", surface, matched.Name);
            }
            else
            {
                if (directMatch is null)
                {
                    logger.LogInformation("資料探勘：{Surface} 去掉稱謂後比對到既有卡「{CardName}」，不查 Wiki", surface, matched.Name);
                }
                else
                {
                    logger.LogInformation("資料探勘：{Surface} 比對到既有卡「{CardName}」，不查 Wiki", surface, matched.Name);
                }
                return new MiningItem(surface, MiningOutcome.Matched, matched.Id, matched.Name, null);
            }
        }

        // 型錄要等探勘結束後才重載，本次剛建立的卡（含名詞與別名）另外用 CreatedCatalog 比對（同樣套用團體卡名詞的規則）
        var directThisRun = run.CreatedCatalog.FindByNormalizedName(surface);
        if ((directThisRun ?? run.CreatedCatalog.FindByNormalizedNameOrStripped(surface)) is { } firstThisRun)
        {
            var matchedThisRun = firstThisRun.Kind == GlossaryKind.Group && noun.Kind == "person"
                ? run.CreatedCatalog.FindByNormalizedNameOrStripped(surface, excludeGroups: true) ?? firstThisRun
                : firstThisRun;
            if (noun.Kind == "person" && matchedThisRun.Kind == GlossaryKind.Group &&
                !GlossaryNameNormalizer.SameName(matchedThisRun.Name, surface))
            {
                memberOfGroup ??= matchedThisRun;
                logger.LogInformation("資料探勘：{Surface} 只出現在團體卡「{GroupName}」的名詞裡，繼續查詢以建立角色卡", surface, matchedThisRun.Name);
            }
            else
            {
                if (directThisRun is null)
                {
                    logger.LogInformation("資料探勘：{Surface} 去掉稱謂後比對到本次剛建立的卡「{CardName}」，不查 Wiki", surface, matchedThisRun.Name);
                }
                else
                {
                    logger.LogInformation("資料探勘：{Surface} 比對到本次剛建立的卡「{CardName}」，不查 Wiki", surface, matchedThisRun.Name);
                }
                return new MiningItem(surface, MiningOutcome.Matched, matchedThisRun.Id, matchedThisRun.Name, null);
            }
        }

        // ===== 第 4 步：查詢 Wikidata／Wikipedia／Fandom =====
        progress?.Report($"查詢中 {index}/{total}：{surface}");
        logger.LogInformation("資料探勘：查詢 {Surface}（{Index}/{Total}）", surface, index, total);
        var entity = await LookupWikidataAsync(surface, ct).ConfigureAwait(false);

        // 第 1 關（Wikidata 編號相同）：surface 當作新名詞加進那張卡
        if (entity is not null && catalog.FindBySourceUrl(entity.Url) is { } byUrl)
        {
            logger.LogInformation("資料探勘：{Surface} 的 Wikidata {Id} 與卡「{CardName}」相同（第 1 關）", surface, entity.Id, byUrl.Name);
            return AddAlias(byUrl, surface);
        }

        var wikiPage = entity?.JaWikiTitle is { } jaTitle
            ? await knowledge.GetWikipediaPageAsync("ja", jaTitle, ct).ConfigureAwait(false)
            : entity is null
                ? await LookupWikipediaBySearchAsync(surface, ct).ConfigureAwait(false)
                : null;
        var fandomPage = noun.Kind == "person"
            ? await LookupFandomAsync(surface, ct).ConfigureAwait(false)
            : null;

        // 第 1 關（來源網址相同）：查完三個來源後，Wikidata／Wikipedia／Fandom 任一網址指向本次剛建立的卡就併入。
        // 同一次探勘剛建的卡還不在型錄裡（型錄由 Dashboard 在探勘完成後才重載），另外查本次建立的紀錄
        if (FindCreatedThisRun(run, entity, wikiPage, fandomPage) is { } createdThisRun)
        {
            logger.LogInformation("資料探勘：{Surface} 的來源網址與本次剛建立的卡「{CardName}」相同（第 1 關）", surface, createdThisRun.Name);
            return AddAlias(createdThisRun, surface, run.CreatedCatalog);
        }

        if (entity is null && wikiPage is null && fandomPage is null)
        {
            StoreCandidate(noun, origin, streamId, context);
            logger.LogInformation("資料探勘：{Surface} 三個知識來源都查不到，存進名詞候選", surface);
            return new MiningItem(surface, MiningOutcome.NotFound, null, null, null);
        }

        // ===== 第 5 步：第 3 關（GLM 判斷）加上建卡 =====
        progress?.Report($"整理詞彙卡：{surface}");
        logger.LogInformation("資料探勘：整理詞彙卡 {Surface}", surface);
        // 相近卡：既有型錄加上本次剛建立的卡（型錄還沒重載），依 Id 去重、最多 5 筆；
        // memberOfGroup 不放進來，避免 GLM 回 existing 把人名併進團體卡
        var similarCards = catalog.FindSimilar(surface)
            .Concat(run.CreatedCatalog.FindSimilar(surface))
            .Where(card => memberOfGroup is null || card.Id != memberOfGroup.Id)
            .GroupBy(card => card.Id)
            .Select(group => group.First())
            .Take(5)
            .ToList();
        LlmJsonResult composeJson;
        using (var composePurpose = LlmCallPurposeScope.Enter(LlmCallPurposes.Mining))
        {
            composeJson = await llm.CompleteJsonAsync(
                MiningPrompts.ComposeSystem,
                MiningPrompts.BuildComposeUser(surface, noun.Kind, entity, wikiPage, fandomPage, similarCards),
                ct).ConfigureAwait(false);
        }
        run.TotalTokens += composeJson.PromptTokens + composeJson.CompletionTokens;

        var decision = ParseCompose(composeJson.Content);
        if (decision is null)
        {
            StoreCandidate(noun, origin, streamId, context);
            logger.LogWarning("資料探勘：{Surface} 的建卡 AI 回覆無法解析，存進名詞候選", surface);
            return new MiningItem(surface, MiningOutcome.Failed, null, null, "AI 回覆無法解析");
        }

        // existing：cardId 必須在 similarCards 中（含本次剛建立的卡），否則視為 new；
        // cardId 是 memberOfGroup 時一律視為 new（人名不併進團體卡，照樣建角色卡）
        if (string.Equals(decision.Decision, "existing", StringComparison.OrdinalIgnoreCase) &&
            decision.CardId is { } cardId &&
            cardId != memberOfGroup?.Id &&
            similarCards.Any(card => card.Id == cardId) &&
            (catalog.FindCard(cardId) ?? run.CreatedCatalog.FindCard(cardId)) is { } existing)
        {
            return AddAlias(existing, surface, run.CreatedCatalog);
        }

        // GLM 決定 new、建卡之前：Name 與被拒絕卡的卡名或別名相同時，同樣當成黑名單，不建卡
        var newName = decision.Name?.Trim() ?? string.Empty;
        if (FindRejectedByName(run.RejectedCards, newName) is { } rejectedByName)
        {
            logger.LogInformation("資料探勘：{Surface} 比對到已拒絕的卡「{CardName}」，略過", surface, rejectedByName.Name);
            return new MiningItem(surface, MiningOutcome.Rejected, null, null, $"已拒絕的卡：{rejectedByName.Name}");
        }

        // 卡名撞名不重建：Name 與任一張未被拒絕的既有卡（型錄加上本次剛建立的卡）卡名相同時，
        // 代表 GLM 雖然回 new、其實是同一張卡，改成把 surface 併入那張卡
        if (FindCardByName(catalog.Cards.Concat(run.CreatedCatalog.Cards), newName) is { } sameName)
        {
            logger.LogInformation("資料探勘：{Surface} 的建卡名稱「{Name}」與既有卡相同，改為併入", surface, newName);
            return AddAlias(sameName, surface, run.CreatedCatalog);
        }

        var created = CreateNewCard(decision, noun, similarCards, memberOfGroup, entity, wikiPage, fandomPage, origin, streamId, context,
            out var newCard, out var newTerms);
        if (newCard is not null)
        {
            run.AddCreatedCard(newCard, newTerms, entity, wikiPage, fandomPage);
        }
        return created;
    }

    /// <summary>第 1 關的本次建立卡比對：Wikidata／Wikipedia／Fandom 任一網址有記在 CreatedByUrl 就回傳那張卡。</summary>
    private static GlossaryCard? FindCreatedThisRun(RunState run, WikidataEntity? entity, WikiPage? wikiPage, WikiPage? fandomPage)
    {
        foreach (var url in new[] { entity?.Url, wikiPage?.Url, fandomPage?.Url })
        {
            if (!string.IsNullOrEmpty(url) && run.CreatedByUrl.TryGetValue(url, out var card))
            {
                return card;
            }
        }
        return null;
    }

    /// <summary>探勘開始時讀取黑名單：所有 Status 是 Rejected 的卡；讀取失敗時記 Warning、當作沒有黑名單。</summary>
    private void LoadRejectedCards(RunState run)
    {
        try
        {
            run.RejectedCards = glossaryStore.GetAllGlossaryCards()
                .Where(card => card.Status == "Rejected")
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "資料探勘：讀取已拒絕的卡失敗，這次探勘不套用黑名單");
            run.RejectedCards = [];
        }
    }

    /// <summary>黑名單比對：surface 與去掉結尾稱謂後的名稱，任一個與被拒絕卡的卡名或任一別名行相同時回傳那張卡。</summary>
    private static GlossaryCard? FindRejectedBySurface(IReadOnlyList<GlossaryCard> rejectedCards, string surface)
    {
        var stripped = GlossaryNameNormalizer.StripHonorific(surface);
        return rejectedCards.FirstOrDefault(card =>
            NameMatchesRejectedCard(card, surface) ||
            (stripped != surface && NameMatchesRejectedCard(card, stripped)));
    }

    /// <summary>黑名單比對：name 與被拒絕卡的卡名或任一別名行相同時回傳那張卡。</summary>
    private static GlossaryCard? FindRejectedByName(IReadOnlyList<GlossaryCard> rejectedCards, string name) =>
        rejectedCards.FirstOrDefault(card => NameMatchesRejectedCard(card, name));

    /// <summary>名稱與被拒絕卡的卡名或任一別名行（每行一個）經 SameName 相同。</summary>
    private static bool NameMatchesRejectedCard(GlossaryCard card, string name) =>
        GlossaryNameNormalizer.SameName(card.Name, name) ||
        (card.Aliases ?? string.Empty).Split('\n').Any(alias => GlossaryNameNormalizer.SameName(alias, name));

    /// <summary>第一張卡名與 name 相同（SameName）的卡。</summary>
    private static GlossaryCard? FindCardByName(IEnumerable<GlossaryCard> cards, string name) =>
        cards.FirstOrDefault(card => GlossaryNameNormalizer.SameName(card.Name, name));

    /// <summary>Wikidata：第一筆 Label 與 surface 經 SameName 相同，或第一筆是唯一結果時，取這筆的 entity。</summary>
    private async Task<WikidataEntity?> LookupWikidataAsync(string surface, CancellationToken ct)
    {
        var hits = await knowledge.SearchWikidataAsync(surface, "ja", 3, ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return null;
        }

        var first = hits[0];
        return hits.Count == 1 || GlossaryNameNormalizer.SameName(first.Label, surface)
            ? await knowledge.GetWikidataEntityAsync(first.Id, ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>Wikipedia（沒有 entity 時）：以 surface 搜尋，取第一筆的頁面。</summary>
    private async Task<WikiPage?> LookupWikipediaBySearchAsync(string surface, CancellationToken ct)
    {
        var hits = await knowledge.SearchWikipediaAsync("ja", surface, 1, ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return null;
        }
        return await knowledge.GetWikipediaPageAsync("ja", hits[0], ct).ConfigureAwait(false);
    }

    /// <summary>Fandom（只有 person 才查）：virtualyoutuber 站內搜尋，取第一筆的頁面。</summary>
    private async Task<WikiPage?> LookupFandomAsync(string surface, CancellationToken ct)
    {
        var hits = await knowledge.SearchFandomAsync("virtualyoutuber", surface, 1, ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return null;
        }
        return await knowledge.GetFandomPageAsync("virtualyoutuber", hits[0], ct).ConfigureAwait(false);
    }

    /// <summary>把 surface 當作一個新名詞加進既有卡（第 1 關與第 3 關 existing 共用）：Source 是 surface、
    /// Target 沿用「卡名同名名詞」的 Target（找不到用卡名）、Category 沿用該名詞的分類，Status 為 Pending。
    /// 本次剛建立的卡不在型錄裡，其「卡名同名名詞」改由 createdCatalog 查。</summary>
    private MiningItem AddAlias(GlossaryCard card, string surface, GlossaryCatalog? createdCatalog = null)
    {
        var nameTerm = glossaryState.Catalog.TermsOf(card.Id).FirstOrDefault(term =>
            GlossaryNameNormalizer.SameName(term.Source, card.Name))
            ?? createdCatalog?.TermsOf(card.Id).FirstOrDefault(term =>
                GlossaryNameNormalizer.SameName(term.Source, card.Name));
        var term = new GlossaryTerm
        {
            CardId = card.Id,
            Source = surface,
            Target = string.IsNullOrWhiteSpace(nameTerm?.Target) ? card.Name : nameTerm.Target,
            Category = nameTerm?.Category,
            Note = "資料探勘：別名",
            Status = "Pending",
        };
        glossaryStore.SaveGlossaryTermEdits([], [term]);
        logger.LogInformation("資料探勘：{Surface} 併入既有卡「{CardName}」成為別名", surface, card.Name);
        return new MiningItem(surface, MiningOutcome.AddedAlias, card.Id, card.Name, null);
    }

    /// <summary>依 GLM 的 new 決策建立詞彙卡：名詞數上限（Character 5／Group 3／Game 1）、summary 100 字、
    /// 別名正規化後存 Aliases（最多 5 行）、parentGroup 對到既有 Group 卡時填 ParentId（角色卡對不到時改用
    /// 第 2 關記下的 memberOfGroup），疑似重複時在 Description 標註。</summary>
    private MiningItem CreateNewCard(ComposeResponse decision, MinedNoun noun, IReadOnlyList<GlossaryCard> similarCards,
        GlossaryCard? memberOfGroup, WikidataEntity? entity, WikiPage? wikiPage, WikiPage? fandomPage, string origin, long? streamId, string context,
        out GlossaryCard? newCard, out List<GlossaryTerm> newTerms)
    {
        newCard = null;
        newTerms = [];
        var surface = noun.Surface;
        // 種類不分大小寫（GLM 可能回 game／character／group）
        var kind = decision.Kind?.Trim() switch
        {
            { } k when k.Equals(GlossaryKind.Character, StringComparison.OrdinalIgnoreCase) => GlossaryKind.Character,
            { } k when k.Equals(GlossaryKind.Group, StringComparison.OrdinalIgnoreCase) => GlossaryKind.Group,
            { } k when k.Equals(GlossaryKind.Game, StringComparison.OrdinalIgnoreCase) => GlossaryKind.Game,
            var other => other,
        };
        var name = decision.Name?.Trim() ?? string.Empty;
        var summary = Truncate(decision.Summary?.Trim(), SummaryMaxLength);
        var aliases = GlossaryCatalog.NormalizeCardAliases(
            string.Join("\n", (decision.Aliases ?? []).Take(MaxCardAliases)), name);
        var terms = (decision.Terms ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Source) && !string.IsNullOrWhiteSpace(t.Target))
            .Select(t => new GlossaryTerm
            {
                Source = t.Source!.Trim(),
                Target = t.Target!.Trim(),
                Category = string.IsNullOrWhiteSpace(t.Category) ? null : t.Category.Trim(),
                Note = "資料探勘",
                Status = "Pending",
            })
            .Take(kind == GlossaryKind.Game ? 1 : kind == GlossaryKind.Group ? 3 : 5)
            .ToList();

        // 種類不合法、名稱或名詞缺空時視為解析失敗：存候選、記 Failed
        if ((kind != GlossaryKind.Character && kind != GlossaryKind.Group && kind != GlossaryKind.Game) ||
            name.Length == 0 || terms.Count == 0)
        {
            StoreCandidate(noun, origin, streamId, context);
            logger.LogWarning("資料探勘：{Surface} 的建卡決策內容不完整（kind={Kind}），存進名詞候選", surface, kind);
            return new MiningItem(surface, MiningOutcome.Failed, null, null, "AI 回覆無法解析");
        }

        long? parentId = null;
        if (!string.IsNullOrWhiteSpace(decision.ParentGroup))
        {
            parentId = glossaryState.Catalog.Cards
                .Where(card => card.Kind == GlossaryKind.Group && GlossaryNameNormalizer.SameName(card.Name, decision.ParentGroup))
                .Min(card => (long?)card.Id);
        }

        // 角色卡的 parentGroup 對不到任何既有團體卡時，用第 2 關記下的團體卡當上層（人名只出現在團體卡名詞裡的情況）
        if (parentId is null && kind == GlossaryKind.Character && memberOfGroup is not null)
        {
            parentId = memberOfGroup.Id;
        }

        var sourceUrl = entity?.Url ?? wikiPage?.Url ?? fandomPage?.Url;
        var description = kind == GlossaryKind.Game
            ? summary
            : AppendSourceLine(summary, wikiPage?.Url ?? fandomPage?.Url);

        // 第 4 關（只標註）：similarCards 不是空的而且還是建了新卡時，說明欄標註疑似重複
        if (similarCards.Count > 0)
        {
            description = $"疑似重複：{name} ↔ {string.Join("、", similarCards.Select(card => card.Name))}\n{description}";
        }

        var card = new GlossaryCard
        {
            Kind = kind!,
            Name = name,
            ParentId = parentId,
            Aliases = aliases,
            Description = description,
            SourceUrl = sourceUrl,
            Status = "Pending",
        };
        var newId = glossaryStore.CreateGlossaryCard(card, terms);
        card.Id = newId;
        newCard = card;
        newTerms = terms;
        var aliasNote = aliases is null ? string.Empty : $"，別名：{aliases.Replace("\n", "、")}";
        logger.LogInformation("資料探勘：為 {Surface} 建立新卡「{Name}」（{Kind}，Id {Id}，{TermCount} 個名詞{AliasNote}）",
            surface, name, kind, newId, terms.Count, aliasNote);
        return new MiningItem(surface, MiningOutcome.Created, newId, name, null);
    }

    /// <summary>非遊戲卡的說明：summary 加換行，再加上來源網址行；沒有網址時只有 summary。</summary>
    private static string AppendSourceLine(string summary, string? url) =>
        string.IsNullOrWhiteSpace(url) ? summary : $"{summary}\n來源：{url}";

    private void StoreCandidate(MinedNoun noun, string origin, long? streamId, string context) =>
        glossaryStore.UpsertTermCandidates([new TermCandidate(noun.Surface, noun.Kind, noun.Importance, origin, streamId, context)]);

    private static string Truncate(string? text, int maxLength) =>
        string.IsNullOrEmpty(text) || text.Length <= maxLength ? text ?? string.Empty : text[..maxLength];

    /// <summary>從模型輸出中切出 JSON 物件本體（容忍 markdown 圍欄與前後文字）。</summary>
    private static string? ExtractJsonObject(string content)
    {
        var text = content.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static NounsResponse? ParseNouns(string content)
    {
        var json = ExtractJsonObject(content);
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<NounsResponse>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ComposeResponse? ParseCompose(string content)
    {
        var json = ExtractJsonObject(content);
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ComposeResponse>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>單次採掘的累計狀態（token 用量與本次建立的卡），讓逐名詞的處理可以累加。</summary>
    private sealed class RunState
    {
        private readonly List<GlossaryCard> _createdCards = [];
        private readonly List<GlossaryTerm> _createdTerms = [];

        public int TotalTokens { get; set; }

        /// <summary>這次探勘的黑名單（Status = Rejected 的卡）：比對到就不查 Wiki、不建卡、不存候選。</summary>
        public IReadOnlyList<GlossaryCard> RejectedCards { get; set; } = [];

        /// <summary>本次探勘建立的卡（網址 → 卡）：Wikidata／Wikipedia／Fandom 的相關網址全部都記，
        /// 讓同一次探勘中指向同一個來源的名詞併入而不重複建卡。</summary>
        public Dictionary<string, GlossaryCard> CreatedByUrl { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本次建立的卡＋名詞構成的型錄：第 1～3 關比對用；
        /// 每建一張卡就用「本次建立的卡＋它們的名詞（CardId 設好）」重建（數量很少，成本可忽略）。</summary>
        public GlossaryCatalog CreatedCatalog { get; private set; } = new([], []);

        /// <summary>記下本次建立的新卡：名詞補上 CardId、所有相關網址記進 CreatedByUrl、重建 CreatedCatalog。</summary>
        public void AddCreatedCard(GlossaryCard card, IReadOnlyList<GlossaryTerm> terms,
            WikidataEntity? entity, WikiPage? wikiPage, WikiPage? fandomPage)
        {
            _createdCards.Add(card);
            foreach (var term in terms)
            {
                term.CardId = card.Id;
                _createdTerms.Add(term);
            }

            foreach (var url in new[] { entity?.Url, wikiPage?.Url, fandomPage?.Url })
            {
                if (!string.IsNullOrEmpty(url))
                {
                    CreatedByUrl[url] = card;
                }
            }

            CreatedCatalog = new GlossaryCatalog(_createdCards, _createdTerms);
        }
    }

    private sealed class NounsResponse
    {
        public List<NounDto>? Nouns { get; set; }
    }

    private sealed class NounDto
    {
        public string? Surface { get; set; }
        public string? Kind { get; set; }
        public int? Importance { get; set; }
    }

    private sealed class ComposeResponse
    {
        public string? Decision { get; set; }
        public long? CardId { get; set; }
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? Summary { get; set; }
        public List<string>? Aliases { get; set; }
        public string? ParentGroup { get; set; }
        public List<TermDto>? Terms { get; set; }
    }

    private sealed class TermDto
    {
        public string? Source { get; set; }
        public string? Target { get; set; }
        public string? Category { get; set; }
    }
}
