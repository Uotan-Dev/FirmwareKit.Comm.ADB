namespace FirmwareKit.Comm.ADB;

/// <summary>
/// A single ADB service stream multiplexed over one connection, implementing the
/// OPEN / OKAY / WRTE / CLSE state machine.
/// <para>在单条连接上复用的 ADB 服务流，实现 OPEN / OKAY / WRTE / CLSE 状态机。</para>
/// </summary>
public sealed class AdbStream : IDisposable
{
    private readonly AdbConnection _connection;
    private readonly object _lock = new();
    private readonly Queue<byte[]> _incoming = new();
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    // Signaled when the peer's OKAY arrives (RemoteId assigned) or the stream
    // closes/faults, so Write() can block without polling.
    // <para>对端 OKAY 到达（RemoteId 赋值）或流关闭/故障时触发，使 Write() 可
    // 阻塞等待而无需轮询。</para>
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _closed;
    private bool _faulted;
    private Exception? _fault;

    /// <summary>Gets the local stream identifier.
    /// <para>获取本地流标识。</para></summary>
    public uint LocalId { get; }

    /// <summary>Gets the remote stream identifier, or 0 until the peer acknowledges OPEN.
    /// <para>获取远端流标识；在对端确认 OPEN 之前为 0。</para></summary>
    public uint RemoteId { get; internal set; }

    /// <summary>
    /// Sets the remote id and signals that the stream is ready for writes.
    /// <para>设置远端 ID 并触发可写信号。</para>
    /// </summary>
    internal void SetRemoteId(uint remoteId)
    {
        RemoteId = remoteId;
        _ready.Set();
    }

    /// <summary>
    /// Blocks until the stream is ready (OKAY received) or the timeout elapses.
    /// <para>阻塞至流就绪（收到 OKAY）或超时。</para>
    /// </summary>
    internal bool WaitReady(int timeoutMs) => _ready.Wait(timeoutMs);

    /// <summary>Gets a value indicating whether the stream has been closed.
    /// <para>获取流是否已关闭。</para></summary>
    public bool IsClosed { get { lock (_lock) return _closed; } }

    /// <summary>Gets a value indicating whether the stream is in a faulted state.
    /// <para>获取流是否处于故障状态。</para></summary>
    public bool IsFaulted { get { lock (_lock) return _faulted; } }

    /// <summary>Gets the exception that faulted the stream, or <c>null</c>.
    /// <para>获取导致流故障的异常；无故障时为 <c>null</c>。</para></summary>
    public Exception? Fault { get { lock (_lock) return _fault; } }

    internal AdbStream(AdbConnection connection, uint localId)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        LocalId = localId;
    }

    internal void EnqueueData(byte[] payload)
    {
        lock (_lock)
        {
            if (_closed) return;
            _incoming.Enqueue(payload);
        }

        _dataAvailable.Set();
    }

    internal void MarkClosed()
    {
        lock (_lock) { _closed = true; }
        _ready.Set();
        _dataAvailable.Set();
    }

    internal void MarkFaulted(Exception ex)
    {
        lock (_lock)
        {
            _faulted = true;
            _fault = ex;
            _closed = true;
        }

        _ready.Set();
        _dataAvailable.Set();
    }

    /// <summary>
    /// Writes data to the peer. Blocks until the OPEN handshake's OKAY acknowledges the stream.
    /// <para>向对端写入数据；会阻塞至 OPEN 握手的 OKAY 确认到达。</para>
    /// </summary>
    /// <param name="data">The payload bytes to write. 待写入的负载字节。</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">The stream is closed or was not acknowledged.</exception>
    public void Write(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (IsClosed) throw new InvalidOperationException("Stream is closed.");

        _connection.WriteToStream(this, data);
    }

    /// <summary>
    /// Reads the next chunk, blocking indefinitely until data arrives or the stream closes.
    /// <para>读取下一块数据，无限期阻塞至数据到达或流关闭。</para>
    /// </summary>
    /// <returns>The next chunk, or <c>null</c> when the stream is closed.
    /// 下一块数据；流关闭时返回 <c>null</c>。</returns>
    public byte[]? Read() => Read(Timeout.Infinite);

    /// <summary>
    /// Reads the next chunk, waiting at most <paramref name="timeoutMs"/> milliseconds.
    /// <para>读取下一块数据，最多等待 <paramref name="timeoutMs"/> 毫秒。</para>
    /// </summary>
    /// <param name="timeoutMs">Maximum wait in milliseconds. 最大等待毫秒数。</param>
    /// <returns>The next chunk, or <c>null</c> when the stream is closed or the wait times out.
    /// 下一块数据；流关闭或超时无数据时返回 <c>null</c>。</returns>
    public byte[]? Read(int timeoutMs)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_incoming.Count > 0) return _incoming.Dequeue();
                if (_closed) return null;
            }

            if (!_dataAvailable.Wait(timeoutMs)) return null;
            _dataAvailable.Reset();
        }
    }

    /// <summary>
    /// Closes the stream locally and notifies the peer with a CLSE message.
    /// <para>在本地关闭流，并向对端发送 CLSE 消息。</para>
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
            try { _connection.CloseStream(this); }
            catch { /* connection may already be gone */ }
        }

        _dataAvailable.Set();
    }

    /// <summary>Releases all resources held by the stream.
    /// <para>释放流占用的所有资源。</para></summary>
    public void Dispose()
    {
        Close();
        _dataAvailable.Dispose();
        GC.SuppressFinalize(this);
    }
}
