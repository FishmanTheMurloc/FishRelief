using System.Text.Json;
using FishRelief.Kernel.Envelope;
using FishRelief.Kernel.Envelope.Models;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Manifest.Models;

namespace FishRelief.Tests;

/// <summary>
/// 本地私有项目包（manifests/local，本地 only，不入仓库）的整链路回归：
/// 渲染信封里的键必须能被信封解析器还原为同一份键列表。
/// 实体名从清单规则动态取出，测试代码不引用任何业务专有名词；
/// 目录不存在时跳过（CI / 新 clone 无此目录）。
/// </summary>
public sealed class LocalProjectPackageTests
{
    private static readonly string ManifestPath =
        Path.Combine(AppContext.BaseDirectory, "manifests", "local", "manifest.json");

    [Fact]
    public void Envelope_ResolvesOriginalKeys()
    {
        if (!File.Exists(ManifestPath))
        {
            return; // 本地 only 清单不存在时跳过
        }

        var loaded = ManifestLoader.Load(ManifestPath);
        var manifest = loaded.Manifest;
        var rule = Assert.IsType<MatchRuleDescriptor>(manifest.Rules[0]);
        var keys = rule.Cases
            .SelectMany(matchCase => matchCase.Keys)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        Assert.True(keys.Length == 2, "本地清单应至少有两个实体键");

        var taskId = manifest.Tasks.Keys.First();
        var envelope = JsonSerializer.Serialize(new
        {
            task = taskId,
            data = new[] { new { name = rule.Name, keys } },
        });

        var context = EnvelopeParser.Resolve(loaded, envelope);

        Assert.Equal(taskId, context.TaskId);
        Assert.Equal(
            keys,
            Assert.IsType<MatchRuleData>(context.Data.Single(item => item.Name == rule.Name)).Keys);
    }
}
