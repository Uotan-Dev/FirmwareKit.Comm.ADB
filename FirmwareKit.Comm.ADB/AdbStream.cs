namespace FirmwareKit.Comm.ADB;

/// <summary>
/// A single ADB service stream multiplexed over the connection
/// (OPEN / OKAY / WRTE / CLSE state machine).
/// <para>在连接上复用的单个 ADB 服务流（OPEN / OKAY / WRTE / CLSE 状态机）。</para>
/// </summary>
public sealed class AdbStream : IDisposable
{
    private readonly AdbConnection _connection;
    private readonly object _lock = new();
    private readonly Queue<byte[]> _incoming = new();
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private bool _closed;
    private bool _faulted;
    private Exception? _fault;

    /// <summary>
    /// Gets the local stream identifier.
    /// <para>获取本地流标识。</para>
    /// </summary>
    public uint LocalId { get; }

    /// <summary>
    /// Gets the remote stream identifier (0 until the peer acknowledges).
    /// <para>获取远端流标识（在对方确认前为 0）。</para>
    /// </summary>
    public uint RemoteId { get; internal set; }

    /// <summary>
    /// Gets whether the stream has been closed.
    /// <para>获取流是否已关闭。</para>
    /// </summary>
    public bool IsClosed
    {
        get { lock (_lock) return _closed; }
    }

    /// <summary>
    /// Gets whether the stream is in a faulted state.
    /// <para>获取流是否处于故障状态。</para>
    /// </summary>
    public bool IsFaulted
    {
        get { lock (_lock) return _faulted; }
    }

    /// <summary>
    /// Gets the fault that caused the stream to fail, if any.
    /// <para>获取导致流失败的异常（若有）。</para>
    /// </summary>
    public Exception? Fault
    {
        get { lock (_lock) return _fault; }
    }

    internal AdbStream(AdbConnection connection, uint localId)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        LocalId = localId;
    }

    /// <summary>
    /// Enqueues a payload received from the peer.
    /// <para>将对方发来的负载入队。</para>
    /// </summary>
    internal void EnqueueData(byte[] payload)
    {
        lock (_lock)
        {
            if (_closed) return;
            _incoming.Enqueue(payload);
        }

        _dataAvailable.Set();
    }

    /// <summary>
    /// Marks the stream as closed by the peer.
    /// <para>标记流已被对方关闭。</para>
    /// </summary>
    internal void MarkClosed()
    {
        lock (_lock)
        {
            _closed = true;
        }

        _dataAvailable.Set();
    }

    /// <summary>
    /// Marks the stream as faulted with the given exception.
    /// <para>使用给定异常将流标记为故障。</para>
    /// </summary>
    internal void MarkFaulted(Exception ex)
    {
        lock (_lock)
        {
            _faulted = true;
            _fault = ex;
            _closed = true;
        }

        _dataAvailable.Set();
    }

    /// <summary>
    /// Writes data to the peer and waits for the OKAY acknowledgement.
    /// <para>向对方写入数据并等待 OKAY 确认。</para>
    /// </summary>
    public void Write(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (IsClosed) throw new InvalidOperationException("Stream is closed.");

        _connection.WriteToStream(this, data);
    }

    /// <summary>
    /// Reads the next chunk of data from the stream, blocking until data arrives
    /// or the stream closes. Returns <c>null</c> when the stream is closed.
    /// <para>读取流的下一块数据，阻塞至数据到达或流关闭。流关闭时返回 <c>null</c>。</para>
    /// </summary>
    public byte[]? Read()
    {
        return Read(Timeout.Infinite);
    }

    /// <summary>
    /// Reads the next chunk of data from the stream, waiting at most
    /// <paramref name="timeoutMs"/> milliseconds. Returns <c>null</c> when the
    /// stream is closed, or when the timeout elapses with no data (in which case
    /// <see cref="IsClosed"/> is still <c>false</c>).
    /// <para>读取流的下一块数据，最多等待 <paramref name="timeoutMs"/> 毫秒。
    /// 流关闭或超时无数据时返回 <c>null</c>（超时情况下 <see cref="IsClosed"/>
    /// 仍为 <c>false</c>）。</para>
    /// </summary>
    public byte[]? Read(int timeoutMs)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_incoming.Count > 0)
                {
                    return _incoming.Dequeue();
                }

                if (_closed)
                {
                    return null;
                }
            }

            if (!_dataAvailable.Wait(timeoutMs))
            {
                // Timeout elapsed without data and without a close signal.
                return null;
            }

            _dataAvailable.Reset();
        }
    }

    /// <summary>
    /// Closes the stream locally and notifies the peer.
    /// <para>在本地关闭流并通知对方。</para>
    /// </summary>
    public void Close()
    {
        bool shouldNotify;
        lock (_lock)
        {
            shouldNotify = !_closed;
            _closed = true;
        }

        if (shouldNotify)
        {
            try
            {
                _connection.CloseStream(this);
            }
            catch
            {
                // The connection may already be gone.
            }
        }

        _dataAvailable.Set();
    }

    /// <summary>
    /// Disposes the stream.
    /// <para>释放流。</para>
    /// </summary>
    public void Dispose()
    {
        Close();
        _dataAvailable.Dispose();
        GC.SuppressFinalize(this);
    }
}
