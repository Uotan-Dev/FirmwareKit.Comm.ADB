namespace FirmwareKit.Comm.ADB.Protocol;

/// <summary>
/// ADB wire commands, byte-swapped AOSP identifiers.
/// <para>ADB 线上的命令标识（AOSP 字节序反转后的值）。</para>
/// </summary>
public enum AdbCommand : uint
{
    /// <summary>
    /// Authentication message (TOKEN / SIGNATURE / RSAPUBLICKEY).
    /// <para>认证消息（TOKEN / SIGNATURE / RSAPUBLICKEY）。</para>
    /// </summary>
    Auth = 0x48545541,

    /// <summary>
    /// Close stream.
    /// <para>关闭流。</para>
    /// </summary>
    Clse = 0x45534C43,

    /// <summary>
    /// Connect (version / maxdata handshake).
    /// <para>连接握手（版本 / 最大数据长度）。</para>
    /// </summary>
    Cnxn = 0x4E584E43,

    /// <summary>
    /// Acknowledge / ready.
    /// <para>确认 / 就绪。</para>
    /// </summary>
    Okay = 0x59414B4F,

    /// <summary>
    /// Open a new stream to a service.
    /// <para>打开到某服务的新流。</para>
    /// </summary>
    Open = 0x4E45504F,

    /// <summary>
    /// Sync (file transfer) stream.
    /// <para>同步（文件传输）流。</para>
    /// </summary>
    Sync = 0x434E5953,

    /// <summary>
    /// Write payload to a stream.
    /// <para>向流写入数据。</para>
    /// </summary>
    Wrte = 0x45545257,
}
