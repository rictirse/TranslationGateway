using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 已核准聽錯字修正的狀態（DI singleton，執行緒安全）：從 store 載入 Approved + Pre 的修正，
/// 翻譯前把生效中詞彙卡的聽錯寫法換成正確寫法。許多錯字本身也是一般日文（例如「作り」），
/// 只套用生效中的卡，避免對與該卡無關的直播一律替換而改壞正常的句子。
/// </summary>
public sealed class MishearingFixState(ILogger<MishearingFixState>? logger = null)
{
    private readonly object _lock = new();
    private IReadOnlyList<Entry> _fixes = [];

    /// <summary>一筆已載入的修正：套用與 log 所需的欄位快照。</summary>
    private sealed record Entry(long CardId, long Id, string Wrong, string Right, string CardName);

    /// <summary>從 store 重新讀取：只留 Approved + Pre；卡名用 GetAllGlossaryCards() 對出（log 用）。
    /// 讀取失敗時保留舊的清單、寫 Warning log，不擲例外。</summary>
    public void Reload(IGlossaryStore store)
    {
        try
        {
            var fixes = store.GetMishearingFixes();
            var cardNames = store.GetAllGlossaryCards().ToDictionary(c => c.Id, c => c.Name);
            SetFixes(fixes, cardNames);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "重新載入聽錯字修正失敗，保留上一次載入的清單");
        }
    }

    /// <summary>直接設定（測試與 Reload 共用）：再過濾一次 Approved + Pre，
    /// Wrong 去頭尾空白後少於 2 個字的丟掉；依 Wrong 長度遞減、同長度依 Id 排序（長的先換）。</summary>
    public void SetFixes(IReadOnlyList<MishearingFix> fixes, IReadOnlyDictionary<long, string>? cardNames = null)
    {
        var entries = fixes
            .Where(fix => fix.Status == MishearingStatus.Approved && fix.Kind == MishearingKind.Pre)
            .Select(fix => new
            {
                Fix = fix,
                Wrong = fix.Wrong.Trim()
            })
            .Where(e => e.Wrong.Length >= 2)
            .Select(e => new Entry(
                e.Fix.CardId,
                e.Fix.Id,
                e.Wrong,
                e.Fix.Right.Trim(),
                cardNames is not null && cardNames.TryGetValue(e.Fix.CardId, out var name)
                    ? name
                    : $"（卡片 #{e.Fix.CardId}）"))
            .OrderByDescending(e => e.Wrong.Length)
            .ThenBy(e => e.Id)
            .ToList();
        lock (_lock)
        {
            _fixes = entries;
        }
    }

    /// <summary>目前已載入的筆數（只算 Approved + Pre）。</summary>
    public int Count
    {
        get { lock (_lock) return _fixes.Count; }
    }

    /// <summary>對 text 套用 activeCardIds 中卡片的修正，回傳替換後的文字；
    /// 沒有命中時回傳原字串（同一個實例）。比對用 Ordinal，
    /// 每筆命中的修正寫一次 Information log（一次換掉所有出現處，因此同句只記一次）；已經是正確寫法的地方不動。
    /// <paramref name="appliedFixIds"/> 非空時，把實際命中的修正 Id 加進去（回溯紀錄用；不傳時行為不變）。</summary>
    public string Apply(string text, IReadOnlyCollection<long> activeCardIds, ICollection<long>? appliedFixIds = null)
    {
        if (string.IsNullOrEmpty(text) || activeCardIds.Count == 0) return text;

        IReadOnlyList<Entry> snapshot;
        lock (_lock) snapshot = _fixes;
        if (snapshot.Count == 0) return text;

        var result = text;
        foreach (var fix in snapshot)
        {
            if (!activeCardIds.Contains(fix.CardId)) continue;
            if (!result.Contains(fix.Wrong, StringComparison.Ordinal)) continue;
            var replaced = ReplaceOutsideRight(result, fix.Wrong, fix.Right);
            if (ReferenceEquals(replaced, result)) continue;
            result = replaced;
            appliedFixIds?.Add(fix.Id);
            if (ShouldLog(fix.Id, text))
            {
                logger?.LogInformation("聽錯字修正：{Wrong} → {Right}（{CardName}）", fix.Wrong, fix.Right, fix.CardName);
            }
        }
        return result;
    }

    /// <summary>已記過 log 的（修正 Id、原句）：錄影預翻每秒都會對附近幾句重算原文，同一句每秒記一次會洗版（使用者實測一秒兩行）。
    /// 同一句同一筆修正只記第一次；超過上限就整個清掉重來（只影響 log，不影響修正本身）。</summary>
    private readonly HashSet<(long FixId, string Text)> _logged = [];

    private const int MaxLoggedEntries = 2000;

    private bool ShouldLog(long fixId, string text)
    {
        lock (_lock)
        {
            if (_logged.Count >= MaxLoggedEntries)
            {
                _logged.Clear();
            }

            return _logged.Add((fixId, text));
        }
    }

    /// <summary>把 wrong 換成 right，但已經是正確寫法的地方不動：正確寫法包含錯字時
    /// （例如錯字「クリ」、正確「ツクリ」），直接 Replace 會把「ツクリ」改成「ツツクリ」。
    /// 由左到右掃描，同一位置 wrong 與 right 都對得上時取長的那個判斷，遇到 right 整段照抄、遇到 wrong 換成 right；沒有替換時回傳原字串（同一個實例）。</summary>
    private static string ReplaceOutsideRight(string text, string wrong, string right)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var changed = false;
        var i = 0;
        while (i < text.Length)
        {
            var rightHere = right.Length > 0 && i + right.Length <= text.Length &&
                            string.CompareOrdinal(text, i, right, 0, right.Length) == 0;
            var wrongHere = i + wrong.Length <= text.Length &&
                            string.CompareOrdinal(text, i, wrong, 0, wrong.Length) == 0;
            // 兩者都對得上時取長的：錯字「ツクリー」比正確「ツクリ」長 → 是錯字；正確「ツクリ」比錯字「クリ」長 → 是正確寫法
            if (wrongHere && (!rightHere || wrong.Length > right.Length))
            {
                builder.Append(right);
                i += wrong.Length;
                changed = true;
            }
            else if (rightHere)
            {
                builder.Append(right);
                i += right.Length;
            }
            else
            {
                builder.Append(text[i]);
                i++;
            }
        }
        return changed ? builder.ToString() : text;
    }
}
