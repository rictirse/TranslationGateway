using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class DirectoryJunctionTests
{
    private readonly DirectoryJunction junction = new();

    /// <summary>結束時清掉暫存資料夾（連結與目標都會清到）。</summary>
    private static void DeleteDir(DirectoryInfo dir)
    {
        try { Directory.Delete(dir.FullName, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Create_ThenGetTargetReturnsTargetFullPath_AndFilesWrittenThroughLinkAppearInTarget()
    {
        var dir = Directory.CreateTempSubdirectory("junction-");
        try
        {
            var target = Path.Combine(dir.FullName, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "existing.bin"), "already there");

            var link = Path.Combine(dir.FullName, "link");
            junction.Create(link, target);

            Assert.Equal(Path.GetFullPath(target), junction.GetTarget(link));

            // 透過連結寫入的檔案會出現在 target 裡（同一份內容）
            File.WriteAllText(Path.Combine(link, "through-link.txt"), "hello");
            Assert.True(File.Exists(Path.Combine(target, "through-link.txt")));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "through-link.txt")));
            Assert.Equal("already there", File.ReadAllText(Path.Combine(link, "existing.bin")));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void GetTarget_RegularFolderOrMissingPath_ReturnsNull()
    {
        var dir = Directory.CreateTempSubdirectory("junction-");
        try
        {
            var regular = Path.Combine(dir.FullName, "regular");
            Directory.CreateDirectory(regular);

            Assert.Null(junction.GetTarget(regular));
            Assert.Null(junction.GetTarget(Path.Combine(dir.FullName, "missing")));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void DeleteLinkThenParentRecursive_TargetAndItsFilesSurvive()
    {
        var dir = Directory.CreateTempSubdirectory("junction-");
        try
        {
            var target = Path.Combine(dir.FullName, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "model.bin"), "model data");

            // 連結放在另一個資料夾底下，把那個資料夾（連結的上層）整個遞迴刪除
            var parent = Path.Combine(dir.FullName, "parent");
            Directory.CreateDirectory(parent);
            var link = Path.Combine(parent, "link");
            junction.Create(link, target);

            // 先用不遞迴的 Delete 只刪連結本身（Windows 上 .NET 遞迴刪除遇到 junction 會擲例外，PotPlayerPreparer 也是這樣刪）
            Directory.Delete(link);
            Directory.Delete(parent, recursive: true);

            Assert.False(Directory.Exists(parent));
            Assert.True(Directory.Exists(target));
            Assert.Equal("model data", File.ReadAllText(Path.Combine(target, "model.bin")));
        }
        finally
        {
            DeleteDir(dir);
        }
    }
}
