namespace Translation.Bridge.Core.Interface;

public interface ITextProcessingService
{
    /// <summary>
    /// 執行文本後處理（如繁簡轉換、標點修復等）
    /// </summary>
    string Process(string input);
}