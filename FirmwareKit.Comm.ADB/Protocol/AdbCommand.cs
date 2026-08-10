namespace FirmwareKit.Comm.ADB.Protocol;

/// <summary>
/// ADB wire commands (AOSP identifiers, byte-swapped for little-endian wire order).
/// <para>ADB 线上命令标识（AOSP 标识符，按小端序字节反转）。</para>
/// </summary>
public enum AdbCommand : uint
{
    /// <summary>Authentication (TOKEN / SIGNATURE / RSAPUBLICKEY). 认证消息。</summary>
    Auth = 0x48545541,
    /// <summary>Close stream. 关闭流。</summary>
    Clse = 0x45534C43,
    /// <summary>Connect handshake (version / maxdata). 连接握手。</summary>
    Cnxn = 0x4E584E43,
    /// <summary>Acknowledge / ready. 确认 / 就绪。</summary>
    Okay = 0x59414B4F,
    /// <summary>Open a stream to a service. 打开到服务的流。</summary>
    Open = 0x4E45504F,
    /// <summary>Sync (file transfer) stream. 同步（文件传输）流。</summary>
    Sync = 0x434E5953,
    /// <summary>Write payload to a stream. 向流写入负载。</summary>
    Wrte = 0x45545257,
}
