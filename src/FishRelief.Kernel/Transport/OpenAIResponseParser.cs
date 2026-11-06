using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

#pragma warning disable OPENAI001 // AsChatResponse / AsChatResponseUpdatesAsync 处于实验阶段

namespace FishRelief.Kernel.Transport;

/// <summary>
/// 上游响应解析（路线 A）：OpenAI SDK 解析响应体为强类型对象，
/// 再经官方适配层转换为 MEAI 类型（内部已挂 RawRepresentation，并处理 DeepSeek 类上游的非标 reasoning 字段）。
/// 字段保真上限 = OpenAI SDK 模型面。
/// </summary>
internal static class OpenAIResponseParser
{
    public static ChatResponse ParseCompletion(ReadOnlyMemory<byte> body) =>
        (ModelReaderWriter.Read<ChatCompletion>(BinaryData.FromBytes(body))
            ?? throw new InvalidOperationException("上游响应解析为空")).AsChatResponse();

    public static StreamingChatCompletionUpdate ParseStreamingUpdate(ReadOnlyMemory<byte> body) =>
        ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromBytes(body))
            ?? throw new InvalidOperationException("上游流式增量解析为空");

    public static IAsyncEnumerable<ChatResponseUpdate> ConvertUpdatesAsync(
        IAsyncEnumerable<StreamingChatCompletionUpdate> updates) =>
        updates.AsChatResponseUpdatesAsync();
}
