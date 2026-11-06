namespace FishRelief.Kernel.Manifest;

/// <summary>
/// 本地文件系统清单仓储：构造时从指定路径加载并校验清单（fail-fast），
/// 之后 Get 始终返回同一份不可变清单。配合 DI 单例使用。
/// </summary>
public sealed class FileSystemManifestRepository : IManifestRepository
{
    private readonly LoadedManifest _loaded;

    /// <param name="manifestPath">manifest.json 的路径；模板按清单所在目录解析。</param>
    public FileSystemManifestRepository(string manifestPath)
    {
        _loaded = ManifestLoader.Load(manifestPath);
    }

    public LoadedManifest Get() => _loaded;
}
