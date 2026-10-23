namespace Translation.Bridge.Core.Interface;

/// <summary>目錄連結：Windows 用 junction（不需要系統管理員），其他平台用符號連結（讓 Linux 測試能跑）。</summary>
public interface IDirectoryJunction
{
    /// <summary>path 是目錄連結時回傳目標的完整路徑；不存在或是一般資料夾時回傳 null。</summary>
    string? GetTarget(string path);

    /// <summary>在 linkPath 建立指向 targetPath 的目錄連結；linkPath 不能已經存在，targetPath 必須已存在。失敗擲例外。</summary>
    void Create(string linkPath, string targetPath);
}
