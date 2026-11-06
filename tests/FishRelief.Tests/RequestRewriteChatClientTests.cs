using System.Runtime.CompilerServices;
using System.Text.Json;
using FishRelief.Kernel;
using FishRelief.Kernel.Envelope;
using FishRelief.Kernel.Manifest;
using FishRelief.Kernel.Rewrite;
using Microsoft.Extensions.AI;

namespace FishRelief.Tests;

/// <summary>
/// 请求改写中间件：验证网关权威组装（system/user/model 覆写、申报工具按清单重建、历史原样穿过）
/// 与各拒绝路径。inner client 用捕获型假实现。
/// 客户端上报的是渲染信封（任务 + 各变量键）；变量名从 demo 清单读取，不硬编码。
/// </summary>
public sealed class RequestRewriteChatClientTests
{
    private static readonly LoadedManifest Demo = ManifestLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "manifests", "demo", "manifest.json"));

    [Fact]
    public async Task ValidRequest_RewritesAndKeepsHistory()
    {
        var (client, capture) = Create();
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, DispatchEnvelope()),
            new(ChatRole.User, "请为本次申请生成大肥鱼派发单"),
            AssistantWithCalls(("CheckStock", """{"fishName":"电子大肥鱼"}""", "call-1")),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "Remaining: 3")]),
        };

        await client.GetResponseAsync(history, new ChatOptions
        {
            ModelId = "deepseek-flash",
            Tools = [ClientTool("CheckStock"), ClientTool("Cancel")],
        });

        var sent = capture.Messages!;
        Assert.StartsWith("你是大肥鱼派发站的调度员", sent[0].Text);
        Assert.Equal("请为本次申请生成大肥鱼派发单", sent[1].Text);
        // 历史消息原样穿过（同一引用，raw representation 不受影响）
        Assert.Same(history[2], sent[2]);
        Assert.Same(history[3], sent[3]);
        // model 与 tools 按清单覆写：只声明客户端申报的工具，定义取自清单
        Assert.Equal("deepseek-flash", capture.Options!.ModelId);
        var declarations = capture.Options.Tools!.Cast<JsonFunctionDeclaration>().ToList();
        Assert.Equal(["CheckStock", "Cancel"], declarations.Select(t => t.Name));
        foreach (var declaration in declarations)
        {
            var definition = Demo.Manifest.Tasks["dispatch"].Tools.Single(t => t.Name == declaration.Name);
            Assert.Equal(definition.Description, declaration.Description);
            Assert.Equal(definition.Parameters!.Value.GetRawText(), declaration.JsonSchema.GetRawText());
        }

        Assert.Null(capture.Options.ToolMode);
    }

    [Fact]
    public async Task NoDeclaredTools_AdvertisesNone()
    {
        var (client, capture) = Create();

        await client.GetResponseAsync(ValidMessages(), new ChatOptions { ModelId = "deepseek-flash" });

        // 客户端未申报工具 = 本次不需要工具调用，不补发清单里的工具
        Assert.Empty(capture.Options!.Tools!);
        Assert.Null(capture.Options.ToolMode);
    }

    [Fact]
    public async Task DeclaredToolOutsideManifest_Rejects()
    {
        var (client, _) = Create();

        await Assert.ThrowsAsync<RequestValidationException>(() => client.GetResponseAsync(
            ValidMessages(), new ChatOptions { ModelId = "deepseek-flash", Tools = [ClientTool("SneakyFreeChat")] }));
    }

    [Fact]
    public async Task DuplicateDeclaredTool_Rejects()
    {
        var (client, _) = Create();

        await Assert.ThrowsAsync<RequestValidationException>(() => client.GetResponseAsync(
            ValidMessages(), new ChatOptions { ModelId = "deepseek-flash", Tools = [ClientTool("CheckStock"), ClientTool("CheckStock")] }));
    }

    [Fact]
    public async Task TaskFromEnvelope_SelectsTask()
    {
        var (client, capture) = Create();
        var messages = new List<ChatMessage>
        {
            // 首条 user 消息内容与任务无关，task 由信封决定
            new(ChatRole.System, Envelope("dispatch", ("entities", ["团子大肥鱼"]))),
            new(ChatRole.User, "随便写点什么"),
        };

        await client.GetResponseAsync(messages, new ChatOptions { ModelId = "deepseek-flash" });

        Assert.StartsWith("你是大肥鱼派发站的调度员", capture.Messages![0].Text);
        Assert.Equal("deepseek-flash", capture.Options!.ModelId);
    }

    [Fact]
    public async Task UnknownEntity_ThrowsRuleKeyNotMatched()
    {
        var (client, _) = Create();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Envelope("dispatch", ("entities", ["不存在的鱼"]))),
            new(ChatRole.User, "请为本次申请生成大肥鱼派发单"),
        };

        // 词表命中在组装侧判定，异常类型明确为客户端错误（宿主映射 400）
        await Assert.ThrowsAsync<RuleKeyNotMatchedException>(() => client.GetResponseAsync(messages));
    }

    [Fact]
    public async Task PlainTextSystemMessage_Rejects()
    {
        var (client, _) = Create();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "## 本次派发名单\n- 电子大肥鱼"),
            new(ChatRole.User, "请为本次申请生成大肥鱼派发单"),
        };

        await Assert.ThrowsAsync<EnvelopeParseException>(() => client.GetResponseAsync(messages));
    }

    [Fact]
    public async Task TwoSystemMessages_Rejects()
    {
        var (client, _) = Create();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DispatchEnvelope()),
            new(ChatRole.System, DispatchEnvelope()),
            new(ChatRole.User, "请为本次申请生成大肥鱼派发单"),
        };

        await Assert.ThrowsAsync<RequestValidationException>(() => client.GetResponseAsync(messages));
    }

    [Fact]
    public async Task HistoryToolCallOutsideManifest_Rejects()
    {
        var (client, _) = Create();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DispatchEnvelope()),
            new(ChatRole.User, "请为本次申请生成大肥鱼派发单"),
            AssistantWithCalls(("SneakyFreeChat", "{}", "call-1")),
        };

        await Assert.ThrowsAsync<RequestValidationException>(() => client.GetResponseAsync(messages));
    }

    [Fact]
    public async Task UnpairedToolResult_Rejects()
    {
        var (client, _) = Create();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DispatchEnvelope()),
            new(ChatRole.User, "请为本次申请生成大肥鱼派发单"),
            new(ChatRole.Tool, [new FunctionResultContent("call-orphan", "result")]),
        };

        await Assert.ThrowsAsync<RequestValidationException>(() => client.GetResponseAsync(messages));
    }

    [Fact]
    public async Task AssistantWithoutToolCalls_Rejects()
    {
        var (client, _) = Create();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DispatchEnvelope()),
            new(ChatRole.User, "请为本次申请生成大肥鱼派发单"),
            new(ChatRole.Assistant, "自由发挥的 assistant 文本"),
        };

        await Assert.ThrowsAsync<RequestValidationException>(() => client.GetResponseAsync(messages));
    }

    [Fact]
    public async Task MissingSystem_Rejects()
    {
        var (client, _) = Create();
        var messages = new List<ChatMessage> { new(ChatRole.User, "请为本次申请生成大肥鱼派发单") };

        await Assert.ThrowsAsync<RequestValidationException>(() => client.GetResponseAsync(messages));
    }

    [Fact]
    public async Task RequestedAvailableModel_IsUsed()
    {
        var (client, capture) = Create();

        await client.GetResponseAsync(ValidMessages(), new ChatOptions { ModelId = "deepseek-flash" });

        Assert.Equal("deepseek-flash", capture.Options!.ModelId);
    }

    [Fact]
    public async Task RequestedUnavailableModel_Rejects()
    {
        var (client, _) = Create();

        await Assert.ThrowsAsync<RequestValidationException>(() => client.GetResponseAsync(
            ValidMessages(), new ChatOptions { ModelId = "claude-3.5-sonnet" }));
    }

    [Fact]
    public async Task MissingModel_Rejects()
    {
        var (client, _) = Create();

        await Assert.ThrowsAsync<RequestValidationException>(
            () => client.GetResponseAsync(ValidMessages()));
    }

    [Fact]
    public async Task Streaming_SharesRewriteAndPassesThrough()
    {
        var (client, inner) = Create();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(ValidMessages(), new ChatOptions { ModelId = "deepseek-flash" }))
        {
            updates.Add(update);
        }

        Assert.Single(updates);

        // 改写逻辑共用：inner 收到的是权威组装后的请求
        var sent = inner.StreamingMessages!;
        Assert.StartsWith("你是大肥鱼派发站的调度员", sent[0].Text);
        Assert.Equal("deepseek-flash", inner.StreamingOptions!.ModelId);
        Assert.Equal(2, sent.Count); // 仅 system+user，无历史
    }

    private static string DispatchEnvelope() => Envelope("dispatch", ("entities", ["电子大肥鱼"]));

    private static IReadOnlyList<ChatMessage> ValidMessages() =>
    [
        new(ChatRole.System, DispatchEnvelope()),
        new(ChatRole.User, "请为本次申请生成大肥鱼派发单"),
    ];

    /// <summary>按变量名拼出信封 JSON（变量名同时是任务模板变量名）。</summary>
    private static string Envelope(string task, params (string Name, string[] Keys)[] variables)
    {
        return JsonSerializer.Serialize(new
        {
            task,
            data = variables.Select(variable => new { name = variable.Name, keys = variable.Keys }),
        });
    }

    private static ChatMessage AssistantWithCalls(params (string Name, string Args, string CallId)[] calls) =>
        new(ChatRole.Assistant, calls.Select(c => (AIContent)new FunctionCallContent(c.CallId, c.Name, new Dictionary<string, object?>())).ToList());

    /// <summary>客户端侧工具声明桩：名字之外的内容（描述与 schema）都应被清单定义替换。</summary>
    private static AITool ClientTool(string name) =>
        new JsonFunctionDeclaration(name, "客户端自报的描述", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

    private static (RequestRewriteChatClient Client, CaptureClient Inner) Create()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifests", "demo", "manifest.json");
        var repo = new FileSystemManifestRepository(manifestPath);
        var rewriter = new RequestRewriter(repo);
        var inner = new CaptureClient();
        var client = new RequestRewriteChatClient(inner, rewriter);
        return (client, inner);
    }

    private sealed class CaptureClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? Messages { get; private set; }

        public ChatOptions? Options { get; private set; }

        public IReadOnlyList<ChatMessage>? StreamingMessages { get; private set; }

        public ChatOptions? StreamingOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingMessages = messages.ToList();
            StreamingOptions = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
