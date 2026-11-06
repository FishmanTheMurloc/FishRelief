using System.Text.Json;
using System.Text.Json.Serialization;

namespace FishRelief.Kernel.Manifest.Models;

/// <summary>
/// 命中分支：keys 说明该分支接受哪些上报值（精确相等），liquidObject 是命中后并入
/// Liquid 对象（模板条目）的附加字段（term 由内核按命中值自动注入，不得在此声明）。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MatchCase
{
    /// <summary>
    /// 该分支接受的字面量键（精确相等，不做任何模式匹配）；
    /// 同一规则内一个键最多只能出现在一个分支里。
    /// </summary>
    [JsonRequired]
    public List<string> Keys { get; init; } = [];

    /// <summary>命中后并入 Liquid 对象（模板条目）的字段，值为字符串或字符串数组。</summary>
    [JsonRequired]
    public Dictionary<string, JsonElement> LiquidObject { get; init; } = [];
}
