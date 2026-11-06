using FishRelief.Kernel.Manifest;

namespace FishRelief.Tests;

/// <summary>
/// 本地文件清单仓储：构造时加载（fail-fast），Get 返回同一份不可变清单；
/// 路径不存在时抛 ManifestException。
/// </summary>
public sealed class FileSystemManifestRepositoryTests
{
    [Fact]
    public void Get_ReturnsSameLoadedManifest()
    {
        var repo = new FileSystemManifestRepository(DemoPath());

        var first = repo.Get();
        var second = repo.Get();

        Assert.Same(first, second);
        Assert.Equal("demo", first.Manifest.Project);
    }

    [Fact]
    public void Constructor_MissingFile_ThrowsManifestException()
    {
        Assert.Throws<ManifestException>(
            () => new FileSystemManifestRepository("manifests/not-exists/manifest.json"));
    }

    private static string DemoPath() =>
        Path.Combine(AppContext.BaseDirectory, "manifests", "demo", "manifest.json");
}
