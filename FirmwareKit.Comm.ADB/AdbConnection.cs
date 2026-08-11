using FirmwareKit.Comm.ADB.Protocol;

namespace FirmwareKit.Comm.ADB;

/// <summary>
/// A single ADB connection over a transport: CNXN handshake, AUTH negotiation,
/// message dispatch loop, and stream multiplexing.
/// <para>单条基于传输层的 ADB 连接：CNXN 握手、AUTH 协商、消息分发循环、流复用。</para>
/// </summary>
public sealed class AdbConnection : IDisposable
{
    private readonly IAdbTransport _transport;
    private readonly AdbAuthentication _authentication;
    private readonly Dictionary<uint, AdbStream> _streams = new();
    private readonly object _lock = new();
    // Dedicated write lock: Send() is invoked both from the message-loop thread
    // (OKAY/CLSE replies) and from caller threads (OPEN/WRTE), and each message is
    // two transport writes (header, then payload). Concurrent writes interleave
    // bytes on the wire and corrupt framing. A separate lock (not _lock) is needed
    // because HandleWrite holds _lock while calling Send.
    // <para>专用写锁：Send() 既从消息循环线程（OKAY/CLSE 应答）也从调用方线程
    // （OPEN/WRTE）调用，每条消息是两次传输写，并发写会交错字节破坏帧。必须用独立锁，
    // 因为 HandleWrite 在持有 _lock 时调用 Send。</para>
    private readonly object _sendLock = new();
    // Signaled when the peer's CNXN banner arrives (handshake complete), so
    // callers can wait without polling.
    // <para>对端 CNXN 横幅到达（握手完成）时触发，调用方可无轮询等待。</para>
    private readonly ManualResetEventSlim _peerReady = new(false);
    private uint _nextLocalId = 1;
    private bool _connected;
    private bool _disposed;

    // AOSP adb_auth_host.cpp handle_auth(): the FIRST token is answered with a
    // signature; if the device rejects it (key not trusted, ro.adb.secure=1) it sends
    // another token, and the client must then advertise its public key so the user can
    // authorize it. Without this fallback the handshake loops signing forever.
    // <para>AOSP handle_auth()：首个令牌以签名应答；设备拒绝时再发令牌，客户端须改发
    // 公钥供用户授权。缺少该回退会导致握手无限循环签名。</para>
    private bool _authSignatureSent;

    /// <summary>Gets a value indicating whether the connection is established and authenticated.
    /// <para>获取连接是否已建立并通过认证。</para></summary>
    public bool IsConnected { get { lock (_lock) return _connected; } }

    /// <summary>Gets the protocol version reported by the peer.
    /// <para>获取对端报告的协议版本。</para></summary>
    public uint PeerVersion { get; private set; }

    /// <summary>Gets the maximum payload size reported by the peer.
    /// <para>获取对端报告的最大负载长度。</para></summary>
    public uint PeerMaxPayload { get; private set; }

    /// <summary>Gets the connection banner / system information sent by the peer.
    /// <para>获取对端发送的连接横幅 / 系统信息。</para></summary>
    public string? PeerBanner { get; private set; }

    /// <summary>Gets the features advertised by the peer (parsed from the banner).
    /// <para>获取对端宣告的特性（从横幅解析）。</para></summary>
    public IReadOnlyList<string> PeerFeatures { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Initializes a new ADB connection over the given transport with the given authentication identity.
    /// <para>使用给定的传输层与认证身份初始化新的 ADB 连接。</para>
    /// </summary>
    /// <param name="transport">The transport to communicate over. 通信传输层。</param>
    /// <param name="authentication">The RSA authentication identity. RSA 认证身份。</param>
    public AdbConnection(IAdbTransport transport, AdbAuthentication authentication)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    /// <summary>
    /// Connects: sends CNXN, drives the AUTH handshake, then starts the background
    /// message dispatch loop.
    /// <para>连接：发送 CNXN，驱动 AUTH 握手，然后启动后台消息分发循环。</para>
    /// </summary>
    public void Connect()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AdbConnection));
            if (_connected) return;
        }

        Send(AdbMessaging.BuildConnect());
        _connected = true;
        _authSignatureSent = false;

        Thread loop = new(MessageLoop) { IsBackground = true, Name = "ADB message loop" };
        loop.Start();
    }

    /// <summary>Opens a stream to a service (e.g. "shell:v2:...", "sync:", "reboot:").
    /// 打开到指定服务的流。</summary>
    public AdbStream OpenStream(string service)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        EnsureConnected();

        uint localId;
        AdbStream stream;
        lock (_lock)
        {
            localId = _nextLocalId++;
            stream = new AdbStream(this, localId);
            _streams[localId] = stream;
        }

        Send(AdbMessaging.BuildOpen(localId, service));
        return stream;
    }

    internal void WriteToStream(AdbStream stream, byte[] data)
    {
        EnsureConnected();

        // Block until the OPEN's OKAY arrives (RemoteId assigned) instead of
        // polling; latency drops from ~10 ms to sub-millisecond per write.
        // <para>阻塞等待 OPEN 的 OKAY 到达（RemoteId 赋值）而非轮询，
        // 每次写入延迟从约 10ms 降到亚毫秒级。</para>
        if (stream.RemoteId == 0)
        {
            stream.WaitReady(5000);
        }

        if (stream.RemoteId == 0)
        {
            throw new InvalidOperationException("Stream was not acknowledged by the peer (OKAY timeout).");
        }

        Send(AdbMessaging.BuildWrite(stream.LocalId, stream.RemoteId, data));
    }

    internal void CloseStream(AdbStream stream)
    {
        lock (_lock) { _streams.Remove(stream.LocalId); }
        Send(AdbMessaging.BuildClose(stream.LocalId, stream.RemoteId));
    }

    private void Send(AdbMessage message)
    {
        lock (_sendLock)
        {
            byte[] header = AdbMessaging.SerializeHeader(message);
            _transport.Write(header, header.Length);

            if (message.Payload is { Length: > 0 } payload)
            {
                _transport.Write(payload, payload.Length);
            }
        }
    }

    private AdbMessage ReadMessage()
    {
        byte[] header = _transport.Read(AdbProtocol.MessageHeaderSize);
        var (command, arg0, arg1, payloadLength, payloadCrc) = AdbMessaging.ParseHeader(header);

        if (payloadLength > AdbProtocol.MaxPayload)
        {
            throw new InvalidDataException($"ADB payload length {payloadLength} exceeds the protocol maximum.");
        }

        byte[]? payload = null;
        if (payloadLength > 0)
        {
            payload = _transport.Read((int)payloadLength);
            if (payloadCrc != 0)
            {
                AdbMessaging.VerifyCrc(payload, payloadCrc);
            }
        }

        return new AdbMessage(command, arg0, arg1, payload);
    }

    private void MessageLoop()
    {
        try
        {
            while (!_disposed)
            {
                AdbMessage message = ReadMessage();
                switch (message.Command)
                {
                    case AdbCommand.Auth: HandleAuth(message); break;
                    case AdbCommand.Cnxn: HandleConnect(message); break;
                    case AdbCommand.Open: HandleOpen(message); break;
                    case AdbCommand.Okay: HandleOkay(message); break;
                    case AdbCommand.Wrte: HandleWrite(message); break;
                    case AdbCommand.Clse: HandleClose(message); break;
                }
            }
        }
        catch (Exception ex)
        {
            FaultAllStreams(ex);
        }
    }

    private void HandleAuth(AdbMessage message)
    {
        switch ((AdbAuthType)message.Arg0)
        {
            case AdbAuthType.Token:
                if (!_authSignatureSent)
                {
                    byte[] token = message.Payload ?? Array.Empty<byte>();
                    byte[] signature = _authentication.SignToken(token);
                    _authSignatureSent = true;
                    Send(AdbMessaging.BuildAuth(AdbAuthType.Signature, signature));
                }
                else
                {
                    byte[] publicKey = _authentication.BuildPublicKeyPayload();
                    Send(AdbMessaging.BuildAuth(AdbAuthType.RSAPublicKey, publicKey));
                }
                break;

            case AdbAuthType.RSAPublicKey:
                byte[] explicitPublicKey = _authentication.BuildPublicKeyPayload();
                Send(AdbMessaging.BuildAuth(AdbAuthType.RSAPublicKey, explicitPublicKey));
                break;

            case AdbAuthType.Signature:
                break;
        }
    }

    private void HandleConnect(AdbMessage message)
    {
        PeerVersion = message.Arg0;
        PeerMaxPayload = message.Arg1;
        PeerBanner = message.PayloadAsString();
        PeerFeatures = ParseFeatures(PeerBanner);
        _peerReady.Set();
    }

    /// <summary>
    /// Blocks until the peer's CNXN banner arrives (handshake complete) or the timeout.
    /// <para>阻塞至对端 CNXN 横幅到达（握手完成）或超时。</para>
    /// </summary>
    /// <param name="timeoutMs">Maximum wait in milliseconds. 最大等待毫秒数。</param>
    /// <returns><c>true</c> if the handshake completed; <c>false</c> on timeout.
    /// 握手完成返回 <c>true</c>；超时返回 <c>false</c>。</returns>
    public bool WaitForPeer(int timeoutMs = 10000) => _peerReady.Wait(timeoutMs);

    private static IReadOnlyList<string> ParseFeatures(string? banner)
    {
        if (string.IsNullOrEmpty(banner)) return Array.Empty<string>();

        int idx = banner!.IndexOf("features=", StringComparison.Ordinal);
        if (idx < 0) return Array.Empty<string>();

        return banner.Substring(idx + "features=".Length).Trim()
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private void HandleOpen(AdbMessage message)
    {
        // Peer-initiated open: assign a local id; OKAY uses sender convention
        // (arg0 = our id, arg1 = peer id).
        AdbStream stream;
        lock (_lock)
        {
            stream = new AdbStream(this, _nextLocalId++);
            _streams[stream.LocalId] = stream;
        }

        Send(AdbMessaging.BuildOkay(stream.LocalId, message.Arg0));
    }

    private void HandleOkay(AdbMessage message)
    {
        // Peer messages carry arg0 = peer's local id (our remote id), arg1 = our local id.
        AdbStream? stream;
        lock (_lock)
        {
            _streams.TryGetValue(message.Arg1, out stream);
        }
        // Set outside the lock: this signals _ready which may unblock waiting writers.
        stream?.SetRemoteId(message.Arg0);
    }

    private void HandleWrite(AdbMessage message)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg1, out AdbStream? stream))
            {
                stream.EnqueueData(message.Payload ?? Array.Empty<byte>());
                Send(AdbMessaging.BuildOkay(message.Arg1, message.Arg0));
            }
        }
    }

    private void HandleClose(AdbMessage message)
    {
        AdbStream? stream = null;
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg1, out stream)) _streams.Remove(message.Arg1);
        }

        stream?.MarkClosed();
        Send(AdbMessaging.BuildClose(message.Arg1, message.Arg0));
    }

    private void FaultAllStreams(Exception ex)
    {
        AdbStream[] streams;
        lock (_lock)
        {
            streams = new List<AdbStream>(_streams.Values).ToArray();
            _streams.Clear();
            _connected = false;
        }

        foreach (AdbStream stream in streams) stream.MarkFaulted(ex);
    }

    private void EnsureConnected()
    {
        lock (_lock)
        {
            if (!_connected) throw new InvalidOperationException("ADB connection is not established.");
        }
    }

    /// <summary>Disconnects and releases all resources. 断开连接并释放所有资源。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            _connected = false;
            foreach (AdbStream stream in _streams.Values) stream.MarkClosed();
            _streams.Clear();
        }

        _transport.Dispose();
        _authentication.Dispose();
        _peerReady.Dispose();
        GC.SuppressFinalize(this);
    }
}
