﻿using System.Collections.Concurrent;
using System.Globalization;
using FishRelief.Kernel.Envelope;
using FishRelief.Kernel.Manifest;
using Fluid;

namespace FishRelief.Kernel.Prompt;

/// <summary>
/// 提示词组装：按清单规则表 + 客户端在渲染信封里上报的各键，确定性渲染出完整 system prompt。
/// 客户端传来的提示词正文不参与任何环节；规则名与数量由清单声明，内核不解释其含义。
/// </summary>
public static class PromptAssembler
{
    private static readonly FluidParser Parser = new();
    private static readonly TemplateOptions Options = CreateOptions();
    private static readonly ConcurrentDictionary<string, IFluidTemplate> TemplateCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 渲染指定任务的 system prompt。按加载期编译的规则计划（清单声明序）逐条处理：
    /// 在信封数据里找同名项，取不到时绑定为空数组；取到后由计划自己完成命中判定与条目构造。
    /// 行的格式由任务模板自行循环输出。
    /// </summary>
    public static async Task<string> AssembleAsync(LoadedManifest loaded, TaskData taskData)
    {
        if (!loaded.Manifest.Tasks.ContainsKey(taskData.TaskId))
        {
            throw new PromptAssemblyException($"未知任务 \"{taskData.TaskId}\"");
        }

        if (!loaded.TaskTemplates.TryGetValue(taskData.TaskId, out var taskTemplateSource))
        {
            throw new PromptAssemblyException($"任务 \"{taskData.TaskId}\" 没有可用的模板");
        }

        var context = new TemplateContext(Options);
        foreach (var plan in loaded.RulePlans)
        {
            var data = taskData.Data.FirstOrDefault(item => item.Name == plan.Name);
            context.SetValue(plan.Name, plan.BuildEntries(data));
        }

        var template = ParseTemplate(taskTemplateSource, $"任务 \"{taskData.TaskId}\"");
        return NormalizeNewlines(await template.RenderAsync(context)).TrimEnd('\n');
    }

    private static IFluidTemplate ParseTemplate(string source, string what)
    {
        if (TemplateCache.TryGetValue(source, out var cached))
        {
            return cached;
        }

        if (!Parser.TryParse(source, out var template, out var error))
        {
            throw new PromptAssemblyException($"{what} 解析失败：{error}");
        }

        TemplateCache[source] = template;
        return template;
    }

    /// <summary>
    /// 渲染选项：区域性锁死，只使用 Liquid 内置过滤器。
    /// 片段只接收标量与数组变量，不需要注册模型类型。
    /// </summary>
    private static TemplateOptions CreateOptions()
    {
        return new TemplateOptions
        {
            CultureInfo = CultureInfo.InvariantCulture,
        };
    }

    /// <summary>提示词按 LF 输出，避免模板文件行尾影响上游前缀缓存。</summary>
    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');
}
