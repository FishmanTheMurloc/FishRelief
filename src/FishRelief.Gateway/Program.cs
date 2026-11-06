using FishRelief.Gateway;
using FishRelief.Kernel.Admission;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Rewrite;
using FishRelief.Kernel.Transport;
using Microsoft.Extensions.AI;
using Serilog;

// 引导日志：builder 配置就绪前的失败（配置读取、清单加载）也要可见，UseSerilog 随后接管正式管道
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // 日志管道：级别与输出目标（控制台 + 按天滚动文件）全部来自 appsettings 的 Serilog 段
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

    // 日志台账：独立于运行日志的 Serilog 实例，只写结构化文件（JSON Lines，按天滚动）
    var auditPath = builder.Configuration["Audit:Path"]
        ?? throw new InvalidOperationException("缺少配置 Audit:Path");
    var auditRetention = builder.Configuration.GetValue<int?>("Audit:RetainedFileCountLimit") ?? 90;
    builder.Services.AddSingleton(_ => new AuditLog(auditPath, auditRetention));

    // 清单仓储：启动时加载，加载失败拒绝启动（fail-closed）
    builder.Services.AddSingleton<IManifestRepository>(_ =>
        new FileSystemManifestRepository(builder.Configuration["Manifest:Path"]
            ?? throw new InvalidOperationException("缺少配置 Manifest:Path")));

    // 请求改写：校验 + 权威组装（中间件与 debug 端点共用）
    builder.Services.AddSingleton<RequestRewriter>();

    // 上游传输：无整体超时，禁自动重定向（防上游 token 外漏）
    // 启动探测与正式请求复用同一 invoker；探测自带超时（见下方 CancellationTokenSource）
    var upstreamInvoker = new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    builder.Services.AddSingleton<HttpMessageInvoker>(upstreamInvoker);

    var upstreamEndpoint = builder.Configuration["Upstream:Endpoint"];
    if (string.IsNullOrWhiteSpace(upstreamEndpoint))
    {
        throw new InvalidOperationException("缺少配置 Upstream:Endpoint");
    }

    var upstreamApiKey = builder.Configuration["Upstream:ApiKey"];
    if (string.IsNullOrWhiteSpace(upstreamApiKey))
    {
        throw new InvalidOperationException("缺少配置 Upstream:ApiKey");
    }

    // 上游模型清单：启动时强制探测一次；探测失败拒绝启动（fail-closed）
    // 探测在 Build 之后执行（见下方启动阶段）：此时日志管线已就绪，成功与失败都能落盘
    var modelsEndpoint = ResolveModelsEndpoint(new Uri(upstreamEndpoint));

    // 中间件链：改写（校验+权威组装） → 准入（全局闸门+有界等待） → 上游传输
    builder.Services.AddSingleton<IChatClient>(sp =>
    {
        var repository = sp.GetRequiredService<IManifestRepository>();

        var upstream = new UpstreamChatClient(
            upstreamInvoker,
            new Uri(upstreamEndpoint),
            () => upstreamApiKey);

        return new RequestRewriteChatClient(
            new AdmissionChatClient(upstream, repository),
            sp.GetRequiredService<RequestRewriter>());
    });

    var app = builder.Build();

    // 日志工厂首次解析后，静态 Serilog 才切换到配置好的 logger；先解析再探测，保证探测日志（含失败）进入文件
    app.Services.GetRequiredService<ILoggerFactory>();

    IReadOnlySet<string> upstreamModels;
    using (var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    {
        try
        {
            upstreamModels = await new UpstreamModelCatalogClient(upstreamInvoker, modelsEndpoint, () => upstreamApiKey)
                .ListModelsAsync(probeTimeout.Token);
        }
        catch (UpstreamException ex)
        {
            throw new InvalidOperationException($"上游模型探测失败（HTTP {ex.StatusCode}）：{ex.Body}", ex);
        }
        catch (OperationCanceledException ex) when (probeTimeout.IsCancellationRequested)
        {
            throw new InvalidOperationException("上游模型探测超时（10 秒）", ex);
        }
    }

    app.Services.GetRequiredService<ILogger<Program>>().LogInformation(
        "上游模型探测完成，可用模型 {Count} 个：{Models}",
        upstreamModels.Count,
        string.Join(", ", upstreamModels.OrderBy(m => m, StringComparer.Ordinal)));

    // 清单声明的每个模型名都必须在上游模型清单中；缺失即拒绝启动（fail-closed）
    var manifest = app.Services.GetRequiredService<IManifestRepository>().Get().Manifest;
    var missingModels = manifest.Tasks.Values
        .SelectMany(task => task.AvailableModels)
        .Distinct(StringComparer.Ordinal)
        .Where(model => !upstreamModels.Contains(model))
        .OrderBy(model => model, StringComparer.Ordinal)
        .ToList();
    if (missingModels.Count > 0)
    {
        throw new InvalidOperationException($"清单模型未在上游模型清单中暴露：{string.Join(", ", missingModels)}");
    }

    // 请求日志：每请求一条完成记录（方法/路径/状态码/耗时/来源 IP），不记录请求体
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} from {RemoteIp} → {StatusCode} in {Elapsed:0.0}ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            diagnosticContext.Set("RemoteIp", httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    });

    // 启动即解析中间件链与台账：配置缺失、清单加载失败、台账路径不可写都在启动阶段暴露，而非首个请求才 500
    app.Services.GetRequiredService<IChatClient>();
    app.Services.GetRequiredService<AuditLog>();

    // 调试端点是可选能力：默认不注册（访问得到 404），仅在显式开启时挂载
    // 二者无鉴权且会暴露项目名与任务列表，因此绝不能默认暴露
    if (builder.Configuration.GetValue<bool>("Debug:EndpointsEnabled"))
    {
        app.MapGet("/debug/manifest", (IManifestRepository repo) =>
        {
            var loaded = repo.Get();
            return Results.Ok(new
            {
                loaded.Manifest.Project,
                Tasks = loaded.Manifest.Tasks.Keys,
            });
        });

        // 改写预览：与线上共用解析与改写，但不进入准入、不调用上游、不写台账
        app.MapPost("/debug/rewrite", DebugRewriteEndpoint.HandleAsync);

        app.Logger.LogWarning(
            "调试端点已启用：/debug/manifest 与 /debug/rewrite 无鉴权，仅限本地调试，禁止暴露在公网");
    }

    // 模型列表：清单内各任务 availableModels 的公开名并集（只回公开名，不代理上游模型清单）
    app.MapGet("/v1/models", ModelsEndpoint.HandleAsync);

    app.MapPost("/v1/chat/completions", ChatCompletionsEndpoint.HandleAsync);

    app.Run();
}
catch (Exception ex)
{
    // fail-closed：启动失败（配置缺失、清单非法、清单模型不在上游清单、上游模型探测失败）记录后以非零码退出，交由进程管理器处理
    Log.Fatal(ex, "网关启动失败");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>由 chat completions 端点推导上游模型清单端点（同一 base 下的 /v1/models）。</summary>
static Uri ResolveModelsEndpoint(Uri chatCompletionsEndpoint)
{
    const string suffix = "/chat/completions";
    var path = chatCompletionsEndpoint.AbsolutePath;
    if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Upstream:Endpoint 必须以 {suffix} 结尾，才能推导模型清单地址");
    }

    return new Uri(chatCompletionsEndpoint, path[..^suffix.Length] + "/models");
}
