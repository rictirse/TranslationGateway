using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>假的 PotPlayer 登錄檔設定：記錄 ApplyTranslation／ApplyWhisper 收到的內容，可設定擲例外。</summary>
internal sealed class FakePotPlayerRegistry : IPotPlayerRegistry
{
    public List<string> Applied { get; } = [];

    public List<(bool Enabled, int? LanguageIndex)> WhisperApplied { get; } = [];

    /// <summary>非 null 時把每次呼叫（"translation"／"whisper"）記進共享清單，用來驗證呼叫順序。</summary>
    public List<string>? CallLog { get; set; }

    public bool ThrowOnApply { get; set; }

    public bool ThrowOnApplyWhisper { get; set; }

    public void ApplyTranslation(string scriptFileName)
    {
        if (ThrowOnApply)
        {
            throw new InvalidOperationException("registry error");
        }

        Applied.Add(scriptFileName);
        CallLog?.Add("translation");
    }

    public void ApplyWhisper(bool enabled, int? languageIndex)
    {
        if (ThrowOnApplyWhisper)
        {
            throw new InvalidOperationException("registry error");
        }

        WhisperApplied.Add((enabled, languageIndex));
        CallLog?.Add("whisper");
    }
}

public class PotPlayerPreparerTests
{
    /// <summary>暫存資料夾、假 registry、假 settings 的測試內容；測試結束刪除整個暫存資料夾。</summary>
    private sealed class Context : IDisposable
    {
        public required string Dir { get; init; }

        public required string InstallDir { get; init; }

        public required string CopyRoot { get; init; }

        public required string PlayerPath { get; init; }

        public required FakeSettingsService Settings { get; init; }

        public required GatewayEndpoint Endpoint { get; init; }

        public required FakePotPlayerRegistry Registry { get; init; }

        /// <summary>有傳時建立 Model／Engine 目錄連結用的 %APPDATA% 替身。</summary>
        public string? UserDataRoot { get; init; }

        public string DataRoot => Path.Combine(UserDataRoot!, "PotPlayerMini64");

        public string CopyModelPath => Path.Combine(CopyRoot, "Model");

        public string CopyEnginePath => Path.Combine(CopyRoot, "Engine");

        public string DataModelPath => Path.Combine(DataRoot, "Model");

        public string DataEnginePath => Path.Combine(DataRoot, "Engine");

        public string InstallScriptPath => Path.Combine(InstallDir, "Extension", "Subtitle", "Translate", TranslateScriptBuilder.ScriptFileName);

        public string CopyScriptPath => Path.Combine(CopyRoot, "Extension", "Subtitle", "Translate", TranslateScriptBuilder.ScriptFileName);

        public string InstallParseScriptPath => Path.Combine(InstallDir, "Extension", "Media", "PlayParse", ChaptersHookBuilder.TargetScriptFileName);

        public string CopyParseScriptPath => Path.Combine(CopyRoot, "Extension", "Media", "PlayParse", ChaptersHookBuilder.TargetScriptFileName);

        public string CopyPlayerPath => Path.Combine(CopyRoot, Path.GetFileName(PlayerPath));

        public string MarkerPath => Path.Combine(CopyRoot, ".gateway-source.txt");

        public PotPlayerPreparer CreatePreparer() => new(
            Settings, Endpoint, Registry, TranslateScriptBuilder.LoadTemplate, CopyRoot, NullLogger<PotPlayerPreparer>.Instance);

        /// <summary>加上 userDataRoot 與真的 DirectoryJunction（Model／Engine 目錄連結測試用）。</summary>
        public PotPlayerPreparer CreatePreparerWithLinks() => new(
            Settings, Endpoint, Registry, TranslateScriptBuilder.LoadTemplate, CopyRoot, NullLogger<PotPlayerPreparer>.Instance,
            UserDataRoot, new DirectoryJunction());

        /// <summary>有載入章節掛勾模板的 preparer（章節掛勾測試用）。</summary>
        public PotPlayerPreparer CreatePreparerWithHook() => new(
            Settings, Endpoint, Registry, TranslateScriptBuilder.LoadTemplate, CopyRoot, NullLogger<PotPlayerPreparer>.Instance,
            loadChaptersHook: ChaptersHookBuilder.LoadTemplate);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch (IOException)
            {
                // 暫存目錄留給系統清
            }
        }
    }

    /// <summary>建立暫存資料夾與安裝目錄裡的 PotPlayerMini64.exe（內容任意）。</summary>
    private static Context Create(string playerFileName = "PotPlayerMini64.exe", bool withUserData = false)
    {
        var dir = Directory.CreateTempSubdirectory("preparer-");
        var installDir = Path.Combine(dir.FullName, "install");
        Directory.CreateDirectory(installDir);
        var playerPath = Path.Combine(installDir, playerFileName);
        File.WriteAllText(playerPath, "fake exe");

        var settings = new FakeSettingsService();
        settings.Current.Streamlink.PlayerPath = playerPath;
        return new Context
        {
            Dir = dir.FullName,
            InstallDir = installDir,
            CopyRoot = Path.Combine(dir.FullName, "copy"),
            PlayerPath = playerPath,
            Settings = settings,
            Endpoint = new GatewayEndpoint { Port = 5003 },
            Registry = new FakePotPlayerRegistry(),
            UserDataRoot = withUserData ? Path.Combine(dir.FullName, "userData") : null,
        };
    }

    /// <summary>把安裝目錄的腳本路徑建成資料夾，讓寫入擲 IOException／UnauthorizedAccessException（跨平台，不靠檔案權限）。</summary>
    private static void BlockInstallScriptPath(Context ctx) => Directory.CreateDirectory(ctx.InstallScriptPath);

    /// <summary>長得像 PotPlayer 內建 YouTube 解析腳本的樣本（CRLF、含日文與 PlayitemParse 簽章）。</summary>
    private static string YouTubeParseScript() => string.Join("\r\n",
    [
        "// PotPlayer YouTube MediaPlayParse",
        "string PlayitemParse(const string &in path, dictionary &MetaData, array<dictionary> &QualityList)",
        "{",
        "    HostPrintUTF8(\"日本語のテスト\");",
        "    return 0;",
        "}",
        ""
    ]);

    /// <summary>寫入 YouTube 解析腳本（可加 UTF-8 BOM）。</summary>
    private static void WriteParseScript(string path, string content, bool withBom)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(withBom));
    }

    // ===== 章節掛勾（loadChaptersHook）=====

    [Fact]
    public void Prepare_WithHook_PatchesYouTubeParseScript_KeepsBom()
    {
        using var ctx = Create();
        WriteParseScript(ctx.InstallParseScriptPath, YouTubeParseScript(), withBom: true);
        var preparer = ctx.CreatePreparerWithHook();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.Equal(ctx.PlayerPath, result.PlayerPath);

        // 原檔的 UTF-8 BOM 保留，內容改名並接上掛勾（URL 是實際 port）
        var bytes = File.ReadAllBytes(ctx.InstallParseScriptPath);
        Assert.Equal(0xEF, bytes[0]);
        var content = new UTF8Encoding(false).GetString(bytes[3..]);
        Assert.Contains("GatewayOriginal_PlayitemParse", content);
        Assert.Contains("const string FixedGatewayChaptersUrl = \"http://127.0.0.1:5003/chapters\";", content);
        Assert.Contains("日本語のテスト", content);

        // 內容相同不重寫：第二次 Prepare 不改時間戳
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(ctx.InstallParseScriptPath, oldTime);
        Assert.True(preparer.Prepare().Success);
        Assert.Equal(oldTime, File.GetLastWriteTime(ctx.InstallParseScriptPath));
    }

    [Fact]
    public void Prepare_WithHook_ScriptMissing_StillSucceeds()
    {
        using var ctx = Create();
        var preparer = ctx.CreatePreparerWithHook();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.Equal(ctx.PlayerPath, result.PlayerPath);
        // 找不到解析腳本只記 log，不建檔也不建資料夾
        Assert.False(File.Exists(ctx.InstallParseScriptPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(ctx.InstallParseScriptPath)));
    }

    [Fact]
    public void Prepare_WithHook_BadScript_LogsAndSucceeds()
    {
        using var ctx = Create();
        const string badScript = "// 沒有 PlayitemParse 簽章的腳本\r\nint Other() { return 0; }\r\n";
        WriteParseScript(ctx.InstallParseScriptPath, badScript, withBom: true);
        var logger = new SpyLogger<PotPlayerPreparer>();
        var preparer = new PotPlayerPreparer(
            ctx.Settings, ctx.Endpoint, ctx.Registry, TranslateScriptBuilder.LoadTemplate, ctx.CopyRoot, logger,
            loadChaptersHook: ChaptersHookBuilder.LoadTemplate);

        var result = preparer.Prepare();

        // 掛勾失敗只記 Warning，Prepare 仍成功、回傳值不變
        Assert.True(result.Success);
        Assert.Equal(ctx.PlayerPath, result.PlayerPath);
        // 原檔不變（BOM 也還在）
        Assert.Equal(badScript, File.ReadAllText(ctx.InstallParseScriptPath));
        Assert.Equal(0xEF, File.ReadAllBytes(ctx.InstallParseScriptPath)[0]);
        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void Prepare_WithoutHook_DoesNotTouchParseScript()
    {
        using var ctx = Create();
        WriteParseScript(ctx.InstallParseScriptPath, YouTubeParseScript(), withBom: false);
        var preparer = ctx.CreatePreparer();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.Equal(YouTubeParseScript(), File.ReadAllText(ctx.InstallParseScriptPath));
    }

    [Fact]
    public void Prepare_CopyMode_PatchesCopyNotInstall()
    {
        using var ctx = Create();
        WriteParseScript(ctx.InstallParseScriptPath, YouTubeParseScript(), withBom: false);
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparerWithHook();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.Equal(ctx.CopyPlayerPath, result.PlayerPath);

        // 掛勾寫在複本裡的那份
        Assert.True(File.Exists(ctx.CopyParseScriptPath));
        var copyContent = File.ReadAllText(ctx.CopyParseScriptPath);
        Assert.Contains("GatewayOriginal_PlayitemParse", copyContent);
        Assert.Contains("const string FixedGatewayChaptersUrl = \"http://127.0.0.1:5003/chapters\";", copyContent);

        // 原安裝目錄的不動
        Assert.Equal(YouTubeParseScript(), File.ReadAllText(ctx.InstallParseScriptPath));
    }

    [Fact]
    public void Prepare_HookAccessDeniedInInstall_FallsBackToCopy()
    {
        // 翻譯腳本寫得進安裝目錄，但 YouTube 解析腳本權限被拒（使用者筆電：Program Files 的 PlayParse 不能寫）→ 改用複本
        using var ctx = Create();
        WriteParseScript(ctx.InstallParseScriptPath, YouTubeParseScript(), withBom: false);
        File.SetAttributes(ctx.InstallParseScriptPath, FileAttributes.ReadOnly);
        try
        {
            // Linux 以 root 執行時唯讀屬性擋不住寫入，這個情境模擬不出來：跳過（Windows 驗證層會跑到）
            try
            {
                using (new FileStream(ctx.InstallParseScriptPath, FileMode.Open, FileAccess.Write)) { }
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            var result = ctx.CreatePreparerWithHook().Prepare();

            Assert.True(result.Success);
            Assert.Equal(ctx.CopyPlayerPath, result.PlayerPath);
            Assert.Contains("GatewayOriginal_PlayitemParse", File.ReadAllText(ctx.CopyParseScriptPath));
            Assert.Equal(YouTubeParseScript(), File.ReadAllText(ctx.InstallParseScriptPath));
        }
        finally
        {
            File.SetAttributes(ctx.InstallParseScriptPath, FileAttributes.Normal);
            if (File.Exists(ctx.CopyParseScriptPath))
            {
                File.SetAttributes(ctx.CopyParseScriptPath, FileAttributes.Normal);
            }
        }
    }

    /// <summary>記下所有 log 訊息的假 logger（章節掛勾的 Warning 測試用）。</summary>
    private sealed class SpyLogger<T> : ILogger<T>
    {
        private readonly object _lock = new();

        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_lock)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public void Prepare_WritableInstallDir_WritesScriptAndReturnsSource()
    {
        using var ctx = Create();
        var preparer = ctx.CreatePreparer();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.Equal(ctx.PlayerPath, result.PlayerPath);
        Assert.Equal(string.Empty, result.Message);

        Assert.True(File.Exists(ctx.InstallScriptPath));
        var content = File.ReadAllText(ctx.InstallScriptPath);
        Assert.Contains("const string FixedGatewayUrl = \"http://127.0.0.1:5003/translate\";", content);
        Assert.Contains($"const int FixedContextCount = {TranslateScriptBuilder.ContextCount};", content);
        // UTF-8 不加 BOM
        Assert.NotEqual(0xEF, File.ReadAllBytes(ctx.InstallScriptPath)[0]);

        Assert.Equal([TranslateScriptBuilder.ScriptFileName], ctx.Registry.Applied);
        Assert.False(Directory.Exists(ctx.CopyRoot));
    }

    [Fact]
    public void Prepare_ScriptUnchanged_DoesNotRewrite()
    {
        using var ctx = Create();
        var preparer = ctx.CreatePreparer();
        Assert.True(preparer.Prepare().Success);

        // 把時間戳固定在過去：第二次 Prepare 若重寫，時間一定會變
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(ctx.InstallScriptPath, oldTime);

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.Equal(oldTime, File.GetLastWriteTime(ctx.InstallScriptPath));
    }

    [Fact]
    public void Prepare_InstallDirNotWritable_UsesCopy()
    {
        using var ctx = Create();
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparer();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.Equal(ctx.CopyPlayerPath, result.PlayerPath);
        Assert.Contains("複本", result.Message);

        // 腳本寫在複本裡（不是資料夾），內容含 port；複本裡也有 exe
        Assert.True(File.Exists(ctx.CopyScriptPath));
        Assert.Contains("http://127.0.0.1:5003/translate", File.ReadAllText(ctx.CopyScriptPath));
        Assert.True(File.Exists(ctx.CopyPlayerPath));

        // 標記檔四行（安裝目錄指紋）：來源完整路徑、檔案數、大小總和、最新的 LastWriteTimeUtc.Ticks
        Assert.True(File.Exists(ctx.MarkerPath));
        var lines = File.ReadAllLines(ctx.MarkerPath);
        Assert.Equal(4, lines.Length);
        Assert.Equal(Path.GetFullPath(ctx.PlayerPath), lines[0]);
        Assert.Equal("1", lines[1]);
        Assert.Equal(new FileInfo(ctx.PlayerPath).Length.ToString(), lines[2]);
        Assert.Equal(File.GetLastWriteTimeUtc(ctx.PlayerPath).Ticks.ToString(), lines[3]);

        Assert.Equal([TranslateScriptBuilder.ScriptFileName], ctx.Registry.Applied);
    }

    [Fact]
    public void Prepare_MarkerMatches_SkipsRecopyUntilSourceChanges()
    {
        using var ctx = Create();
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparer();
        Assert.True(preparer.Prepare().Success);

        // 標記檔相同：第二次 Prepare 不重新複製，複本裡的額外檔案還在
        var extraFile = Path.Combine(ctx.CopyRoot, "extra.txt");
        File.WriteAllText(extraFile, "keep me");
        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.True(File.Exists(extraFile));
        Assert.True(File.Exists(ctx.CopyScriptPath));

        // 來源 exe 內容變了（長度與修改時間都變）：重新複製，額外檔案不見、exe 換新內容
        File.WriteAllText(ctx.PlayerPath, "fake exe version 2 with longer content");
        var recopied = preparer.Prepare();

        Assert.True(recopied.Success);
        Assert.False(File.Exists(extraFile));
        Assert.Equal("fake exe version 2 with longer content", File.ReadAllText(ctx.CopyPlayerPath));
        Assert.True(File.Exists(ctx.CopyScriptPath));
        // 標記檔更新成新來源
        Assert.Equal(new FileInfo(ctx.PlayerPath).Length.ToString(), File.ReadAllLines(ctx.MarkerPath)[2]);
    }

    [Fact]
    public void Prepare_DllChangedWithoutExeChange_Recopies()
    {
        using var ctx = Create();
        var dllPath = Path.Combine(ctx.InstallDir, "PotPlayer64.dll");
        File.WriteAllText(dllPath, "old dll");
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparer();
        Assert.True(preparer.Prepare().Success);

        // PotPlayer 更新常只換 DLL 不換 exe：exe 不動，只改 DLL 的內容與修改時間也要重新複製
        File.WriteAllText(dllPath, "new dll content v2");
        File.SetLastWriteTime(dllPath, File.GetLastWriteTime(dllPath).AddMinutes(1));

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.Equal("new dll content v2", File.ReadAllText(Path.Combine(ctx.CopyRoot, "PotPlayer64.dll")));
    }

    [Fact]
    public void Prepare_NewFileAddedToSource_Recopies()
    {
        using var ctx = Create();
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparer();
        Assert.True(preparer.Prepare().Success);

        // 來源多了新檔案（檔案數變了）：重新複製，複本裡的額外檔案被刪、新檔案有複製進去
        var extraFile = Path.Combine(ctx.CopyRoot, "extra.txt");
        File.WriteAllText(extraFile, "keep me");
        File.WriteAllText(Path.Combine(ctx.InstallDir, "lang.xml"), "new file");

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.False(File.Exists(extraFile));
        Assert.True(File.Exists(Path.Combine(ctx.CopyRoot, "lang.xml")));
    }

    [Fact]
    public void Prepare_SourceModelDirChanged_DoesNotRecopy()
    {
        using var ctx = Create();
        var modelDir = Path.Combine(ctx.InstallDir, "Model");
        Directory.CreateDirectory(modelDir);
        var modelFile = Path.Combine(modelDir, "m.bin");
        File.WriteAllText(modelFile, "model v1");
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparer();
        Assert.True(preparer.Prepare().Success);

        // Model 是使用者資料（複本裡是目錄連結），裡面的檔案變動不觸發重新複製
        var extraFile = Path.Combine(ctx.CopyRoot, "extra.txt");
        File.WriteAllText(extraFile, "keep me");
        File.WriteAllText(modelFile, "model v2 with different length");
        File.SetLastWriteTime(modelFile, File.GetLastWriteTime(modelFile).AddMinutes(1));
        File.WriteAllText(Path.Combine(modelDir, "another.bin"), "another model");

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.True(File.Exists(extraFile));
    }

    [Fact]
    public void Prepare_NoPort_FailsWithoutWritingAnything()
    {
        using var ctx = Create();
        ctx.Endpoint.Port = 0;
        var preparer = ctx.CreatePreparer();

        var result = preparer.Prepare();

        Assert.False(result.Success);
        Assert.Null(result.PlayerPath);
        Assert.Equal("Gateway 沒有可用的 port，PotPlayer 連不到 Gateway，請看系統記錄", result.Message);
        Assert.False(File.Exists(ctx.InstallScriptPath));
        Assert.False(Directory.Exists(ctx.CopyRoot));
        Assert.Empty(ctx.Registry.Applied);
    }

    [Fact]
    public void Prepare_PlayerMissing_Fails()
    {
        using var ctx = Create();
        ctx.Settings.Current.Streamlink.PlayerPath = Path.Combine(ctx.Dir, "missing.exe");
        var preparer = ctx.CreatePreparer();

        var result = preparer.Prepare();

        Assert.False(result.Success);
        Assert.Null(result.PlayerPath);
        Assert.Contains("找不到播放器", result.Message);
        Assert.Empty(ctx.Registry.Applied);
    }

    [Fact]
    public void Prepare_RegistryThrows_StillSucceeds()
    {
        using var ctx = Create();
        ctx.Registry.ThrowOnApply = true;
        var preparer = ctx.CreatePreparer();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.Equal(ctx.PlayerPath, result.PlayerPath);
    }

    // ===== Model／Engine 目錄連結（userDataRoot + DirectoryJunction）=====

    [Fact]
    public void Prepare_InstallDirNotWritable_ModelAndEngineLinkToUserData()
    {
        using var ctx = Create(withUserData: true);
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparerWithLinks();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        var junction = new DirectoryJunction();
        Assert.Equal(Path.GetFullPath(ctx.DataModelPath), junction.GetTarget(ctx.CopyModelPath));
        Assert.Equal(Path.GetFullPath(ctx.DataEnginePath), junction.GetTarget(ctx.CopyEnginePath));
        // target 資料夾已建好，透過連結看得到
        Assert.True(Directory.Exists(ctx.DataModelPath));
        Assert.True(Directory.Exists(ctx.DataEnginePath));
    }

    [Fact]
    public void Prepare_SourceHasModelDir_DoesNotCopyItIntoTarget()
    {
        using var ctx = Create(withUserData: true);
        Directory.CreateDirectory(Path.Combine(ctx.InstallDir, "Model"));
        File.WriteAllText(Path.Combine(ctx.InstallDir, "Model", "x.bin"), "model");
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparerWithLinks();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        // 複本裡的 Model 是連結，來源的 x.bin 沒被複製到 target
        var junction = new DirectoryJunction();
        Assert.NotNull(junction.GetTarget(ctx.CopyModelPath));
        Assert.False(File.Exists(Path.Combine(ctx.DataModelPath, "x.bin")));
    }

    [Fact]
    public void Prepare_CopyModelRegularFolderWithFilesAndTargetMissing_MovesToTargetAndLinks()
    {
        using var ctx = Create(withUserData: true);
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparerWithLinks();
        Assert.True(preparer.Prepare().Success);

        // 使用者手動把連結換成一般資料夾放了模型，target 不存在
        Directory.Delete(ctx.CopyModelPath);
        Directory.Delete(ctx.DataModelPath);
        Directory.CreateDirectory(ctx.CopyModelPath);
        File.WriteAllText(Path.Combine(ctx.CopyModelPath, "m.bin"), "model");

        var result = preparer.Prepare();

        Assert.True(result.Success);
        // m.bin 搬到 target，copy\Model 變成連結
        var junction = new DirectoryJunction();
        Assert.Equal(Path.GetFullPath(ctx.DataModelPath), junction.GetTarget(ctx.CopyModelPath));
        Assert.True(File.Exists(Path.Combine(ctx.DataModelPath, "m.bin")));
    }

    [Fact]
    public void Prepare_BothSidesHaveFiles_NoRecopyNeeded_SucceedsWithoutTouchingFiles()
    {
        using var ctx = Create(withUserData: true);
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparerWithLinks();
        Assert.True(preparer.Prepare().Success);

        // 換成兩邊都有檔案的狀態：copy\Model 是一般資料夾有 m.bin、target 有 t.bin
        Directory.Delete(ctx.CopyModelPath);
        Directory.CreateDirectory(ctx.CopyModelPath);
        File.WriteAllText(Path.Combine(ctx.CopyModelPath, "m.bin"), "copy side");
        File.WriteAllText(Path.Combine(ctx.DataModelPath, "t.bin"), "target side");

        var result = preparer.Prepare();

        Assert.True(result.Success);
        // 不動任何檔案，copy\Model 仍然是一般資料夾
        var junction = new DirectoryJunction();
        Assert.Null(junction.GetTarget(ctx.CopyModelPath));
        Assert.Equal("copy side", File.ReadAllText(Path.Combine(ctx.CopyModelPath, "m.bin")));
        Assert.Equal("target side", File.ReadAllText(Path.Combine(ctx.DataModelPath, "t.bin")));
    }

    [Fact]
    public void Prepare_BothSidesHaveSameFiles_DeletesCopySideAndLinks()
    {
        using var ctx = Create(withUserData: true);
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparerWithLinks();
        Assert.True(preparer.Prepare().Success);

        // 使用者把模型「複製」進複本：兩邊都有同名同大小的 m.bin
        Directory.Delete(ctx.CopyModelPath);
        Directory.CreateDirectory(ctx.CopyModelPath);
        File.WriteAllText(Path.Combine(ctx.CopyModelPath, "m.bin"), "model");
        File.WriteAllText(Path.Combine(ctx.DataModelPath, "m.bin"), "model");

        var result = preparer.Prepare();

        Assert.True(result.Success);
        var junction = new DirectoryJunction();
        Assert.Equal(Path.GetFullPath(ctx.DataModelPath), junction.GetTarget(ctx.CopyModelPath));
        Assert.Equal("model", File.ReadAllText(Path.Combine(ctx.DataModelPath, "m.bin")));
    }

    [Fact]
    public void Prepare_BothSidesHaveFiles_RecopyNeeded_FailsWithManualCleanupMessage()
    {
        using var ctx = Create(withUserData: true);
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparerWithLinks();
        Assert.True(preparer.Prepare().Success);

        Directory.Delete(ctx.CopyModelPath);
        Directory.CreateDirectory(ctx.CopyModelPath);
        File.WriteAllText(Path.Combine(ctx.CopyModelPath, "m.bin"), "copy side");
        File.WriteAllText(Path.Combine(ctx.DataModelPath, "t.bin"), "target side");
        // 來源變了 → 需要重複製（會刪掉整個複本，不能刪到使用者的模型）
        File.WriteAllText(ctx.PlayerPath, "fake exe version 2 with longer content");

        var result = preparer.Prepare();

        Assert.False(result.Success);
        Assert.Null(result.PlayerPath);
        Assert.Contains("手動整理", result.Message);
        // 兩邊的檔案都還在
        Assert.Equal("copy side", File.ReadAllText(Path.Combine(ctx.CopyModelPath, "m.bin")));
        Assert.Equal("target side", File.ReadAllText(Path.Combine(ctx.DataModelPath, "t.bin")));
    }

    [Fact]
    public void Prepare_SourceChangedWithExistingLinks_RecopiesAndKeepsTargetFiles()
    {
        using var ctx = Create(withUserData: true);
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparerWithLinks();
        Assert.True(preparer.Prepare().Success);
        File.WriteAllText(Path.Combine(ctx.DataModelPath, "keep.bin"), "model data");

        // 來源變了 → 重複製：DeleteCopyRoot 只刪連結，target 的檔案要還在，連結重建
        File.WriteAllText(ctx.PlayerPath, "fake exe version 2 with longer content");
        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.True(File.Exists(ctx.CopyPlayerPath));
        Assert.Equal("model data", File.ReadAllText(Path.Combine(ctx.DataModelPath, "keep.bin")));
        var junction = new DirectoryJunction();
        Assert.Equal(Path.GetFullPath(ctx.DataModelPath), junction.GetTarget(ctx.CopyModelPath));
        Assert.Equal(Path.GetFullPath(ctx.DataEnginePath), junction.GetTarget(ctx.CopyEnginePath));
    }

    [Fact]
    public void Prepare_LinkPointsElsewhere_RepointsToUserDataTarget()
    {
        using var ctx = Create(withUserData: true);
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparerWithLinks();
        Assert.True(preparer.Prepare().Success);

        // 把連結換成指到別的位置（裡面的檔案證明原目標不被動到）
        var other = Path.Combine(ctx.Dir, "other-Model");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "other.bin"), "other");
        Directory.Delete(ctx.CopyModelPath);
        new DirectoryJunction().Create(ctx.CopyModelPath, other);

        var result = preparer.Prepare();

        Assert.True(result.Success);
        var junction = new DirectoryJunction();
        Assert.Equal(Path.GetFullPath(ctx.DataModelPath), junction.GetTarget(ctx.CopyModelPath));
        Assert.True(File.Exists(Path.Combine(other, "other.bin")));
    }

    [Fact]
    public void Prepare_WithoutUserDataRoot_DoesNotCreateLinks()
    {
        using var ctx = Create();
        BlockInstallScriptPath(ctx);
        var preparer = ctx.CreatePreparer();

        var result = preparer.Prepare();

        Assert.True(result.Success);
        Assert.False(Directory.Exists(ctx.CopyModelPath));
        Assert.False(Directory.Exists(ctx.CopyEnginePath));
    }

    // ===== ApplyWhisper =====

    [Fact]
    public void ApplyWhisper_PassesEnabledAndLanguageIndexToRegistry()
    {
        using var ctx = Create();
        var preparer = ctx.CreatePreparer();

        preparer.ApplyWhisper(new WhisperDecider.WhisperPlan(true, WhisperLanguages.Japanese, "直播中"));

        Assert.Equal([(true, WhisperLanguages.Japanese)], ctx.Registry.WhisperApplied);
    }

    [Fact]
    public void ApplyWhisper_RegistryThrows_DoesNotThrow()
    {
        using var ctx = Create();
        ctx.Registry.ThrowOnApplyWhisper = true;
        var preparer = ctx.CreatePreparer();

        preparer.ApplyWhisper(new WhisperDecider.WhisperPlan(false, WhisperLanguages.Japanese, "有日文字幕，改用 YouTube 字幕"));

        Assert.Empty(ctx.Registry.WhisperApplied);
    }
}
