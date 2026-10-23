using System.IO;
using System.Runtime.CompilerServices;
using OpenCCNET;

namespace Translation.Gateway.Wpf.Tests;

internal static class TestSetup
{
    /// <summary>與 Gateway 的 App.OnStartup 相同的 OpenCC 初始化；資源目錄用絕對路徑，避免依賴工作目錄。</summary>
    [ModuleInitializer]
    internal static void InitializeOpenCc() => ZhConverter.Initialize(
        dictionaryDirectory: Path.Combine(AppContext.BaseDirectory, "Dictionary"),
        jiebaResourceDirectory: Path.Combine(AppContext.BaseDirectory, "JiebaResource"),
        segmentMode: SegmentMode.Jieba);
}
