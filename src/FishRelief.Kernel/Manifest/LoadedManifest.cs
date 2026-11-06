namespace FishRelief.Kernel.Manifest;

using System.Collections.Frozen;
using FishRelief.Kernel.Manifest.Models;

/// <summary>
/// 已加载并通过校验的清单，附带模板源码、清单所在目录与规则计划。
/// </summary>
public sealed class LoadedManifest
{
    public required ProjectManifest Manifest { get; init; }

    /// <summary>清单所在目录，模板中的相对路径以此为基准。</summary>
    public required string ManifestDirectory { get; init; }

    /// <summary>task id → 模板源码。</summary>
    public required IReadOnlyDictionary<string, string> TaskTemplates { get; init; }

    /// <summary>按清单声明序编译的规则计划；请求期遍历该列表，顺序即渲染顺序。</summary>
    internal IReadOnlyList<Rule> RulePlans { get; init; } = [];

    /// <summary>规则名 → 计划，供信封侧按名字 O(1) 查名；与 <see cref="RulePlans"/> 同源。</summary>
    internal FrozenDictionary<string, Rule> PlansByName { get; init; } = FrozenDictionary<string, Rule>.Empty;
}
