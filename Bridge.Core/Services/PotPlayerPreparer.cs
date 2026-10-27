using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text;
using Translation.Bridge.Core.Interface;

namespace Translation.Bridge.Core.Services;

/// <summary>開台前準備 PotPlayer 的結果：失敗時 <see cref="PlayerPath"/> 為 null、<see cref="Message"/> 是給使用者看的說明。</summary>
public sealed record PotPlayerPrepareResult(bool Success, string? PlayerPath, string Message);

/// <summary>開台前準備 PotPlayer：把 port 寫死進翻譯腳本寫入安裝目錄（不能寫就複製整個 PotPlayer 到 Gateway 的資料夾用複本），
/// 並改登錄檔選好翻譯引擎與自動翻譯。複本是免安裝版的行為，Model 和 Engine 用連結共用 %APPDATA%\<exe 名稱> 底下的同名資料夾。
/// 複本要不要更新看整個安裝目錄的指紋，不能只看 exe：PotPlayer 更新常只換 DLL／腳本不換 exe，只比 exe 會讓複本停在舊版。
/// PotPlayer 只在啟動時讀設定，呼叫前必須先關閉所有 PotPlayer。</summary>
public sealed class PotPlayerPreparer(
    ISettingsService settings,
    GatewayEndpoint endpoint,
    IPotPlayerRegistry registry,
    Func<string> loadTemplate,
    string copyRoot,
    ILogger<PotPlayerPreparer> logger,
    string? userDataRoot = null,
    IDirectoryJunction? junction = null,
    Func<string>? loadChaptersHook = null)
{
    private const string MarkerFileName = ".gateway-source.txt";

    /// <summary>PotPlayer 的使用者資料夾（免安裝版會到 exe 旁邊找）：以目錄連結共用 %APPDATA%\<exe 名稱> 底下的同名資料夾。</summary>
    private static readonly string[] UserDataDirectories = ["Model", "Engine"];

    /// <summary>準備 PotPlayer：寫翻譯腳本與登錄檔設定，回傳要啟動的播放器路徑（原安裝目錄或 Gateway 的複本）。</summary>
    public PotPlayerPrepareResult Prepare()
    {
        if (endpoint.TranslateUrl is null)
        {
            return new PotPlayerPrepareResult(false, null, "Gateway 沒有可用的 port，PotPlayer 連不到 Gateway，請看系統記錄");
        }

        var source = settings.Current.Streamlink.PlayerPath;
        if (!File.Exists(source))
        {
            return new PotPlayerPrepareResult(false, null, $"找不到播放器：{source}，請到設定頁修改");
        }

        if (!string.Equals(Path.GetFileName(source), "PotPlayerMini64.exe", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("播放器不是 PotPlayerMini64.exe，Gateway 寫的登錄檔設定可能不會生效：{Path}", source);
        }

        var script = TranslateScriptBuilder.Build(loadTemplate(), endpoint.TranslateUrl, TranslateScriptBuilder.ContextCount);
        var scriptRelativePath = Path.Combine("Extension", "Subtitle", "Translate", TranslateScriptBuilder.ScriptFileName);

        string playerPath;
        string message;
        try
        {
            // 先試原本的安裝目錄
            var installScriptPath = Path.Combine(Path.GetDirectoryName(source)!, scriptRelativePath);
            if (WriteScriptIfChanged(installScriptPath, script))
            {
                logger.LogInformation("已更新 PotPlayer 翻譯腳本：{Path}", installScriptPath);
            }

            // 章節掛勾寫不進安裝目錄（權限被拒）也改用複本：翻譯腳本可能早就寫過（內容相同不重寫）或該資料夾剛好可寫，
            // 只看翻譯腳本會誤判成「安裝目錄可寫」，結果 YouTube 解析腳本接不上、沒有留言章節（使用者筆電實際發生）
            TryHookChaptersScript(Path.GetDirectoryName(source)!, rethrowAccessDenied: true);

            playerPath = source;
            message = string.Empty;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 安裝目錄不能寫（例如 Program Files）：改用 Gateway 資料夾裡的複本（與原版共用同一份登錄檔設定）
            logger.LogInformation("PotPlayer 安裝目錄不能寫入，改用 Gateway 的複本：{CopyRoot}", copyRoot);
            try
            {
                playerPath = PrepareCopy(source, script, scriptRelativePath);
                message = "（PotPlayer 安裝目錄不能寫入，這次用 Gateway 的複本開啟）";
            }
            catch (Exception copyEx)
            {
                return new PotPlayerPrepareResult(false, null, $"無法準備 PotPlayer：{copyEx.Message}");
            }
        }

        try
        {
            registry.ApplyTranslation(TranslateScriptBuilder.ScriptFileName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "寫入 PotPlayer 登錄檔設定失敗，PotPlayer 可能不會自動翻譯");
        }

        return new PotPlayerPrepareResult(true, playerPath, message);
    }

    /// <summary>依決策結果設定 PotPlayer 的 Whisper 開關與語言（寫登錄檔，PotPlayer 啟動時才讀）；失敗只記 Warning，不往外拋。</summary>
    public void ApplyWhisper(WhisperDecider.WhisperPlan plan)
    {
        try
        {
            registry.ApplyWhisper(plan.Enabled, plan.LanguageIndex);
            logger.LogInformation("Whisper：{State}，語言 {Language}（{Reason}）",
                plan.Enabled ? "開啟" : "關閉", WhisperLanguages.NameOf(plan.LanguageIndex), plan.Reason);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "寫入 PotPlayer Whisper 設定失敗，維持原本的設定");
        }
    }

    /// <summary>準備 Gateway 的 PotPlayer 複本：安裝目錄指紋（<see cref="BuildInstallFingerprint"/>）不同時整個重複製，
    /// 之後把腳本寫進複本。不能只比 exe 的修改時間與大小：PotPlayer 更新常只換 DLL／腳本等幾十個檔案、exe 不動，
    /// 只比 exe 會讓複本停在舊版，開 YouTube 錄影時常常失敗。指紋算不出來時也視為需要重複製。
    /// 複本是免安裝版的行為，Model 和 Engine 不複製，改用目錄連結共用 %APPDATA%\<exe 名稱> 底下的同名資料夾。
    /// 回傳複本裡的 exe 路徑。</summary>
    private string PrepareCopy(string source, string script, string scriptRelativePath)
    {
        var sourceFullPath = Path.GetFullPath(source);
        var markerPath = Path.Combine(copyRoot, MarkerFileName);
        var fingerprint = BuildInstallFingerprint(sourceFullPath);
        var markerMatches = fingerprint is not null
            && File.Exists(markerPath)
            && File.ReadAllText(markerPath) == fingerprint;

        if (!markerMatches)
        {
            // 指紋算不出來（BuildInstallFingerprint 已記 Warning）也視為需要重複製，這次不寫標記
            var reason = File.Exists(markerPath) ? "指紋不同" : "沒有複本";
            logger.LogInformation("PotPlayer 安裝目錄有變更（{Reason}），重新複製", reason);

            // 重複製前先把使用者放在複本裡的 Model／Engine 搬走（兩邊都有檔案時擲例外，不刪除、不複製）
            EnsureUserDataLinks(sourceFullPath, forRecopy: true);

            var stopwatch = Stopwatch.StartNew();
            DeleteCopyRoot();
            var files = CopyFiles(Path.GetDirectoryName(sourceFullPath)!, copyRoot);
            if (fingerprint is not null)
            {
                File.WriteAllText(markerPath, fingerprint, new UTF8Encoding(false));
            }

            stopwatch.Stop();
            logger.LogInformation("已複製 PotPlayer 到 {CopyRoot}（{Files} 個檔案，{Seconds:0.0} 秒）",
                copyRoot, files, stopwatch.Elapsed.TotalSeconds);
        }

        WriteScriptIfChanged(Path.Combine(copyRoot, scriptRelativePath), script);

        // 章節掛勾只改複本裡的那份 YouTube 解析腳本，原安裝目錄不動（指紋也算原安裝目錄，不受影響）
        TryHookChaptersScript(copyRoot);

        try
        {
            EnsureUserDataLinks(sourceFullPath, forRecopy: false);
        }
        catch (Exception ex)
        {
            // 連結失敗只影響 Whisper，播放本身不受影響
            logger.LogWarning(ex, "建立 PotPlayer 複本的模型連結失敗，Whisper 可能找不到模型");
        }

        return Path.Combine(copyRoot, Path.GetFileName(sourceFullPath));
    }

    /// <summary>把章節掛勾（ChaptersHook.as）接進 <paramref name="potPlayerDir"/> 底下 PotPlayer 內建的
    /// YouTube 解析腳本（Extension\Media\PlayParse）：說明欄沒有章節時向 Gateway 要從留言整理出的章節。
    /// <paramref name="loadChaptersHook"/> 為 null 或 <see cref="GatewayEndpoint.ChaptersUrl"/> 為 null 時略過；
    /// 檔案不存在只記 log；任何失敗只記 Warning，不讓 <see cref="Prepare"/> 失敗。
    /// 原檔有沒有 UTF-8 BOM，寫回時照舊（PotPlayer 的腳本含日文，BOM 不能掉）。</summary>
    /// <param name="rethrowAccessDenied">true 時權限被拒（UnauthorizedAccessException）往外拋，讓呼叫端改用複本；其他失敗照樣只記 Warning。</param>
    private void TryHookChaptersScript(string potPlayerDir, bool rethrowAccessDenied = false)
    {
        if (loadChaptersHook is null)
        {
            return;
        }

        try
        {
            var chaptersUrl = endpoint.ChaptersUrl;
            if (chaptersUrl is null)
            {
                return;
            }

            var path = Path.Combine(potPlayerDir, "Extension", "Media", "PlayParse", ChaptersHookBuilder.TargetScriptFileName);
            if (!File.Exists(path))
            {
                logger.LogInformation("找不到 PotPlayer 的 YouTube 解析腳本，略過章節掛勾：{Path}", path);
                return;
            }

            var bytes = File.ReadAllBytes(path);
            var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];
            var script = Encoding.UTF8.GetString(hasBom ? bytes[3..] : bytes);

            var hooked = ChaptersHookBuilder.Build(script, loadChaptersHook(), chaptersUrl);
            if (hooked == script)
            {
                return;
            }

            // 複本是 Gateway 自己的檔案：從安裝目錄複製過來時若帶著唯讀屬性，先拿掉才寫得進去
            if (!rethrowAccessDenied && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }

            File.WriteAllText(path, hooked, new UTF8Encoding(hasBom));
            logger.LogInformation("已把章節掛勾接進 PotPlayer 的 YouTube 解析腳本：{Path}", path);
        }
        catch (UnauthorizedAccessException) when (rethrowAccessDenied)
        {
            // 預期中的情況（Program Files 不能寫），只記一行說明、不附例外堆疊，免得看起來像錯誤
            logger.LogInformation("章節掛勾寫不進 PotPlayer 安裝目錄（權限不足），改用 Gateway 的複本：{Dir}", potPlayerDir);
            throw;
        }
        catch (Exception ex)
        {
            // IO、權限、簽章對不上等都只記 Warning：掛勾失敗頂多沒有留言章節，播放不受影響
            logger.LogWarning(ex, "章節掛勾接不進 PotPlayer 的 YouTube 解析腳本，這次沒有留言章節（播放不受影響）");
        }
    }

    /// <summary>計算 PotPlayer 安裝目錄的指紋，四行以換行分隔：exe 完整路徑、檔案數、所有檔案大小總和、
    /// 所有檔案中最新的 LastWriteTimeUtc.Ticks。範圍是 exe 所在目錄底下遞迴的所有檔案（<see cref="EnumerateInstallFiles"/>）。
    /// 任何檔案拿不到資訊時記 Warning 並回傳 null（呼叫端會視為需要重複製）。</summary>
    private string? BuildInstallFingerprint(string sourceFullPath)
    {
        try
        {
            long totalLength = 0;
            long latestTicks = 0;
            var count = 0;
            foreach (var file in EnumerateInstallFiles(Path.GetDirectoryName(sourceFullPath)!))
            {
                var info = new FileInfo(file);
                count++;
                totalLength += info.Length;
                latestTicks = Math.Max(latestTicks, info.LastWriteTimeUtc.Ticks);
            }

            return string.Join(Environment.NewLine, sourceFullPath, count, totalLength, latestTicks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "計算 PotPlayer 安裝目錄指紋失敗，這次視為需要重新複製：{Source}", sourceFullPath);
            return null;
        }
    }

    /// <summary>列出安裝目錄底下遞迴的所有檔案：排除頂層 Model／Engine（使用者資料，複本裡是目錄連結）
    /// 與任何 reparse point（目錄連結或符號連結，不跟進去，避免繞到目錄外的資料）。</summary>
    private static IEnumerable<string> EnumerateInstallFiles(string root)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Dequeue()))
            {
                var attributes = File.GetAttributes(entry);
                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    yield return entry;
                }
                else if (!attributes.HasFlag(FileAttributes.ReparsePoint) && !IsUserDataPath(Path.GetRelativePath(root, entry)))
                {
                    pending.Enqueue(entry);
                }
            }
        }
    }

    /// <summary>確保複本裡的 Model／Engine 是指向 %APPDATA%\<exe 名稱>\Model（Engine）的目錄連結：
    /// 已指對不動作；指錯就重建；一般資料夾在目標空著時搬過去再建連結；兩邊都有檔案時不動任何檔案
    /// （<paramref name="forRecopy"/> 為 true 時另擲例外，避免接下來的 DeleteCopyRoot 刪到使用者的模型）。</summary>
    private void EnsureUserDataLinks(string source, bool forRecopy)
    {
        if (userDataRoot is null || junction is null)
        {
            return;
        }

        // 還沒有複本就沒有要搬的資料；連結等複製完成後的那次呼叫再建
        if (forRecopy && !Directory.Exists(copyRoot))
        {
            return;
        }

        var dataRoot = Path.Combine(userDataRoot, Path.GetFileNameWithoutExtension(source));
        foreach (var name in UserDataDirectories)
        {
            var link = Path.Combine(copyRoot, name);
            var target = Path.Combine(dataRoot, name);

            var existingTarget = junction.GetTarget(link);
            if (existingTarget is not null)
            {
                if (IsSamePath(existingTarget, target))
                {
                    continue;
                }

                // 指到別的位置：刪掉連結本身（不遞迴），照下面的流程重建
                Directory.Delete(link);
            }
            else if (Directory.Exists(link))
            {
                // 一般資料夾（例如使用者手動把模型搬進複本）
                var linkHasFiles = Directory.EnumerateFileSystemEntries(link).Any();
                if (!linkHasFiles)
                {
                    Directory.Delete(link);
                }
                else if (!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any())
                {
                    // 目標不存在或是空的：把整個資料夾搬過去（同磁碟是搬移），再建連結
                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target);
                    }

                    Directory.CreateDirectory(dataRoot);
                    Directory.Move(link, target);
                    logger.LogInformation("已把 PotPlayer 複本裡的 {Name} 搬到 {Target}，改用連結共用", name, target);
                }
                else if (RemoveDuplicatesOfTarget(link, target))
                {
                    // 兩邊都有檔案，但複本的每個檔案在目標都有同名同大小的一份：刪掉複本這份（刪除不寫入資料），再建連結
                    Directory.Delete(link, recursive: true);
                    logger.LogInformation("PotPlayer 複本的 {Name} 和 {Target} 檔案相同，已刪掉複本這份", name, target);
                }
                else
                {
                    // 兩邊都有不同的檔案：重複的已刪掉，其餘不動，請使用者自己保留一份
                    logger.LogWarning("PotPlayer 複本的 {Name} 和 {Target} 都有檔案，不建立連結，請手動整理（保留一份）", name, target);
                    if (forRecopy)
                    {
                        throw new InvalidOperationException($"PotPlayer 複本的 {name} 和 {target} 都有檔案，請手動整理（保留一份）後再開台");
                    }

                    continue;
                }
            }

            Directory.CreateDirectory(target);
            junction.Create(link, target);
            logger.LogInformation("PotPlayer 複本的 {Name} 連到 {Target}", name, target);
        }
    }

    /// <summary>刪掉 <paramref name="dir"/> 裡在 <paramref name="target"/> 有同一相對路徑、同大小的檔案；
    /// 全部都刪掉（只剩空資料夾）時回傳 true，有任何檔案不同或目標沒有時保留那些檔案並回傳 false。</summary>
    private static bool RemoveDuplicatesOfTarget(string dir, string target)
    {
        var allRemoved = true;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
        {
            var targetFile = new FileInfo(Path.Combine(target, Path.GetRelativePath(dir, file)));
            if (targetFile.Exists && targetFile.Length == new FileInfo(file).Length)
            {
                File.Delete(file);
            }
            else
            {
                allRemoved = false;
            }
        }

        return allRemoved;
    }

    /// <summary>比較兩個路徑是否相同（完整路徑、去掉結尾分隔符號、不分大小寫）。</summary>
    private static bool IsSamePath(string a, string b)
    {
        var fullA = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullB = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>刪除整個 copyRoot；只能刪 copyRoot 底下的內容，刪除前逐一確認路徑（防範路徑組合意外）。</summary>
    private void DeleteCopyRoot()
    {
        if (!Directory.Exists(copyRoot))
        {
            return;
        }

        var copyRootFull = Path.GetFullPath(copyRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(copyRootFull))
        {
            var entryFull = Path.GetFullPath(entry);
            if (!entryFull.StartsWith(copyRootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"拒絕刪除 copyRoot 以外的路徑：{entryFull}");
            }
        }

        // 先逐一刪掉第一層的目錄連結本身（Model／Engine）：Windows 上 .NET 遞迴刪除遇到 junction 會擲「參數錯誤」，
        // 不遞迴的 Directory.Delete 只刪連結，不會動到 %APPDATA% 裡的模型
        foreach (var directory in Directory.EnumerateDirectories(copyRootFull))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(directory);
            }
        }

        Directory.Delete(copyRootFull, recursive: true);
    }

    /// <summary>遞迴複製資料夾裡的所有檔案（空資料夾不重建），回傳複製的檔案數。
    /// 來源第一層的 Model／Engine（不分大小寫）整個子樹不複製（複本改用目錄連結共用）。</summary>
    private static int CopyFiles(string sourceDir, string destDir)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            if (IsUserDataPath(relativePath))
            {
                continue;
            }

            var destFile = Path.Combine(destDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>相對路徑第一層是 Model 或 Engine（不分大小寫）時為 true。</summary>
    private static bool IsUserDataPath(string relativePath)
    {
        var separatorIndex = relativePath.IndexOf(Path.DirectorySeparatorChar);
        var firstSegment = separatorIndex < 0 ? relativePath : relativePath[..separatorIndex];
        return UserDataDirectories.Any(name => name.Equals(firstSegment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>腳本內容相同時不寫入（回傳 false），否則以 UTF-8 不加 BOM 寫入並回傳 true；寫入失敗時擲例外。</summary>
    private static bool WriteScriptIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
        return true;
    }
}
