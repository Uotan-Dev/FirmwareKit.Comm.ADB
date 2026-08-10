using System.Net.Sockets;

namespace FirmwareKit.Comm.ADB.Backend.Tcp;

/// <summary>
/// ADB transport over a TCP connection (adb connect host:port), used to reach
/// emulators and network-enabled devices that only expose a local network link.
/// <para>基于 TCP 连接的 ADB 传输层（adb connect host:port），用于连接仅暴露
/// 本地网络链接的模拟器与网络设备。</para>
/// </summary>
public sealed class AdbTcpTransport : IAdbTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private bool _disposed;

    /// <summary>
    /// Gets the remote host. 远端主机。
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the remote port. 远端端口。
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Initializes a new TCP transport and connects to the given host and port.
    /// <para>初始化新的 TCP 传输层并连接到给定主机与端口。</para>
    /// </summary>
    /// <param name="host">Host name or IP address. 主机名或 IP 地址。</param>
    /// <param name="port">TCP port of the adbd service. adbd 服务的 TCP 端口。</param>
    /// <param name="connectTimeoutMs">Connection timeout in milliseconds. 连接超时（毫秒）。</param>
    public AdbTcpTransport(string host, int port, int connectTimeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host must not be empty.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Host = host;
        Port = port;

        _client = new TcpClient { NoDelay = true };
        try
        {
            System.Threading.Tasks.Task connect = _client.ConnectAsync(host, port);
            if (!connect.Wait(connectTimeoutMs))
            {
                throw new TimeoutException($"Timed out connecting to ADB device at {host}:{port}.");
            }

            _stream = _client.GetStream();
            // Guard against a silent peer: surface a read failure instead of
            // blocking forever. 防止对端静默：读超时抛出异常而非永久阻塞。
            _stream.ReadTimeout = 60_000;
        }
        catch
        {
            _client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads exactly the requested number of bytes, blocking until available.
    /// <para>精确读取指定数量的字节，阻塞至数据可用。</para>
    /// </summary>
    public byte[] Read(int length)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AdbTcpTransport));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = _stream.Read(buffer, offset, length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("ADB TCP connection closed by the peer.");
            }

            offset += read;
        }

        return buffer;
    }

    /// <summary>
    /// Writes all bytes to the transport, returning the number of bytes written.
    /// <para>向传输层写入全部字节，返回实际写入的字节数。</para>
    /// </summary>
    public long Write(byte[] data, int length)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AdbTcpTransport));
        }

        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (length < 0 || length > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _stream.Write(data, 0, length);
        return length;
    }

    /// <summary>
    /// Releases the underlying TCP connection.
    /// <para>释放底层 TCP 连接。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stream?.Dispose();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
