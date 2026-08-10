using System.Text;

namespace FirmwareKit.Comm.ADB.Protocol;

/// <summary>
/// Represents a single ADB wire message: a fixed 24-byte header plus an optional payload.
/// <para>表示单条 ADB 线上消息：固定的 24 字节头部加可选负载。</para>
/// </summary>
public sealed class AdbMessage
{
    /// <summary>
    /// Gets the command identifier.
    /// <para>获取命令标识。</para>
    /// </summary>
    public AdbCommand Command { get; }

    /// <summary>
    /// Gets the first argument (local stream id / auth type / version).
    /// <para>获取第一个参数（本地流 ID / 认证类型 / 版本）。</para>
    /// </summary>
    public uint Arg0 { get; }

    /// <summary>
    /// Gets the second argument (remote stream id / max payload).
    /// <para>获取第二个参数（远端流 ID / 最大负载）。</para>
    /// </summary>
    public uint Arg1 { get; }

    /// <summary>
    /// Gets the payload, or <c>null</c> when the message has no payload.
    /// <para>获取负载；消息无负载时为 <c>null</c>。</para>
    /// </summary>
    public byte[]? Payload { get; }

    /// <summary>
    /// Initializes a new ADB message.
    /// <para>初始化一条新的 ADB 消息。</para>
    /// </summary>
    public AdbMessage(AdbCommand command, uint arg0, uint arg1, byte[]? payload = null)
    {
        Command = command;
        Arg0 = arg0;
        Arg1 = arg1;
        Payload = payload;
    }

    /// <summary>
    /// Returns the payload decoded as UTF-8 text (empty string when no payload).
    /// <para>将负载按 UTF-8 解码为文本返回（无负载时为空字符串）。</para>
    /// </summary>
    public string PayloadAsString() =>
        Payload is { Length: > 0 } ? Encoding.UTF8.GetString(Payload) : string.Empty;
}
