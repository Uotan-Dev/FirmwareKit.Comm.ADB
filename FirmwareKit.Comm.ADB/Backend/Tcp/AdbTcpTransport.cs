using System.Net.Sockets;

namespace FirmwareKit.Comm.ADB.Backend.Tcp;

/// <summary>
/// ADB transport over a TCP connection (adb connect host:port).
/// <para>基于 TCP 连接的 ADB 传输层（adb connect host:port）。</para>
/// </summary>
public sealed class AdbTcpTransport : IAdbTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private bool _disposed;

    /// <summary>Remote host. 远端主机。</summary>
    public string Host { get; }

    /// <summary>Remote port. 远端端口。</summary>
    public int Port { get; }

    /// <summary>Initializes a new TCP transport and connects. 初始化新的 TCP 传输层并连接。</summary>
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
            // Surface a read failure instead of blocking forever on a silent peer.
            _stream.ReadTimeout = 60_000;
        }
        catch
        {
            _client.Dispose();
            throw;
        }
    }

    /// <summary>Reads exactly <paramref name="length"/> bytes, blocking until available.
    /// 精确读取指定字节数，阻塞至数据可用。</summary>
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

    /// <summary>Writes all bytes, returning the count written. 写入全部字节，返回实际写入数。</summary>
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

    /// <summary>Releases the underlying TCP connection.
    /// <para>释放底层 TCP 连接。</para></summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stream?.Dispose();
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
