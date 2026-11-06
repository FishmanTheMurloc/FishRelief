using System.Runtime.CompilerServices;
using FishRelief.Kernel.Manifest;
using Microsoft.Extensions.AI;

namespace FishRelief.Kernel.Admission;

/// <summary>
/// 准入中间件：宁可拒绝也不排长队。全局并发闸门 + 有界等待 + 等待者上限。
/// 拿到名额前最多等 admission.waitSeconds；超时或等待者满即抛 AdmissionRejectedException（宿主映射 429）。
/// 客户端取消（RequestAborted 传入的 token）在等待与占用全程生效，名额即时释放。
/// 并发策略读取自清单 admission 段（本设计无热加载，启动后不变），名额不区分流式/非流式。
/// </summary>
public sealed class AdmissionChatClient : DelegatingChatClient
{
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _globalSlot;
    private readonly int _waitSeconds;
    private readonly int _retryAfterSeconds;
    private int _globalWaiters;

    public AdmissionChatClient(
        IChatClient innerClient,
        IManifestRepository manifestRepository,
        TimeProvider? timeProvider = null)
        : base(innerClient)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        var admission = manifestRepository.Get().Manifest.Admission;
        _globalSlot = new SemaphoreSlim(admission.MaxConcurrentGlobal);
        _waitSeconds = admission.WaitSeconds;
        _retryAfterSeconds = admission.RetryAfterSeconds;
        MaxWaiters = admission.MaxWaiters;
    }

    /// <summary>同时在等待的请求数上限（清单 admission.maxWaiters）。</summary>
    public int MaxWaiters { get; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var gate = await AcquireAsync(cancellationToken);
        return await InnerClient.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var gate = await AcquireAsync(cancellationToken);
        await foreach (var update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>获取全局名额；宿主把闸门保持到响应完成（流式含整个枚举过程），释放由 using/finally 保证。</summary>
    private async Task<Gate> AcquireAsync(CancellationToken cancellationToken)
    {
        var startTimestamp = _timeProvider.GetTimestamp();

        if (Interlocked.Increment(ref _globalWaiters) > MaxWaiters)
        {
            Interlocked.Decrement(ref _globalWaiters);
            throw new AdmissionRejectedException("等待者已满", _retryAfterSeconds);
        }

        try
        {
            if (!await TryWaitAsync(_globalSlot, startTimestamp, cancellationToken))
            {
                throw new AdmissionRejectedException("等待超时或全局并发已满", _retryAfterSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // 等待期间客户端取消：未占用任何名额，直接出栈
        }

        Interlocked.Decrement(ref _globalWaiters);
        return new Gate(() => _globalSlot.Release());
    }

    /// <summary>
    /// 在统一的有界等待窗口内等一个名额；超时返回 false，取消直接抛出（客户端断开，非拒绝）。
    /// 等待由 TimeProvider 计时（测试可注入 FakeTimeProvider 虚拟推进时间），超时与调用方取消用 linked token 区分。
    /// </summary>
    private async Task<bool> TryWaitAsync(SemaphoreSlim slot, long startTimestamp, CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromSeconds(_waitSeconds) - _timeProvider.GetElapsedTime(startTimestamp);
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        // net8 的 TimeProvider 没有 CreateCancellationTokenSource 扩展（.NET 9 起才有），
        // 用 CreateTimer 在虚拟时钟到期时取消 CTS，FakeTimeProvider 推进时间即可确定性触发
        var timeoutCts = new CancellationTokenSource();
        using var timer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(), timeoutCts, remaining, Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await slot.WaitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false; // 等待窗口到期
        }
        finally
        {
            timer.Dispose();
            timeoutCts.Dispose();
        }
    }

    /// <summary>两级名额的释放句柄。</summary>
    private sealed class Gate(Action release) : IDisposable
    {
        private readonly Action _release = release;
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _release();
            }
        }
    }
}
