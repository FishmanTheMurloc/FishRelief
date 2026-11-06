namespace FishRelief.Kernel.Manifest;

/// <summary>
/// 项目包清单仓储：屏蔽清单的存放介质（本地文件 / 将来可能的数据库）。
/// 返回的 <see cref="LoadedManifest"/> 是不可变对象，实现应保证调用方拿到的是
/// 已通过加载校验的完整清单；加载失败抛 ManifestException（fail-closed）。
/// 当前设计无热加载：实现可在构造时加载并缓存，注册为 DI 单例。
/// </summary>
public interface IManifestRepository
{
    /// <summary>获取已加载并通过校验的项目包清单。</summary>
    LoadedManifest Get();
}
