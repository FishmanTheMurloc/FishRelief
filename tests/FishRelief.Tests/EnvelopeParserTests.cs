using System.Text.Json;
using FishRelief.Kernel;
using FishRelief.Kernel.Envelope;
using FishRelief.Kernel.Envelope.Models;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Prompt;

namespace FishRelief.Tests;

/// <summary>
/// 信封解析器：验证渲染信封（任务 + 各规则键）的解析与各拒绝路径。
/// 规则名全部从清单读取，不硬编码（每个清单的规则名可以不同）。
/// 使用 manifests/demo 的大肥鱼派发清单。
/// </summary>
public sealed class EnvelopeParserTests
{
    [Fact]
    public void Resolve_ValidEnvelope_ReturnsTaskAndKeys()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var relations = Section(loaded, "relations");
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>
        {
            [entities] = ["泡泡大肥鱼", "像素大肥鱼"],
            [relations] = ["漂浮 × 稀有型"],
        });

        var context = EnvelopeParser.Resolve(loaded, envelope);

        Assert.Equal("dispatch", context.TaskId);
        Assert.Equal(["泡泡大肥鱼", "像素大肥鱼"], KeysOf(context, entities));
        Assert.Equal(["漂浮 × 稀有型"], KeysOf(context, relations));
    }

    [Fact]
    public void Resolve_MissingOptionalVariable_IsOmitted()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>
        {
            [entities] = ["电子大肥鱼"],
        });

        var context = EnvelopeParser.Resolve(loaded, envelope);

        Assert.Equal("dispatch", context.TaskId);
        Assert.Equal(["电子大肥鱼"], KeysOf(context, entities));
        Assert.DoesNotContain(context.Data, item => item.Name == Section(loaded, "relations"));
    }

    [Fact]
    public async Task Resolve_BlankKeys_IsRejectedByAssembly()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>
        {
            [entities] = ["电子大肥鱼", "", "   "],
        });

        // 解析器不再知道词表：空键能通过 schema，词表判定在组装侧
        var context = EnvelopeParser.Resolve(loaded, envelope);

        await Assert.ThrowsAsync<RuleKeyNotMatchedException>(
            () => PromptAssembler.AssembleAsync(loaded, context));
    }

    [Fact]
    public void Resolve_DuplicateEntity_IsPreserved()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>
        {
            [entities] = ["电子大肥鱼", "电子大肥鱼"],
        });

        var context = EnvelopeParser.Resolve(loaded, envelope);

        Assert.Equal(["电子大肥鱼", "电子大肥鱼"], KeysOf(context, entities));
    }

    [Fact]
    public void Resolve_PlainTextSystem_Rejects()
    {
        var loaded = Load();

        Assert.Throws<EnvelopeParseException>(
            () => EnvelopeParser.Resolve(loaded, "## 本次派发名单\n- 电子大肥鱼"));
    }

    [Fact]
    public void Resolve_UnknownTask_Rejects()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = Envelope("nope", new Dictionary<string, string[]>
        {
            [entities] = ["电子大肥鱼"],
        });

        Assert.Throws<EnvelopeParseException>(() => EnvelopeParser.Resolve(loaded, envelope));
    }

    [Fact]
    public void Resolve_UndeclaredVariable_Rejects()
    {
        var loaded = Load();
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>
        {
            [Section(loaded, "entities")] = ["电子大肥鱼"],
            ["undeclared"] = ["随便什么"],
        });

        Assert.Throws<EnvelopeParseException>(() => EnvelopeParser.Resolve(loaded, envelope));
    }

    [Fact]
    public async Task Resolve_UnknownKey_IsRejectedByAssembly()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>
        {
            [entities] = ["不存在的鱼"],
        });

        // 词表成员资格搬到组装侧：解析成功，组装时按“键 → case”索引判定
        var context = EnvelopeParser.Resolve(loaded, envelope);

        var exception = await Assert.ThrowsAsync<RuleKeyNotMatchedException>(
            () => PromptAssembler.AssembleAsync(loaded, context));

        // 仍是客户端错误（400 路径），不得退化成 PromptAssemblyException（500）
        Assert.False(typeof(PromptAssemblyException).IsAssignableFrom(exception.GetType()));
    }

    [Fact]
    public void Resolve_DuplicateRuleName_Rejects()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = JsonSerializer.Serialize(new
        {
            task = "dispatch",
            data = new[]
            {
                new { name = entities, keys = new[] { "电子大肥鱼" } },
                new { name = entities, keys = new[] { "团子大肥鱼" } },
            },
        });

        // 重复 name 不是 schema 能表达的（uniqueItems 按整项判等），必须由解析器显式拒绝并点名
        Assert.Throws<EnvelopeParseException>(() => EnvelopeParser.Resolve(loaded, envelope));
    }

    [Fact]
    public void Resolve_DataPropertyMissing_Rejects()
    {
        var loaded = Load();
        var envelope = JsonSerializer.Serialize(new { task = "dispatch" });

        // data 缺失合法地反序列化为空列表，但 entities 声明为 required=true，解析器直接拒绝
        Assert.Throws<EnvelopeParseException>(() => EnvelopeParser.Resolve(loaded, envelope));
    }

    [Fact]
    public void Resolve_DataNull_Rejects()
    {
        var loaded = Load();
        var envelope = JsonSerializer.Serialize(new { task = "dispatch", data = (object?)null });

        // data 缺失合法，但存在且不是数组（含 null）不是
        var exception = Assert.Throws<EnvelopeParseException>(() => EnvelopeParser.Resolve(loaded, envelope));
        Assert.Contains("data 必须是数组", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DataItemWithUnmappedField_Rejects()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = JsonSerializer.Serialize(new
        {
            task = "dispatch",
            data = new[] { new { name = entities, keys = new[] { "电子大肥鱼" }, extra = "多余字段" } },
        });

        // 信封解析（含未知字段拒绝）仍先于 schema 校验
        Assert.Throws<EnvelopeParseException>(() => EnvelopeParser.Resolve(loaded, envelope));
    }

    [Fact]
    public void Resolve_MissingRequiredVariable_Rejects()
    {
        var loaded = Load();
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>());

        // 必报由 C# 按 rules[].required 核验，文案点名规则
        var exception = Assert.Throws<EnvelopeParseException>(() => EnvelopeParser.Resolve(loaded, envelope));
        Assert.Contains("规则 \"entities\" 必须上报", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RequiredRuleReportsEmptyKeys_Rejects()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>
        {
            [entities] = [],
        });

        // “必报”只管这一项必须在 data 里；空不空由该规则 jsonSchema 的 minItems 判定
        var exception = Assert.Throws<EnvelopeParseException>(() => EnvelopeParser.Resolve(loaded, envelope));
        Assert.Contains("规则 \"entities\" 的负载不符合其 jsonSchema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_KeyOverLength_Rejects()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>
        {
            [entities] = [new string('a', 129)],
        });

        var exception = Assert.Throws<EnvelopeParseException>(() => EnvelopeParser.Resolve(loaded, envelope));
        Assert.Contains("规则 \"entities\" 的负载不符合其 jsonSchema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_KeyWithControlCharacter_IsRejectedByAssembly()
    {
        var loaded = Load();
        var entities = Section(loaded, "entities");
        var envelope = Envelope("dispatch", new Dictionary<string, string[]>
        {
            [entities] = ["电子\n大肥鱼"],
        });

        var context = EnvelopeParser.Resolve(loaded, envelope);

        await Assert.ThrowsAsync<RuleKeyNotMatchedException>(
            () => PromptAssembler.AssembleAsync(loaded, context));
    }

    private static string Envelope(string task, Dictionary<string, string[]> keys) =>
        JsonSerializer.Serialize(new
        {
            task,
            data = keys.Select(kv => new { name = kv.Key, keys = kv.Value }),
        });

    private static string Section(LoadedManifest loaded, string name) =>
        loaded.Manifest.Rules.Single(rule => rule.Name == name).Name;

    /// <summary>取信封中被上报规则的键（未上报的规则在 Data 里没有对应项）。</summary>
    private static IReadOnlyList<string> KeysOf(TaskData context, string name) =>
        Assert.IsType<MatchRuleData>(context.Data.Single(item => item.Name == name)).Keys;

    private static LoadedManifest Load()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifests", "demo", "manifest.json");
        return ManifestLoader.Load(manifestPath);
    }
}
