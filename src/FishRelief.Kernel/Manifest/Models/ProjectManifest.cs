using System.Text.Json;
using System.Text.Json.Serialization;

namespace FishRelief.Kernel.Manifest.Models;

/// <summary>
/// 项目包清单：网关与项目之间的数据契约，结构见 manifests/schema.json。
/// 未知字段与缺失必填字段均视为清单错误（严格反序列化）。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProjectManifest
{
    /// <summary>项目标识，不参与请求路由；仅用于清单校验与调试端点展示。</summary>
    [JsonRequired]
    public string Project { get; init; } = "";

    /// <summary>任务描述符集合，key 为 task id。</summary>
    [JsonRequired]
    public Dictionary<string, TaskDescriptor> Tasks { get; init; } = [];

    /// <summary>规则描述符表：每条规则自带负载 schema 与是否必报声明，命中后产出模板条目值；加载期编译为运行时计划。</summary>
    [JsonRequired]
    public List<RuleDescriptor> Rules { get; init; } = [];

    [JsonRequired]
    public ManifestLimits Limits { get; init; } = null!;

    /// <summary>准入策略：并发上限与有界等待。</summary>
    [JsonRequired]
    public AdmissionPolicy Admission { get; init; } = null!;

    /// <summary>允许透传到上游的路径白名单。</summary>
    [JsonRequired]
    public List<string> Endpoints { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TaskDescriptor
{
    /// <summary>本任务可用的模型名（上游真实模型名，直接发往上游）；客户端请求的 model 须在此集内，越界即 400。</summary>
    [JsonRequired]
    public List<string> AvailableModels { get; init; } = [];

    /// <summary>首条 user 消息固定串，网关权威组装时直接使用。</summary>
    [JsonRequired]
    public string FirstUserMessage { get; init; } = "";

    [JsonRequired]
    public PromptAssembly PromptAssembly { get; init; } = null!;

    /// <summary>本任务工具声明的权威定义集：客户端申报的工具名必须在此集内，网关按名用这里的描述与 schema 重建；未申报的工具不发给模型。</summary>
    [JsonRequired]
    public List<ToolDefinition> Tools { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PromptAssembly
{
    /// <summary>Liquid 模板文件路径，相对于清单所在目录。</summary>
    [JsonRequired]
    public string TemplateFile { get; init; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ManifestLimits
{
    [JsonRequired]
    public int MaxMessages { get; init; }

    [JsonRequired]
    public int MaxBodyBytes { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AdmissionPolicy
{
    /// <summary>全局同时在飞的请求数上限。</summary>
    [JsonRequired]
    public int MaxConcurrentGlobal { get; init; }

    /// <summary>拿到名额前的最长等待秒数。</summary>
    [JsonRequired]
    public int WaitSeconds { get; init; }

    /// <summary>同时在等待的请求数上限。</summary>
    [JsonRequired]
    public int MaxWaiters { get; init; }

    /// <summary>被拒时返回的 Retry-After 秒数。</summary>
    [JsonRequired]
    public int RetryAfterSeconds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ToolDefinition
{
    [JsonRequired]
    public string Name { get; init; } = "";

    [JsonRequired]
    public string Description { get; init; } = "";

    /// <summary>参数的 JSON Schema（OpenAI function calling 的 parameters）；可省略，省略时网关不向上游转发该字段；有值时必须为 JSON 对象。</summary>
    public JsonElement? Parameters { get; init; }
}
