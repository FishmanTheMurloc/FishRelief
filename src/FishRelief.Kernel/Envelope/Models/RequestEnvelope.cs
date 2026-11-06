using System.Text.Json.Serialization;

namespace FishRelief.Kernel.Envelope.Models;

/// <summary>
/// 客户端在 system 消息里发出的渲染信封：任务选择 + 各规则上报的数据。
/// data 是数组，每项自带规则名（以清单 rules[].name 为准），内核不硬编码任何规则名；
/// 未知/重复规则名由解析器拒绝。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class RequestEnvelope
{
    /// <summary>清单 tasks 中的 task id。</summary>
    [JsonRequired]
    public string Task { get; init; } = "";

    /// <summary>各规则上报的数据，经 RuleDataConverter 多态反序列化为策略对应的 RuleData。</summary>
    public List<RuleData> Data { get; init; } = [];
}
