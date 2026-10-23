using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Translation.Bridge.Core.Services;

namespace Translation.Gateway.Converters;

/// <summary>模型名稱是 Z.AI GLM 時回傳 Visible，否則 Collapsed；ConverterParameter="Invert" 時反過來。
/// 供知識庫模型設定頁切換「思考強度」（GLM）與「思考上限（tokens）」（其他模型）的顯示。</summary>
public sealed class GlmModelVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isGlm = LorebookModelKind.IsGlm(value as string);
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        return isGlm != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
