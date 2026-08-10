using FirmwareKit.Comm.ADB.Protocol;

namespace FirmwareKit.Comm.ADB;

/// <summary>
/// Manages a single ADB connection over a transport: CNXN handshake,
/// AUTH negotiation, message dispatch loop, and stream multiplexing.
/// <para>管理单条基于传输层的 ADB 连接：CNXN 握手、AUTH 协商、
/// 消息分发循环以及流复用。</para>
/// </summary>
public sealed class AdbConnection : IDisposable
{
    private readonly IAdbTransport _transport;
    private readonly AdbAuthentication _authentication;
    private readonly Dictionary<uint, AdbStream> _streams = new();
    private readonly object _lock = new();
    // Dedicated write lock: Send() is invoked both from the message-loop thread
    // (OKAY/CLSE replies) and from caller threads (OPEN/WRTE), and each message
    // is TWO transport writes (header, then payload). Without serializing them,
    // concurrent streams interleave bytes on the wire and corrupt framing. A
    // separate lock (not _lock) is required because HandleWrite holds _lock while
    // calling Send.
    // <para>专用写锁：Send() 既从消息循环线程（OKAY/CLSE 应答）调用，也从调用方
    // 线程（OPEN/WRTE）调用，而每条消息是两次传输写（头部、随后负载）。若不串行化，
    // 并发流会在链路上交错字节并破坏帧结构。必须使用独立锁（不能用 _lock），因为
    // HandleWrite 在持有 _lock 时调用 Send。</para>
    private readonly object _sendLock = new();
    private uint _nextLocalId = 1;
    private bool _connected;
    private bool _disposed;

    // AOSP adb_auth_host.cpp handle_auth(): the FIRST token is answered with a
    // signature; if the device rejects it (key not yet trusted, ro.adb.secure=1)
    // it sends another token, and the client must then advertise its public key
    // so the user can authorize it. Without this fallback the handshake loops
    // signing forever and times out.
    // <para>AOSP adb_auth_host.cpp handle_auth()：首个令牌用签名应答；若设备拒绝
    // （密钥尚未被信任，ro.adb.secure=1）会再次下发令牌，此时客户端必须改为发送
    // 公钥以便用户授权。缺少该回退时握手会一直签名直至超时。</para>
    private bool _authSignatureSent;

    /// <summary>
    /// Gets whether the connection is established and authenticated.
    /// <para>获取连接是否已建立并通过认证。</para>
    /// </summary>
    public bool IsConnected
    {
        get { lock (_lock) return _connected; }
    }

    /// <summary>
    /// Gets the protocol version reported by the peer.
    /// <para>获取对方报告的协议版本。</para>
    /// </summary>
    public uint PeerVersion { get; private set; }

    /// <summary>
    /// Gets the maximum payload size reported by the peer.
    /// <para>获取对方报告的最大负载长度。</para>
    /// </summary>
    public uint PeerMaxPayload { get; private set; }

    /// <summary>
    /// Gets the connection banner / system information sent by the peer.
    /// <para>获取对方发送的连接横幅 / 系统信息。</para>
    /// </summary>
    public string? PeerBanner { get; private set; }

    /// <summary>
    /// Gets the features advertised by the peer (parsed from the banner).
    /// <para>获取对方宣告的特性（从横幅解析）。</para>
    /// </summary>
    public IReadOnlyList<string> PeerFeatures { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Initializes a new ADB connection over the given transport with the given
    /// authentication identity.
    /// <para>使用给定的传输层与认证身份初始化新的 ADB 连接。</para>
    /// </summary>
    public AdbConnection(IAdbTransport transport, AdbAuthentication authentication)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    /// <summary>
    /// Connects to the peer: sends CNXN, drives the AUTH handshake, then enters
    /// the message dispatch loop on a background thread.
    /// <para>连接对方：发送 CNXN，驱动 AUTH 握手，然后进入后台消息分发循环。</para>
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

    /// <summary>
    /// Opens a stream to the given service (e.g. "shell:v2:...", "sync:", "reboot:").
    /// <para>打开到给定服务（如 "shell:v2:..."、"sync:"、"reboot:"）的流。</para>
    /// </summary>
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

    /// <summary>
    /// Writes data to a stream and waits for the OKAY acknowledgement.
    /// <para>向流写入数据并等待 OKAY 确认。</para>
    /// </summary>
    internal void WriteToStream(AdbStream stream, byte[] data)
    {
        EnsureConnected();

        // The peer routes WRTE messages by the remote id (arg1), which is only
        // known once the OKAY acknowledgement of the OPEN arrives; wait for it
        // like the reference client does.
        // <para>对端按远端 ID（arg1）路由 WRTE 消息，该 ID 仅在 OPEN 的
        // OKAY 确认后可知；与参考客户端一致，等待其到达。</para>
        int waited = 0;
        while (stream.RemoteId == 0 && !stream.IsClosed && waited < 500)
        {
            Thread.Sleep(10);
            waited++;
        }

        if (stream.RemoteId == 0)
        {
            throw new InvalidOperationException("Stream was not acknowledged by the peer (OKAY timeout).");
        }

        Send(AdbMessaging.BuildWrite(stream.LocalId, stream.RemoteId, data));
    }

    /// <summary>
    /// Closes a stream locally, notifying the peer with CLSE.
    /// <para>在本地关闭流，并向对方发送 CLSE 通知。</para>
    /// </summary>
    internal void CloseStream(AdbStream stream)
    {
        lock (_lock)
        {
            _streams.Remove(stream.LocalId);
        }

        Send(AdbMessaging.BuildClose(stream.LocalId, stream.RemoteId));
    }

    /// <summary>
    /// Sends a message over the transport. The 24-byte header and the payload are
    /// written as two separate transfers so USB ADB gadgets parse the packet correctly
    /// (see <see cref="AdbMessaging.SerializeHeader"/>); stream transports such as TCP
    /// are unaffected by the split.
    /// <para>通过传输层发送消息。24 字节头部与负载作为两次独立传输写入，以确保 USB ADB
    /// gadget 正确解析数据包（见 <see cref="AdbMessaging.SerializeHeader"/>）；TCP 等
    /// 流式传输不受该拆分影响。</para>
    /// </summary>
    private void Send(AdbMessage message)
    {
        // Serialize the header + payload as one atomic pair of transport writes.
        // The message loop (OKAY/CLSE replies) and caller threads (OPEN/WRTE) all
        // converge here; see _sendLock.
        // <para>将头部 + 负载作为一对原子的传输写串行化。消息循环（OKAY/CLSE 应答）
        // 与调用方线程（OPEN/WRTE）都汇聚于此；见 _sendLock。</para>
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

    /// <summary>
    /// Reads one message from the transport, verifying the header and CRC.
    /// <para>从传输层读取一条消息，校验头部与 CRC。</para>
    /// </summary>
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
            // Modern adbd sends data_check=0 even for non-empty payloads and the
            // reference client accepts that; only verify when the peer actually
            // provided a checksum (legacy devices still do).
            // <para>现代 adbd 即使负载非空也发送 data_check=0，参考客户端同样接受；
            // 仅在对端确实提供了校验和（旧设备仍如此）时进行校验。</para>
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
                    case AdbCommand.Auth:
                        HandleAuth(message);
                        break;
                    case AdbCommand.Cnxn:
                        HandleConnect(message);
                        break;
                    case AdbCommand.Open:
                        HandleOpen(message);
                        break;
                    case AdbCommand.Okay:
                        HandleOkay(message);
                        break;
                    case AdbCommand.Wrte:
                        HandleWrite(message);
                        break;
                    case AdbCommand.Clse:
                        HandleClose(message);
                        break;
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
                // AOSP adb_auth_host.cpp handle_auth(): the first token is answered
                // with a signed token; a subsequent token means the device rejected
                // the signature, and the client then advertises its public key so
                // the user can authorize it on the device (ro.adb.secure=1).
                // <para>AOSP adb_auth_host.cpp handle_auth()：首个令牌以签名应答；
                // 再次收到令牌表示设备拒绝了签名，客户端随后改为发送公钥，以便
                // 用户在设备上授权（ro.adb.secure=1）。</para>
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
                // The device is requesting our public key explicitly.
                byte[] explicitPublicKey = _authentication.BuildPublicKeyPayload();
                Send(AdbMessaging.BuildAuth(AdbAuthType.RSAPublicKey, explicitPublicKey));
                break;

            case AdbAuthType.Signature:
                // Not used by a client; ignore.
                break;
        }
    }

    private void HandleConnect(AdbMessage message)
    {
        PeerVersion = message.Arg0;
        PeerMaxPayload = message.Arg1;
        PeerBanner = message.PayloadAsString();
        PeerFeatures = ParseFeatures(PeerBanner);
    }

    private static IReadOnlyList<string> ParseFeatures(string? banner)
    {
        if (string.IsNullOrEmpty(banner))
        {
            return Array.Empty<string>();
        }

        string bannerText = banner ?? string.Empty;
        int idx = bannerText.IndexOf("features=", StringComparison.Ordinal);
        if (idx < 0)
        {
            return Array.Empty<string>();
        }

        string value = bannerText.Substring(idx + "features=".Length).Trim();
        return value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private void HandleOpen(AdbMessage message)
    {
        // The peer opened a stream towards us; assign a local id and acknowledge
        // with the sender convention (arg0 = our id, arg1 = peer id).
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
        // Peer-initiated messages carry arg0 = the peer's local id (our remote
        // id) and arg1 = our local id.
        // <para>对端发来的消息中：arg0 = 对端本地 ID（即我们的远端 ID），
        // arg1 = 我们的本地 ID。</para>
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg1, out AdbStream? stream))
            {
                stream.RemoteId = message.Arg0;
            }
        }
    }

    private void HandleWrite(AdbMessage message)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg1, out AdbStream? stream))
            {
                stream.EnqueueData(message.Payload ?? Array.Empty<byte>());
                // Acknowledge as the sender: arg0 = our id, arg1 = peer id.
                Send(AdbMessaging.BuildOkay(message.Arg1, message.Arg0));
            }
        }
    }

    private void HandleClose(AdbMessage message)
    {
        AdbStream? stream = null;
        lock (_lock)
        {
            if (_streams.TryGetValue(message.Arg1, out stream))
            {
                _streams.Remove(message.Arg1);
            }
        }

        stream?.MarkClosed();

        // Acknowledge the close so the peer can free its local id (sender convention).
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

        foreach (AdbStream stream in streams)
        {
            stream.MarkFaulted(ex);
        }
    }

    private void EnsureConnected()
    {
        lock (_lock)
        {
            if (!_connected)
            {
                throw new InvalidOperationException("ADB connection is not established.");
            }
        }
    }

    /// <summary>
    /// Disconnects and releases all resources.
    /// <para>断开连接并释放所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            _connected = false;
            foreach (AdbStream stream in _streams.Values)
            {
                stream.MarkClosed();
            }

            _streams.Clear();
        }

        _transport.Dispose();
        _authentication.Dispose();
        GC.SuppressFinalize(this);
    }
}
