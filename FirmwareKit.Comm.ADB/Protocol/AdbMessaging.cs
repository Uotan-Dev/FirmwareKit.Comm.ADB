using System.Text;

namespace FirmwareKit.Comm.ADB.Protocol;

/// <summary>
/// Serializes and deserializes ADB wire messages (24-byte header + payload).
/// <para>序列化与反序列化 ADB 线上消息（24 字节头部 + 负载）。</para>
/// </summary>
public static class AdbMessaging
{
    /// <summary>
    /// Computes the ADB CRC (simple 32-bit sum of payload bytes).
    /// <para>计算 ADB CRC（负载字节的简单 32 位累加和）。</para>
    /// </summary>
    public static uint ComputeCrc(byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
        {
            return 0;
        }

        uint crc = 0;
        foreach (byte b in payload)
        {
            crc = (crc + b) & 0xFFFFFFFF;
        }

        return crc;
    }

    /// <summary>
    /// Serializes a message into its wire representation (header, then payload).
    /// <para>将消息序列化为线上表示（头部，随后负载）。</para>
    /// </summary>
    /// <remarks>
    /// Concatenates header and payload into one buffer. USB transports must send them
    /// as two separate bulk transfers (use <see cref="SerializeHeader"/>); this is for
    /// stream transports such as TCP.
    /// <para>将头部与负载拼接为单个缓冲区。USB 传输必须将两者作为两个独立的批量传输
    /// 发送（用 <see cref="SerializeHeader"/>）；本方法用于 TCP 等流式传输。</para>
    /// </remarks>
    public static byte[] Serialize(AdbMessage message)
    {
        uint command = (uint)message.Command;
        uint payloadLength = message.Payload is null ? 0u : (uint)message.Payload.Length;
        uint payloadCrc = ComputeCrc(message.Payload);

        byte[] packet = new byte[AdbProtocol.MessageHeaderSize + (message.Payload?.Length ?? 0)];

        using (MemoryStream ms = new(packet))
        using (BinaryWriter bw = new(ms))
        {
            bw.Write(command);
            bw.Write(message.Arg0);
            bw.Write(message.Arg1);
            bw.Write(payloadLength);
            bw.Write(payloadCrc);
            bw.Write(command ^ 0xFFFFFFFF);

            if (message.Payload is { Length: > 0 })
            {
                bw.Write(message.Payload);
            }
        }

        return packet;
    }

    /// <summary>
    /// Serializes only the 24-byte message header.
    /// <para>仅序列化 24 字节的消息头部。</para>
    /// </summary>
    /// <remarks>
    /// ADB-over-USB requires the 24-byte header and payload as two separate bulk-OUT
    /// transfers: the kernel gadget parses the header from the first packet before reading
    /// the declared payload length. One combined transfer makes adbd drop the packet and
    /// never reply. The reference client sends them in two writes.
    /// <para>ADB-over-USB 要求 24 字节头部与负载作为两个独立的 bulk-OUT 传输：内核
    /// gadget 先从第一个包解析头部，再按其声明的长度读取负载。合并传输会使 adbd
    /// 丢包且永不回复。参考客户端分两次写入。</para>
    /// </remarks>
    public static byte[] SerializeHeader(AdbMessage message)
    {
        uint command = (uint)message.Command;
        uint payloadLength = message.Payload is null ? 0u : (uint)message.Payload.Length;
        uint payloadCrc = ComputeCrc(message.Payload);

        byte[] header = new byte[AdbProtocol.MessageHeaderSize];
        using (MemoryStream ms = new(header))
        using (BinaryWriter bw = new(ms))
        {
            bw.Write(command);
            bw.Write(message.Arg0);
            bw.Write(message.Arg1);
            bw.Write(payloadLength);
            bw.Write(payloadCrc);
            bw.Write(command ^ 0xFFFFFFFF);
        }

        return header;
    }

    /// <summary>
    /// Parses a raw 24-byte header into header fields (without payload).
    /// <para>将原始 24 字节头部解析为头部字段（不含负载）。</para>
    /// </summary>
    public static (AdbCommand Command, uint Arg0, uint Arg1, uint PayloadLength, uint PayloadCrc) ParseHeader(byte[] header)
    {
        if (header is null || header.Length != AdbProtocol.MessageHeaderSize)
        {
            throw new InvalidDataException($"Invalid ADB header size: {header?.Length}");
        }

        using MemoryStream ms = new(header);
        using BinaryReader br = new(ms);

        uint command = br.ReadUInt32();
        uint arg0 = br.ReadUInt32();
        uint arg1 = br.ReadUInt32();
        uint payloadLength = br.ReadUInt32();
        uint payloadCrc = br.ReadUInt32();
        uint magic = br.ReadUInt32();

        if ((command ^ 0xFFFFFFFF) != magic)
        {
            throw new InvalidDataException("Invalid ADB message magic!");
        }

        return ((AdbCommand)command, arg0, arg1, payloadLength, payloadCrc);
    }

    /// <summary>
    /// Verifies the payload CRC against the value carried in the header.
    /// <para>校验负载 CRC 是否与头部携带的值一致。</para>
    /// </summary>
    public static void VerifyCrc(byte[]? payload, uint expectedCrc)
    {
        uint actual = ComputeCrc(payload);
        if (actual != expectedCrc)
        {
            throw new InvalidDataException($"ADB payload CRC mismatch: expected 0x{expectedCrc:X8}, got 0x{actual:X8}.");
        }
    }

    /// <summary>
    /// Builds a CNXN (connect) message.
    /// <para>构建 CNXN（连接）消息。</para>
    /// </summary>
    public static AdbMessage BuildConnect() =>
        new(AdbCommand.Cnxn, AdbProtocol.Version, AdbProtocol.MaxPayload,
            Encoding.UTF8.GetBytes(AdbProtocol.BuildConnectPayload()));

    /// <summary>
    /// Builds an AUTH message of the given type.
    /// <para>构建指定类型的 AUTH 消息。</para>
    /// </summary>
    public static AdbMessage BuildAuth(AdbAuthType type, byte[] data) =>
        new(AdbCommand.Auth, (uint)type, 0, data);

    /// <summary>
    /// Builds an OPEN message for the given local id and destination service.
    /// <para>为给定的本地 ID 与目标服务构建 OPEN 消息。</para>
    /// </summary>
    public static AdbMessage BuildOpen(uint localId, string destination) =>
        new(AdbCommand.Open, localId, 0, Encoding.UTF8.GetBytes($"{destination}\0"));

    /// <summary>
    /// Builds a WRTE (write) message.
    /// <para>构建 WRTE（写入）消息。</para>
    /// </summary>
    public static AdbMessage BuildWrite(uint localId, uint remoteId, byte[] data) =>
        new(AdbCommand.Wrte, localId, remoteId, data);

    /// <summary>
    /// Builds a CLSE (close) message.
    /// <para>构建 CLSE（关闭）消息。</para>
    /// </summary>
    public static AdbMessage BuildClose(uint localId, uint remoteId) =>
        new(AdbCommand.Clse, localId, remoteId);

    /// <summary>
    /// Builds an OKAY (ready / acknowledgement) message.
    /// <para>构建 OKAY（就绪 / 确认）消息。</para>
    /// </summary>
    public static AdbMessage BuildOkay(uint localId, uint remoteId) =>
        new(AdbCommand.Okay, localId, remoteId);
}
