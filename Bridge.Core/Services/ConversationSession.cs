using System.Threading;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 多輪對話歷史（每句字幕一輪 user/assistant）。所有公開成員以 lock 保護，可多執行緒使用。
/// system prompt 改變或閒置過久會清空歷史；輪數達上限時只保留最近幾輪。
/// </summary>
public sealed class ConversationSession
{
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private readonly List<ChatTurn> _turns = [];
    private string? _lastSystemPrompt;
    private DateTimeOffset _lastAppendTime;

    public ConversationSession(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>對話歷史達到此輪數時重置。</summary>
    public int MaxTurns { get { lock (_lock) return _maxTurns; } set { lock (_lock) _maxTurns = value; } }

    /// <summary>重置時保留的最近輪數。</summary>
    public int KeepTurns { get { lock (_lock) return _keepTurns; } set { lock (_lock) _keepTurns = value; } }

    /// <summary>超過此時間沒有新的對話就清空歷史（換影片、暫停）。</summary>
    public TimeSpan IdleReset { get { lock (_lock) return _idleReset; } set { lock (_lock) _idleReset = value; } }

    private int _maxTurns = 12;
    private int _keepTurns = 4;
    private TimeSpan _idleReset = TimeSpan.FromSeconds(90);

    /// <summary>目前歷史輪數。</summary>
    public int Count { get { lock (_lock) return _turns.Count; } }

    /// <summary>
    /// 依序套用重置規則（system prompt 改變 → 閒置逾時 → 達輪數上限修剪），回傳歷史複本。
    /// </summary>
    public IReadOnlyList<ChatTurn> GetHistory(string systemPrompt)
    {
        lock (_lock)
        {
            // a. system prompt 與上次不同 → 清空（第一次呼叫只記錄，不算不同）
            if (_lastSystemPrompt is not null &&
                !string.Equals(_lastSystemPrompt, systemPrompt, StringComparison.Ordinal))
            {
                _turns.Clear();
            }
            _lastSystemPrompt = systemPrompt;

            // b. 有歷史且距最後一次 Append 超過 IdleReset → 清空
            if (_turns.Count > 0 && _timeProvider.GetUtcNow() - _lastAppendTime > _idleReset)
            {
                _turns.Clear();
            }

            // c. 達 MaxTurns → 只保留最後 KeepTurns 輪（KeepTurns 夾在 0～MaxTurns）
            if (_turns.Count >= _maxTurns)
            {
                var keep = Math.Clamp(_keepTurns, 0, _maxTurns);
                if (_turns.Count > keep)
                {
                    _turns.RemoveRange(0, _turns.Count - keep);
                }
            }

            return _turns.ToList();
        }
    }

    /// <summary>加入一輪對話並記錄時間（取自 TimeProvider.GetUtcNow()）。</summary>
    public void Append(string user, string assistant)
    {
        lock (_lock)
        {
            _turns.Add(new ChatTurn(user, assistant));
            _lastAppendTime = _timeProvider.GetUtcNow();
        }
    }

    /// <summary>清空歷史（保留記錄的 system prompt）。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _turns.Clear();
        }
    }

    /// <summary>移除所有 User 與指定值相同（Ordinal）的輪次；回傳是否有移除。不更新最後對話時間。</summary>
    public bool Remove(string user)
    {
        lock (_lock)
        {
            return _turns.RemoveAll(turn => string.Equals(turn.User, user, StringComparison.Ordinal)) > 0;
        }
    }

    /// <summary>把所有 User 與指定值相同的輪次替換為新的一輪（位置不變）；回傳是否有替換。不更新最後對話時間。</summary>
    public bool Replace(string user, string newUser, string newAssistant)
    {
        lock (_lock)
        {
            var replaced = false;
            for (var i = 0; i < _turns.Count; i++)
            {
                if (!string.Equals(_turns[i].User, user, StringComparison.Ordinal)) continue;
                _turns[i] = new ChatTurn(newUser, newAssistant);
                replaced = true;
            }
            return replaced;
        }
    }
}
