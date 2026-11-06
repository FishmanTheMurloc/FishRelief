using FishRelief.Kernel.Envelope.Models;

namespace FishRelief.Kernel.Envelope;

/// <summary>
/// 从渲染信封解析出的任务数据：任务选择 + 客户端为各规则上报的原始负载。
/// 只含客户端上报过的规则，列表里没有该 name 即未上报，与“报了空数组”可区分。
/// </summary>
public sealed record TaskData
{
    /// <summary>清单 tasks 中的 task id。</summary>
    public required string TaskId { get; init; }

    /// <summary>各规则上报的原始负载（客户端顺序）；同一 name 至多一项。</summary>
    public required IReadOnlyList<RuleData> Data { get; init; }
}
