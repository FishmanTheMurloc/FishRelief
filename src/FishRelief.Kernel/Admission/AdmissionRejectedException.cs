namespace FishRelief.Kernel.Admission;

/// <summary>
/// 准入拒绝：并发满、等待超时或等待者满。宿主应映射为 429 + Retry-After
/// （Retry-After 取 <see cref="RetryAfterSeconds"/> 加 0.8~1.2 抖动，抖动在宿主 HTTP 层做）。
/// 准入拒绝一律可重试；不可重试类（配额耗尽、拉黑）属于二期记账层，不走本异常。
/// </summary>
public sealed class AdmissionRejectedException : Exception
{
    public AdmissionRejectedException(string message, int retryAfterSeconds) : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>建议客户端退避的秒数（来自清单 admission.retryAfterSeconds 基准值）。</summary>
    public int RetryAfterSeconds { get; }
}
