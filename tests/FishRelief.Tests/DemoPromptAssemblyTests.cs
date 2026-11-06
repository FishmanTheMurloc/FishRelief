using FishRelief.Kernel;
using FishRelief.Kernel.Envelope;
using FishRelief.Kernel.Envelope.Models;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Prompt;

namespace FishRelief.Tests;

/// <summary>
/// demo 项目包（大肥鱼派发站）的提示词组装：
/// 用 manifests/demo 的清单验证「清单加载 → 键提取 → 模板渲染」整条链路。
/// 变量与条目都按清单声明处理，测试不假设规则的具体名称。
/// </summary>
public sealed class DemoPromptAssemblyTests
{
    [Fact]
    public async Task Dispatch_SingleFish_RendersListOnly()
    {
        var loaded = Load();

        var prompt = await PromptAssembler.AssembleAsync(
            loaded, Envelope(loaded, "dispatch", ["电子大肥鱼"], []));

        var expectedTail = "\n\n## 本次派发名单\n"
            + "- 电子大肥鱼：电子系，人气经典款，会发出哔哔的电流声，路过的人都会被电到心化。适合首次申领者。";
        Assert.StartsWith("你是大肥鱼派发站的调度员", prompt);
        Assert.EndsWith(expectedTail, prompt);
        Assert.DoesNotContain("## 系别相性", prompt);
        Assert.DoesNotContain("\r", prompt);
    }

    [Fact]
    public async Task Dispatch_RuleAbsentFromData_RendersEmptyVariable()
    {
        var loaded = Load();

        // Data 只含客户端上报过的规则；模板引用的相性规则缺席时，组装层必须兜底绑定为空数组而不是报错
        var rules = loaded.Manifest.Rules;
        var taskData = new TaskData
        {
            TaskId = "dispatch",
            Data =
            [
                new MatchRuleData
                {
                    Name = rules.Single(rule => rule.Name == "entities").Name,
                    Keys = ["电子大肥鱼"],
                },
            ],
        };

        var prompt = await PromptAssembler.AssembleAsync(loaded, taskData);

        var expectedTail = "\n\n## 本次派发名单\n"
            + "- 电子大肥鱼：电子系，人气经典款，会发出哔哔的电流声，路过的人都会被电到心化。适合首次申领者。";
        Assert.StartsWith("你是大肥鱼派发站的调度员", prompt);
        Assert.EndsWith(expectedTail, prompt);
        Assert.DoesNotContain("## 系别相性", prompt);
        Assert.DoesNotContain("\r", prompt);
    }

    [Fact]
    public async Task Dispatch_DeclaredRelations_RendersAffinityBlockInDeclaredOrder()
    {
        var loaded = Load();

        var prompt = await PromptAssembler.AssembleAsync(
            loaded,
            Envelope(loaded, "dispatch", ["泡泡大肥鱼", "像素大肥鱼"], ["漂浮 × 稀有型", "软萌 × 电子"]));

        Assert.EndsWith(
            "\n## 系别相性\n"
            // 输出顺序 = 声明顺序
            + "- 漂浮 × 稀有型：稀有浮动：展示缸升级为黄金展位\n"
            + "- 软萌 × 电子：反差萌加成：同时在场时额外附赠限量贴纸",
            prompt);
    }

    [Fact]
    public async Task Dispatch_DeclaredRelationWithoutMatchingTags_Renders()
    {
        var loaded = Load();

        // 电子大肥鱼自身只有"电子系"标签；声明与该标签无关的关联，仍按声明渲染
        var prompt = await PromptAssembler.AssembleAsync(
            loaded, Envelope(loaded, "dispatch", ["电子大肥鱼"], ["漂浮 × 稀有型"]));

        Assert.EndsWith("- 漂浮 × 稀有型：稀有浮动：展示缸升级为黄金展位", prompt);
    }

    [Fact]
    public async Task Dispatch_KeyWithoutMatchingCase_ThrowsClientError()
    {
        var loaded = Load();

        // 词表命中只在组装侧判定一次：未命中任何 case 是客户端错误，必须抛出而不是静默丢行
        var exception = await Assert.ThrowsAsync<RuleKeyNotMatchedException>(
            () => PromptAssembler.AssembleAsync(loaded, Envelope(loaded, "dispatch", ["不存在的鱼"], [])));

        // 防止未来把两个异常合并回 500
        Assert.False(typeof(PromptAssemblyException).IsAssignableFrom(exception.GetType()));
    }

    private static TaskData Envelope(
        LoadedManifest loaded,
        string taskId,
        IReadOnlyList<string> entityKeys,
        IReadOnlyList<string> relationKeys)
    {
        var rules = loaded.Manifest.Rules;
        return new TaskData
        {
            TaskId = taskId,
            Data =
            [
                new MatchRuleData
                {
                    Name = rules.Single(rule => rule.Name == "entities").Name,
                    Keys = [.. entityKeys],
                },
                new MatchRuleData
                {
                    Name = rules.Single(rule => rule.Name == "relations").Name,
                    Keys = [.. relationKeys],
                },
            ],
        };
    }

    private static LoadedManifest Load()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifests", "demo", "manifest.json");
        return ManifestLoader.Load(manifestPath);
    }
}
