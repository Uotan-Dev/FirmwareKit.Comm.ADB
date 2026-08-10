using System.Text;

namespace FirmwareKit.Comm.ADB.Services;

/// <summary>
/// Sync wire command identifiers (AOSP sync.h / SYNC.TXT).
/// <para>Sync 线上命令标识（AOSP sync.h / SYNC.TXT）。</para>
/// </summary>
public static class SyncIds
{
    // v1 (4-byte) ids.
    public const string Stat = "STAT";
    public const string List = "LIST";
    public const string Dent = "DENT";
    public const string Recv = "RECV";
    public const string Send = "SEND";
    public const string Data = "DATA";
    public const string Done = "DONE";
    public const string Quit = "QUIT";
    public const string Okay = "OKAY";
    public const string Fail = "FAIL";

    // v2 (sendrecv_v2) ids, matching AOSP file_sync_protocol.h
    // (MKID('S','T','A','2') etc.). The library currently speaks v1 framing,
    // so these are reserved for a future v2 implementation.
    // <para>v2（sendrecv_v2）标识，与 AOSP file_sync_protocol.h 一致
    // （MKID('S','T','A','2') 等）。库当前只讲 v1 帧格式，这些保留给未来的 v2 实现。</para>
    public const string StatV2 = "STA2";
    public const string ListV2 = "LIS2";
    public const string DentV2 = "DNT2";
    public const string RecvV2 = "RCV2";
    public const string SendV2 = "SND2";
}

/// <summary>
/// Directory entry returned by sync STAT / LIST.
/// <para>sync STAT / LIST 返回的目录条目。</para>
/// </summary>
public readonly struct SyncEntry
{
    /// <summary>
    /// Gets the entry mode (st_mode).
    /// <para>获取条目模式（st_mode）。</para>
    /// </summary>
    public uint Mode { get; }

    /// <summary>
    /// Gets the entry size in bytes.
    /// <para>获取条目字节大小。</para>
    /// </summary>
    public uint Size { get; }

    /// <summary>
    /// Gets the modification time (Unix seconds).
    /// <para>获取修改时间（Unix 秒）。</para>
    /// </summary>
    public uint Time { get; }

    /// <summary>
    /// Gets the entry name.
    /// <para>获取条目名称。</para>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Initializes a new sync entry.
    /// <para>初始化新的 sync 条目。</para>
    /// </summary>
    public SyncEntry(uint mode, uint size, uint time, string name)
    {
        Mode = mode;
        Size = size;
        Time = time;
        Name = name;
    }
}

/// <summary>
/// Client for the ADB sync service ("sync:") implementing file transfer
/// (SEND / RECV / STAT / LIST), aligned with AOSP file_sync_protocol.h.
/// The current implementation speaks the v1 wire format, which adbd accepts
/// even when sendrecv_v2 is negotiated.
/// <para>ADB sync 服务（"sync:"）客户端，实现文件传输（SEND / RECV / STAT / LIST），
/// 与 AOSP file_sync_protocol.h 对齐。当前实现使用 v1 线上格式；
/// 即使协商了 sendrecv_v2，adbd 也接受 v1 格式。</para>
/// </summary>
public sealed class AdbSyncClient : IDisposable
{
    private const int V1HeaderSize = 8;      // id(4) + length(4)
    private const int V2HeaderSize = 16;     // id(8) + length(4) + crc(4)
    private const uint DefaultMaxChunk = 64 * 1024;

    private readonly global::FirmwareKit.Comm.ADB.AdbConnection _connection;
    private readonly bool _useV2;
    private readonly uint _maxChunk;
    private readonly Queue<byte> _readBuffer = new();
    private global::FirmwareKit.Comm.ADB.AdbStream? _stream;
    private bool _disposed;

    /// <summary>
    /// Initializes a new sync client.
    /// <para>初始化新的 sync 客户端。</para>
    /// </summary>
    /// <param name="connection">The connected ADB connection. 已连接的 ADB 连接。</param>
    /// <param name="useV2">Whether to use the sendrecv_v2 wire format. 是否使用 sendrecv_v2 线上格式。</param>
    /// <param name="maxChunk">Maximum chunk size for payloads. 负载最大块大小。</param>
    public AdbSyncClient(global::FirmwareKit.Comm.ADB.AdbConnection connection, bool useV2 = true, uint maxChunk = DefaultMaxChunk)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _useV2 = useV2 && connection.PeerFeatures.Contains("sendrecv_v2");
        _maxChunk = maxChunk;
    }

    private global::FirmwareKit.Comm.ADB.AdbStream EnsureStream()
    {
        if (_stream is null)
        {
            _stream = _connection.OpenStream("sync:");
        }

        return _stream;
    }

    /// <summary>
    /// Queries the status of a single remote file or directory. The v1 STAT
    /// response is a fixed <c>sync_stat_v1</c> struct: id(4) + mode(4) +
    /// size(4) + mtime(4); there is no name field, so the entry name is derived
    /// from the requested path.
    /// <para>查询单个远端文件或目录的状态。v1 STAT 响应是固定大小的
    /// <c>sync_stat_v1</c> 结构：id(4) + mode(4) + size(4) + mtime(4)；
    /// 不含名字字段，因此条目名取自请求路径。</para>
    /// </summary>
    public SyncEntry? Stat(string remotePath)
    {
        if (remotePath is null) throw new ArgumentNullException(nameof(remotePath));
        var stream = EnsureStream();

        byte[] request = BuildV1Packet(SyncIds.Stat, Encoding.UTF8.GetBytes(remotePath));
        stream.Write(request);

        string id = Encoding.ASCII.GetString(ReadExact(stream, 4), 0, 4);

        if (id == SyncIds.Fail)
        {
            ReadExact(stream, (int)ReadUInt32(stream));
            return null;
        }

        if (id != SyncIds.Stat)
        {
            throw new InvalidDataException($"Unexpected sync response id '{id}' to STAT.");
        }

        byte[] body = ReadExact(stream, 12); // mode + size + mtime
        uint mode = BitConverter.ToUInt32(body, 0);
        uint size = BitConverter.ToUInt32(body, 4);
        uint time = BitConverter.ToUInt32(body, 8);
        string name = Path.GetFileName(remotePath.TrimEnd('/'));
        return new SyncEntry(mode, size, time, name);
    }

    /// <summary>
    /// Lists the contents of a remote directory. Each v1 DENT entry is
    /// id(4) + mode(4) + size(4) + mtime(4) + namelen(4) + name; the list ends
    /// with a DONE status message.
    /// <para>列出远端目录的内容。每个 v1 DENT 条目为 id(4) + mode(4) +
    /// size(4) + mtime(4) + namelen(4) + name；列表以 DONE 状态消息结束。</para>
    /// </summary>
    public IReadOnlyList<SyncEntry> List(string remotePath)
    {
        if (remotePath is null) throw new ArgumentNullException(nameof(remotePath));
        var stream = EnsureStream();

        byte[] request = BuildV1Packet(SyncIds.List, Encoding.UTF8.GetBytes(remotePath));
        stream.Write(request);

        var entries = new List<SyncEntry>();
        while (true)
        {
            string id = Encoding.ASCII.GetString(ReadExact(stream, 4), 0, 4);

            if (id == SyncIds.Done)
            {
                ReadExact(stream, 4); // msglen (always 0 for DONE)
                break;
            }

            if (id == SyncIds.Fail)
            {
                uint msglen = ReadUInt32(stream);
                byte[] err = ReadExact(stream, (int)msglen);
                throw new InvalidDataException(
                    $"sync LIST failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
            }

            if (id != SyncIds.Dent)
            {
                throw new InvalidDataException($"Unexpected sync response id '{id}' to LIST.");
            }

            byte[] body = ReadExact(stream, 16); // mode + size + mtime + namelen
            uint mode = BitConverter.ToUInt32(body, 0);
            uint size = BitConverter.ToUInt32(body, 4);
            uint time = BitConverter.ToUInt32(body, 8);
            uint namelen = BitConverter.ToUInt32(body, 12);
            byte[] nameBytes = ReadExact(stream, (int)namelen);
            entries.Add(new SyncEntry(mode, size, time, Encoding.UTF8.GetString(nameBytes)));
        }

        return entries;
    }

    /// <summary>
    /// Pushes a local file to the device.
    /// <para>将本地文件推送到设备。</para>
    /// </summary>
    public void Push(string localPath, string remotePath, uint mode = 0x81A4 /* 0644 */, uint mtime = 0)
    {
        if (localPath is null) throw new ArgumentNullException(nameof(localPath));
        if (remotePath is null) throw new ArgumentNullException(nameof(remotePath));

        using FileStream fs = File.OpenRead(localPath);
        PushStream(fs, remotePath, mode, mtime);
    }

    /// <summary>
    /// Pushes a stream to the device under the given remote path.
    /// <para>将流推送到设备上的指定远端路径。</para>
    /// </summary>
    public void PushStream(Stream source, string remotePath, uint mode = 0x81A4, uint mtime = 0)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (remotePath is null) throw new ArgumentNullException(nameof(remotePath));

        var stream = EnsureStream();
        if (mtime == 0)
        {
            mtime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        // v1 SEND target is "path,mode" (mode in octal). The mtime is NOT part
        // of the target: it travels in the DONE message below (msglen field).
        // <para>v1 SEND 目标为 "path,mode"（mode 为八进制）。mtime 不在目标中，
        // 而是随下方的 DONE 消息（msglen 字段）传递。</para>
        string sendTarget = $"{remotePath},{Convert.ToString(mode, 8)}";
        byte[] sendRequest = BuildV1Packet(SyncIds.Send, Encoding.UTF8.GetBytes(sendTarget));
        stream.Write(sendRequest);

        byte[] buffer = new byte[_maxChunk];
        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            byte[] chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            byte[] dataPacket = BuildV1Packet(SyncIds.Data, chunk);
            stream.Write(dataPacket);
        }

        // v1 DONE is an 8-byte status message: id(4) + mtime as the msglen
        // field (4). Appending the mtime as a payload would leave stray bytes
        // on the wire and corrupt the next request.
        // <para>v1 DONE 是 8 字节状态消息：id(4) + 以 msglen 字段携带的 mtime(4)。
        // 若把 mtime 作为负载追加，会在线上留下多余字节并破坏下一个请求。</para>
        byte[] donePacket = new byte[V1HeaderSize];
        Encoding.ASCII.GetBytes(SyncIds.Done).CopyTo(donePacket, 0);
        BitConverter.GetBytes(mtime).CopyTo(donePacket, 4);
        stream.Write(donePacket);

        ExpectResponse(stream, "push");
    }

    /// <summary>
    /// Pulls a remote file from the device into a local file.
    /// <para>将设备上的远端文件拉取到本地文件。</para>
    /// </summary>
    public void Pull(string remotePath, string localPath)
    {
        if (remotePath is null) throw new ArgumentNullException(nameof(remotePath));
        if (localPath is null) throw new ArgumentNullException(nameof(localPath));

        using FileStream fs = File.Create(localPath);
        PullStream(remotePath, fs);
    }

    /// <summary>
    /// Pulls a remote file into a stream.
    /// <para>将远端文件拉取到流中。</para>
    /// </summary>
    public void PullStream(string remotePath, Stream destination)
    {
        if (remotePath is null) throw new ArgumentNullException(nameof(remotePath));
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        var stream = EnsureStream();
        byte[] request = BuildV1Packet(SyncIds.Recv, Encoding.UTF8.GetBytes(remotePath));
        stream.Write(request);

        while (true)
        {
            byte[] header = ReadExact(stream, V1HeaderSize);
            string id = Encoding.ASCII.GetString(header, 0, 4);
            uint length = BitConverter.ToUInt32(header, 4);

            if (id == SyncIds.Done)
            {
                break;
            }

            if (id == SyncIds.Fail)
            {
                byte[] err = ReadExact(stream, (int)length);
                throw new InvalidDataException($"sync pull failed for '{remotePath}': {Encoding.UTF8.GetString(err)}");
            }

            if (id == SyncIds.Data)
            {
                byte[] body = ReadExact(stream, (int)length);
                destination.Write(body, 0, body.Length);
                continue;
            }

            throw new InvalidDataException($"Unexpected sync response id '{id}' to RECV.");
        }
    }

    /// <summary>
    /// Closes the sync service stream.
    /// <para>关闭 sync 服务流。</para>
    /// </summary>
    public void Close()
    {
        if (_disposed) return;
        _disposed = true;

        var stream = _stream;
        _stream = null;

        if (stream is not null && !stream.IsClosed)
        {
            try
            {
                byte[] quit = BuildV1Packet(SyncIds.Quit, Array.Empty<byte>());
                stream.Write(quit);
            }
            catch
            {
                // The connection may already be gone.
            }

            stream.Close();
        }
    }

    /// <summary>
    /// Disposes the sync client.
    /// <para>释放 sync 客户端。</para>
    /// </summary>
    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private void ExpectResponse(global::FirmwareKit.Comm.ADB.AdbStream stream, string operation)
    {
        byte[] header = ReadExact(stream, V1HeaderSize);
        string id = Encoding.ASCII.GetString(header, 0, 4);
        uint length = BitConverter.ToUInt32(header, 4);

        if (id == SyncIds.Okay)
        {
            return;
        }

        if (id == SyncIds.Fail)
        {
            byte[] err = ReadExact(stream, (int)length);
            throw new InvalidDataException($"sync {operation} failed: {Encoding.UTF8.GetString(err)}");
        }

        throw new InvalidDataException($"Unexpected sync response id '{id}' during {operation}.");
    }

    /// <summary>
    /// Reads the next 4 bytes as a little-endian unsigned integer.
    /// <para>将接下来的 4 个字节读取为小端无符号整数。</para>
    /// </summary>
    private uint ReadUInt32(global::FirmwareKit.Comm.ADB.AdbStream stream) =>
        BitConverter.ToUInt32(ReadExact(stream, 4), 0);

    private static byte[] BuildV1Packet(string id, byte[] payload)
    {
        byte[] packet = new byte[V1HeaderSize + payload.Length];
        byte[] idBytes = Encoding.ASCII.GetBytes(id);

        Buffer.BlockCopy(idBytes, 0, packet, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)payload.Length), 0, packet, 4, 4);
        Buffer.BlockCopy(payload, 0, packet, V1HeaderSize, payload.Length);
        return packet;
    }

    /// <summary>
    /// Reads exactly the requested number of bytes, buffering any excess bytes
    /// from a WRTE chunk that carries multiple sync responses.
    /// <para>精确读取指定数量的字节，并将承载多个 sync 响应的
    /// WRTE 块中多余的字节缓存起来。</para>
    /// </summary>
    private byte[] ReadExact(global::FirmwareKit.Comm.ADB.AdbStream stream, int length)
    {
        if (length < 0)
        {
            throw new InvalidDataException("Negative sync length.");
        }

        byte[] result = new byte[length];
        int offset = 0;

        // Drain any bytes buffered from a previous chunk first.
        while (offset < length && _readBuffer.Count > 0)
        {
            result[offset++] = _readBuffer.Dequeue();
        }

        while (offset < length)
        {
            byte[]? chunk = stream.Read();
            if (chunk is null)
            {
                throw new EndOfStreamException("Sync stream closed unexpectedly.");
            }

            int needed = length - offset;
            if (chunk.Length <= needed)
            {
                Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            else
            {
                Buffer.BlockCopy(chunk, 0, result, offset, needed);
                offset += needed;
                for (int i = needed; i < chunk.Length; i++)
                {
                    _readBuffer.Enqueue(chunk[i]);
                }
            }
        }

        return result;
    }
}
