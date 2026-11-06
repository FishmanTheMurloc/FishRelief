namespace FishRelief.Kernel;

/// <summary>
/// 提示词组装失败：请求引用了清单中不存在的任务或实体，或模板本身无法解析。
/// </summary>
public sealed class PromptAssemblyException(string message) : Exception(message)
{
}
