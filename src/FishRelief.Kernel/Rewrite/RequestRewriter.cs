using FishRelief.Kernel.Envelope;
using FishRelief.Kernel.Envelope.Models;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Manifest.Models;
using FishRelief.Kernel.Prompt;
using Microsoft.Extensions.AI;

namespace FishRelief.Kernel.Rewrite;

/// <summary>
/// 请求改写结果：权威重组后的消息与选项，以及解析出的上下文。
/// </summary>
public sealed record RequestRewriteResult
{
    /// <summary>重组后的消息：system prompt + 首条 user 重建，历史消息原样穿过。</summary>
    public required List<ChatMessage> Messages { get; init; }

    /// <summary>覆写后的选项：ModelId 为按任务可用集选定的模型名（上游真实模型名，越界回退默认），直接发往上游；Tools 为客户端申报工具经清单定义重建后的集合（未申报则为空），ToolMode 清空。</summary>
    public required ChatOptions Options { get; init; }

    /// <summary>解析出的任务 id（清单 tasks 的 key）。</summary>
    public required string TaskId { get; init; }

    /// <summary>信封里各规则上报的原始数据（客户端顺序）。只含客户端上报过的规则，列表里没有该 name 即未上报。</summary>
    public required IReadOnlyList<RuleData> Data { get; init; }
}

/// <summary>
/// 请求改写：网关权威组装。客户端请求被当作"选项申报"——
/// system prompt、首条 user 消息按清单重建；tools 只保留客户端申报的集合，但每个定义都按清单同名项重建
/// （客户端无法伪造描述与参数 schema），未申报的工具不向模型声明；model 必须命中任务声明的 availableModels，
/// 越界或缺省即 400，天然 fail-closed。
/// 历史消息（自首条 assistant 起）原样穿过，不复制不重编码（raw representation 保留给传输层）。
/// 客户端专用错误（结构校验、清单不匹配、上报键未命中词表）抛 <see cref="RequestValidationException"/> /
/// <see cref="EnvelopeParseException"/> / <see cref="RuleKeyNotMatchedException"/>，由宿主映射为 400；
/// 模板组装失败抛 <see cref="PromptAssemblyException"/>，属服务端清单问题，由宿主映射为 500。
/// </summary>
public sealed class RequestRewriter(IManifestRepository manifestRepository)
{
    /// <summary>把客户端请求改写为应发给上游的请求（消息 + 选项），只改写不发送。</summary>
    public async Task<RequestRewriteResult> RewriteAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var loaded = manifestRepository.Get();
        var incoming = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        ValidateStructure(incoming, loaded.Manifest);

        // 信封解析：从 system 消息的渲染信封取任务与各段声明键。失败抛 EnvelopeParseException（宿主映射 400）
        var taskData = EnvelopeParser.Resolve(loaded, FirstText(incoming, ChatRole.System));
        var task = loaded.Manifest.Tasks[taskData.TaskId];

        ValidateHistoryToolCalls(incoming, task);

        // 权威组装：丢弃客户端 system / 首条 user / tool_choice；tools 只保留申报的工具名、定义按清单重建；model 必须命中任务可用模型
        var rewritten = new List<ChatMessage>(incoming.Count)
        {
            new(ChatRole.System, await PromptAssembler.AssembleAsync(loaded, taskData)),
            new(ChatRole.User, task.FirstUserMessage),
        };
        rewritten.AddRange(SkipHistoryPrefix(incoming));

        var effectiveOptions = options?.Clone() ?? new ChatOptions();
        effectiveOptions.ModelId = SelectModel(task, options?.ModelId);
        effectiveOptions.Tools = ResolveTools(task, options?.Tools)
            .Select(t => (AITool)new JsonFunctionDeclaration(t.Name, t.Description, t.Parameters))
            .ToList();
        effectiveOptions.ToolMode = null; // tool_choice 一并清空，避免引用清单外的工具

        return new RequestRewriteResult
        {
            Messages = rewritten,
            Options = effectiveOptions,
            TaskId = taskData.TaskId,
            Data = taskData.Data,
        };
    }

    /// <summary>
    /// 消息结构校验：system 打头、随后恰一条 user、自首条 assistant 起只允许
    /// assistant（必带 tool_calls）与 tool（CallId 与最近一条 assistant 配对），消息数受限。
    /// </summary>
    private static void ValidateStructure(IReadOnlyList<ChatMessage> messages, ProjectManifest manifest)
    {
        if (messages.Count == 0)
        {
            throw new RequestValidationException("消息列表为空");
        }

        if (messages.Count > manifest.Limits.MaxMessages)
        {
            throw new RequestValidationException($"消息数 {messages.Count} 超过上限 {manifest.Limits.MaxMessages}");
        }

        if (messages[0].Role != ChatRole.System)
        {
            throw new RequestValidationException("缺少首条 system 消息");
        }

        if (messages.Count > 1 && messages[1].Role == ChatRole.System)
        {
            throw new RequestValidationException("只允许一条 system 消息");
        }

        var index = 1;
        if (index >= messages.Count || messages[index].Role != ChatRole.User)
        {
            throw new RequestValidationException("system 消息后必须是首条 user 消息");
        }

        index++;
        if (index < messages.Count && messages[index].Role == ChatRole.User)
        {
            throw new RequestValidationException("首条 assistant 前只允许一条 user 消息");
        }

        var pendingCallIds = new HashSet<string>(StringComparer.Ordinal);
        for (; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role == ChatRole.Assistant)
            {
                var calls = message.Contents.OfType<FunctionCallContent>().ToList();
                if (calls.Count == 0)
                {
                    throw new RequestValidationException("assistant 消息必须携带 tool_calls");
                }

                pendingCallIds.Clear();
                foreach (var call in calls)
                {
                    if (string.IsNullOrEmpty(call.CallId))
                    {
                        throw new RequestValidationException("assistant 的 tool_call 缺少 CallId");
                    }

                    pendingCallIds.Add(call.CallId);
                }
            }
            else if (message.Role == ChatRole.Tool)
            {
                var results = message.Contents.OfType<FunctionResultContent>().ToList();
                if (results.Count == 0)
                {
                    throw new RequestValidationException("tool 消息必须携带 tool result");
                }

                foreach (var result in results)
                {
                    if (!pendingCallIds.Remove(result.CallId))
                    {
                        throw new RequestValidationException($"tool result 的 CallId \"{result.CallId}\" 与最近一条 assistant 的 tool_calls 不配对");
                    }
                }
            }
            else
            {
                throw new RequestValidationException($"首条 assistant 起出现非法角色 {message.Role}");
            }
        }
    }

    /// <summary>历史 tool_calls 校验：所有轮次的 FunctionCallContent.Name 必须在该任务工具集内。</summary>
    private static void ValidateHistoryToolCalls(IReadOnlyList<ChatMessage> messages, TaskDescriptor task)
    {
        var toolNames = task.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                if (!toolNames.Contains(call.Name))
                {
                    throw new RequestValidationException($"历史 tool_calls 引用了清单外的工具 \"{call.Name}\"");
                }
            }
        }
    }

    /// <summary>
    /// 只解析客户端申报的工具：按名匹配清单定义，名字之外的描述与参数 schema 一律用清单重建；
    /// 未申报则不声明任何工具，未知或重复的工具名一律拒绝（防客户端注入清单外的工具）。
    /// </summary>
    private static List<ToolDefinition> ResolveTools(TaskDescriptor task, IList<AITool>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return [];
        }

        var known = task.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var resolved = new List<ToolDefinition>(requested.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in requested)
        {
            if (!seen.Add(tool.Name))
            {
                throw new RequestValidationException($"工具 \"{tool.Name}\" 重复声明");
            }

            if (!known.TryGetValue(tool.Name, out var definition))
            {
                throw new RequestValidationException($"请求引用了清单外的工具 \"{tool.Name}\"");
            }

            resolved.Add(definition);
        }

        return resolved;
    }

    /// <summary>校验客户端请求的模型名在本任务可用集内，越界即 400。</summary>
    private static string SelectModel(TaskDescriptor task, string? requestedModel)
    {
        if (requestedModel is null || !task.AvailableModels.Contains(requestedModel, StringComparer.Ordinal))
        {
            throw new RequestValidationException(
                $"模型 \"{requestedModel ?? "(未指定)"}\" 不在本任务可用模型集内");
        }

        return requestedModel;
    }

    /// <summary>取指定角色的第一条消息文本；不存在抛结构错误（system/user 都是必需品）。</summary>
    private static string FirstText(IReadOnlyList<ChatMessage> messages, ChatRole role)
    {
        var message = messages.FirstOrDefault(m => m.Role == role)
            ?? throw new RequestValidationException($"缺少 {role} 消息");
        return message.Text;
    }

    /// <summary>
    /// 历史消息：自首条 assistant 起原样追加。前缀（system + 首条 user）已被权威组装替换，
    /// 历史消息保持同一引用，raw representation 不受影响。
    /// </summary>
    private static IEnumerable<ChatMessage> SkipHistoryPrefix(IReadOnlyList<ChatMessage> messages)
    {
        var index = 0;
        while (index < messages.Count && messages[index].Role == ChatRole.System)
        {
            index++;
        }

        index++; // 首条 user
        for (; index < messages.Count; index++)
        {
            yield return messages[index];
        }
    }
}
