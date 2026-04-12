using TranslationGateway.Interface;

namespace TranslationGateway.Services;

public class TextProcessingService : ITextProcessingService
{
    public string Process(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        // 這裡目前先放簡易邏輯，之後可以直接在此集成 OpenCC 庫
        var result = input.Replace("帮忙", "幫忙");

        // 也可以加入其他通用的過濾邏輯
        result = result.Trim();

        return result;
    }
}