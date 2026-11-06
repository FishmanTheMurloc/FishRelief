namespace FishRelief.Kernel.Envelope;

/// <summary>
/// 信封解析失败：任务不匹配或名单不合法。宿主应把本异常映射为 400。
/// </summary>
public sealed class EnvelopeParseException(string message) : Exception(message)
{
}
