using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;
using System.Text.RegularExpressions;
using TranslationGateway.Interface;

namespace TranslationGateway.Services;

public class TextProcessingService : ITextProcessingService
{
    public string Process(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        var result = input.Trim();

        if (Regex.IsMatch(input, @"[\u4e00-\u9fa5]"))
        {
            result = ChineseConverter.Convert(input, ChineseConversionDirection.SimplifiedToTraditional);
        }

        result = result.Trim();

        return result;
    }
}