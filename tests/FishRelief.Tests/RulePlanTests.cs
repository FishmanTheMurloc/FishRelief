using System.Collections.Frozen;
using System.Text.Json;
using FishRelief.Kernel;
using FishRelief.Kernel.Envelope.Models;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Manifest.Models;
using Json.Schema;

namespace FishRelief.Tests;

/// <summary>
/// 匹配规则运行时计划的单元测试：直接构造计划对象，不经清单加载与 Liquid 渲染。
/// 钉住四件事：未上报为空、命中注入顺序、未命中是客户端错误（400）、负载类型不符是服务端错误（500）。
/// </summary>
public sealed class RulePlanTests
{
    private static MatchRule CreateRule() =>
        new(
            "entities",
            false,
            JsonSchema.FromText("""{"type":"array"}"""),
            new Dictionary<string, MatchCase>(StringComparer.Ordinal)
            {
                ["甲"] = new MatchCase
                {
                    Keys = ["甲", "乙"],
                    LiquidObject = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["kind"] = JsonSerializer.SerializeToElement("鱼类"),
                        ["note"] = JsonSerializer.SerializeToElement("示例说明"),
                    },
                },
            }.ToFrozenDictionary(StringComparer.Ordinal));

    [Fact]
    public void BuildEntries_NotReported_IsEmpty()
    {
        var rule = CreateRule();

        Assert.Empty(rule.BuildEntries(null));
    }

    [Fact]
    public void BuildEntries_HitKey_EmitsTermBeforeFields()
    {
        var rule = CreateRule();
        var data = new MatchRuleData { Name = "entities", Keys = ["甲"] };

        var entry = Assert.Single(rule.BuildEntries(data));

        // term 由内核注入且排在最前；liquidObject 的字段按声明序追加
        Assert.Equal(["term", "kind", "note"], entry.Keys.ToArray());
        Assert.Equal("甲", entry["term"]);
        Assert.Equal("鱼类", entry["kind"]);
        Assert.Equal("示例说明", entry["note"]);
    }

    [Fact]
    public void BuildEntries_UnknownKey_ThrowsClientError()
    {
        var rule = CreateRule();
        var data = new MatchRuleData { Name = "entities", Keys = ["未知"] };

        var exception = Assert.Throws<RuleKeyNotMatchedException>(() => rule.BuildEntries(data));

        // 语义是客户端错误（400），不得退化成 PromptAssemblyException（500）
        Assert.False(typeof(PromptAssemblyException).IsAssignableFrom(exception.GetType()));
    }

    [Fact]
    public void BuildEntries_WrongPayloadType_ThrowsServerError()
    {
        var rule = CreateRule();

        Assert.Throws<PromptAssemblyException>(() => rule.BuildEntries(new OtherRuleData()));
    }

    /// <summary>仅用于构造“负载类型不符”的输入；不参与 JSON 序列化。</summary>
    private sealed class OtherRuleData : RuleData
    {
    }
}
