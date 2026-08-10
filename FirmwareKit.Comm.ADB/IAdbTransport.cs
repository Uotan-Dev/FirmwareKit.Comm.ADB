namespace FirmwareKit.Comm.ADB;

/// <summary>
/// Low-level transport read/write primitives used by the ADB connection.
/// <para>ADB 连接使用的底层传输层读 / 写原语。</para>
/// </summary>
public interface IAdbTransport : IDisposable
{
    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes from the transport, blocking until available.
    /// <para>从传输层精确读取 <paramref name="length"/> 字节，阻塞至数据可用。</para>
    /// </summary>
    /// <param name="length">Number of bytes to read. 要读取的字节数。</param>
    /// <returns>The bytes read. 读取到的字节。</returns>
    byte[] Read(int length);

    /// <summary>
    /// Writes <paramref name="length"/> bytes from <paramref name="data"/> to the transport.
    /// <para>将 <paramref name="data"/> 中的 <paramref name="length"/> 字节写入传输层。</para>
    /// </summary>
    /// <param name="data">The buffer to write. 待写入的缓冲区。</param>
    /// <param name="length">Number of bytes to write. 要写入的字节数。</param>
    /// <returns>The number of bytes written. 实际写入的字节数。</returns>
    long Write(byte[] data, int length);
}
