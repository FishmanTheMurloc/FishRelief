namespace FishRelief.Kernel.Rewrite;

/// <summary>
/// 请求结构校验失败：消息序列、tool_calls 历史、清单超限等。宿主应把本异常映射为 400。
/// </summary>
public sealed class RequestValidationException(string message) : Exception(message)
{
}
