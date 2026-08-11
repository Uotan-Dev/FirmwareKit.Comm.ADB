using System.Text;

namespace FirmwareKit.Comm.ADB.Protocol;

/// <summary>
/// Serializes and deserializes ADB wire messages (24-byte header + payload).
/// <para>序列化与反序列化 ADB 线上消息（24 字节头部 + 负载）。</para>
/// </summary>
public static class AdbMessaging
{
    /// <summary>Computes the ADB CRC (32-bit sum of payload bytes). ADB CRC（负载字节的 32 位累加和）。</summary>
    public static uint ComputeCrc(byte[]? payload)
    {
        if (payload is null || payload.Length == 0) return 0;
        uint crc = 0;
        foreach (byte b in payload) crc = (crc + b) & 0xFFFFFFFF;
        return crc;
    }

    /// <summary>
    /// Serializes header + payload into one buffer (for stream transports such as TCP).
    /// USB transports must use <see cref="SerializeHeader"/> + a separate payload write.
    /// <para>将头部与负载拼接为单个缓冲区（用于 TCP 等流式传输）。USB 传输须用
    /// <see cref="SerializeHeader"/> 并单独写负载。</para>
    /// </summary>
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
            if (message.Payload is { Length: > 0 }) bw.Write(message.Payload);
        }
        return packet;
    }

    /// <summary>
    /// Serializes only the 24-byte header. ADB-over-USB requires the header and payload
    /// as two separate bulk-OUT transfers: the gadget parses the header from the first
    /// packet before reading the declared payload; one combined transfer makes adbd drop
    /// the packet. The reference client sends them in two writes.
    /// <para>仅序列化 24 字节头部。ADB-over-USB 要求头部与负载作为两个独立 bulk-OUT
    /// 传输：gadget 先从首个包解析头部再按声明长度读负载；合并传输会使 adbd 丢包。</para>
    /// </summary>
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

    /// <summary>Parses a raw 24-byte header (without payload). 解析原始 24 字节头部。</summary>
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

    /// <summary>Verifies the payload CRC against the header value. 校验负载 CRC。</summary>
    public static void VerifyCrc(byte[]? payload, uint expectedCrc)
    {
        uint actual = ComputeCrc(payload);
        if (actual != expectedCrc)
        {
            throw new InvalidDataException($"ADB payload CRC mismatch: expected 0x{expectedCrc:X8}, got 0x{actual:X8}.");
        }
    }

    /// <summary>Builds the CNXN connect message with this client's version, max payload
    /// and feature banner. 构建 CNXN 连接消息（含版本、最大负载与特性横幅）。</summary>
    public static AdbMessage BuildConnect() =>
        new(AdbCommand.Cnxn, AdbProtocol.Version, AdbProtocol.MaxPayload,
            Encoding.UTF8.GetBytes(AdbProtocol.BuildConnectPayload()));

    /// <summary>Builds an AUTH message carrying the given payload.
    /// 构建携带指定负载的 AUTH 消息。</summary>
    public static AdbMessage BuildAuth(AdbAuthType type, byte[] data) =>
        new(AdbCommand.Auth, (uint)type, 0, data);

    /// <summary>Builds an OPEN message to start a service stream.
    /// 构建打开服务流的 OPEN 消息。</summary>
    public static AdbMessage BuildOpen(uint localId, string destination) =>
        new(AdbCommand.Open, localId, 0, Encoding.UTF8.GetBytes($"{destination}\0"));

    /// <summary>Builds a WRTE message carrying payload bytes to the peer stream.
    /// 构建向对端流发送负载字节的 WRTE 消息。</summary>
    public static AdbMessage BuildWrite(uint localId, uint remoteId, byte[] data) =>
        new(AdbCommand.Wrte, localId, remoteId, data);

    /// <summary>Builds a CLSE message to close a stream.
    /// 构建关闭流的 CLSE 消息。</summary>
    public static AdbMessage BuildClose(uint localId, uint remoteId) =>
        new(AdbCommand.Clse, localId, remoteId);

    /// <summary>Builds an OKAY acknowledgement for a stream.
    /// 构建流的 OKAY 确认消息。</summary>
    public static AdbMessage BuildOkay(uint localId, uint remoteId) =>
        new(AdbCommand.Okay, localId, remoteId);
}
