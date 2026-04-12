using CommunityToolkit.Mvvm.ComponentModel;

namespace TranslationGateway.ViewModels;

public partial class SettingsViewModel
{
    [ObservableProperty]
    private string apiHost = "";

    [ObservableProperty]
    private string model = "";

    [ObservableProperty]
    private string chatInput = "";

    [ObservableProperty]
    private string systemInput = "";

    [ObservableProperty]
    private string chatOutput = "";

    [ObservableProperty]
    private int? promptTokens;

    [ObservableProperty]
    private int? completionTokens;

    [ObservableProperty]
    private int? totalTokens;

    [ObservableProperty]
    private string subtitleInputPath = "";

    [ObservableProperty]
    private string subtitleSummary = "";

    [ObservableProperty]
    private bool isSubtitleRunning;

    [ObservableProperty]
    private bool isLocalModel;

    [ObservableProperty]
    private string lastSelectedControlName = "";

    // --- 新增 Translation 相關綁定 ---
    [ObservableProperty]
    private int maxParallelism;
    [ObservableProperty]
    private int batchSize;
    [ObservableProperty]
    private double temperature;

    // --- 新增 Whisper 相關綁定 ---
    [ObservableProperty]
    private string? whisperExePath;

    // 新增 Token 限制相關綁定
    [ObservableProperty]
    private int contextWindow;
    [ObservableProperty]
    private int maxOutput;

    [ObservableProperty]
    private string systemPromptTemplate = "";

    [ObservableProperty]
    private bool isThroughPass;
}