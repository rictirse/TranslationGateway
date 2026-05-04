namespace Translation.Core.Models;

public record TranslationValidationResult(bool IsValid, string Message, double Ratio);