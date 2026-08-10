namespace FirmwareKit.Comm.ADB;

/// <summary>
/// Defines the low-level transport used by the ADB connection (read/write primitives).
/// <para>定义 ADB 连接使用的底层传输层（读 / 写原语）。</para>
/// </summary>
public interface IAdbTransport : IDisposable
{
    /// <summary>
    /// Reads exactly the requested number of bytes from the transport.
    /// <para>从传输层精确读取指定数量的字节。</para>
    /// </summary>
    /// <param name="length">Number of bytes to read. 要读取的字节数。</param>
    /// <returns>The bytes read. 读取到的字节。</returns>
    byte[] Read(int length);

    /// <summary>
    /// Writes all bytes to the transport, returning the number of bytes written.
    /// <para>向传输层写入全部字节，返回实际写入的字节数。</para>
    /// </summary>
    long Write(byte[] data, int length);
}
