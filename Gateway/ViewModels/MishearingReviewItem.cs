using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Translation.Bridge.Core.Models;

namespace Translation.Gateway.ViewModels;

/// <summary>聽錯字修正審核頁的一列：包住一個 <see cref="MishearingFix"/>，加上對出來的卡名與名詞資訊。</summary>
public partial class MishearingReviewItem : ObservableObject
{
    public MishearingReviewItem(MishearingFix fix, string cardName, string termSource, string termTarget)
    {
        Fix = fix;
        CardName = cardName;
        TermSource = termSource;
        TermTarget = termTarget;
    }

    /// <summary>勾選用（批次核准／駁回）；載入時預設全選。</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>包住的修正資料（唯讀）。</summary>
    public MishearingFix Fix { get; }

    /// <summary>所屬詞彙卡名稱（卡已刪除時顯示替代文字）。</summary>
    public string CardName { get; }

    /// <summary>對應名詞的原文（Source）。</summary>
    public string TermSource { get; }

    /// <summary>對應名詞的譯名（Target）。</summary>
    public string TermTarget { get; }

    public string Wrong => Fix.Wrong;

    public string Right => Fix.Right;

    public string? Evidence => Fix.Evidence;

    public int Occurrences => Fix.Occurrences;

    /// <summary>待審核／已核准。</summary>
    public string StatusText => Fix.Status == MishearingStatus.Approved ? "已核准" : "待審核";

    /// <summary>UpdatedText：UpdatedAt，沒有就用 CreatedAt，格式 MM/dd HH:mm。</summary>
    public string UpdatedText => (Fix.UpdatedAt ?? Fix.CreatedAt).ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
}
