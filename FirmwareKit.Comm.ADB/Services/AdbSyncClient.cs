using System.Text;

namespace FirmwareKit.Comm.ADB.Services;

/// <summary>
/// Sync wire command identifiers (AOSP sync.h / SYNC.TXT).
/// <para>Sync 线上命令标识（AOSP sync.h / SYNC.TXT）。</para>
/// </summary>
public static class SyncIds
{
    // v1 (4-byte) ids.
    /// <summary>v1 STAT request id. v1 STAT 请求标识。</summary>
    public const string Stat = "STAT";
    /// <summary>v1 LIST request id. v1 LIST 请求标识。</summary>
    public const string List = "LIST";
    /// <summary>v1 DENT (directory entry) response id. v1 DENT（目录条目）响应标识。</summary>
    public const string Dent = "DENT";
    /// <summary>v1 RECV (pull) request id. v1 RECV（拉取）请求标识。</summary>
    public const string Recv = "RECV";
    /// <summary>v1 SEND (push) request id. v1 SEND（推送）请求标识。</summary>
    public const string Send = "SEND";
    /// <summary>v1 DATA (payload chunk) id. v1 DATA（负载分块）标识。</summary>
    public const string Data = "DATA";
    /// <summary>v1 DONE (transfer complete) id. v1 DONE（传输完成）标识。</summary>
    public const string Done = "DONE";
    /// <summary>v1 QUIT (close sync service) id. v1 QUIT（关闭 sync 服务）标识。</summary>
    public const string Quit = "QUIT";
    /// <summary>v1 OKAY (success) status id. v1 OKAY（成功）状态标识。</summary>
    public const string Okay = "OKAY";
    /// <summary>v1 FAIL status id. v1 FAIL 状态标识。</summary>
    public const string Fail = "FAIL";

    // v2 (sendrecv_v2) ids (MKID('S','T','A','2') ...). The library speaks v1
    // framing today; these are reserved for a future v2 implementation.
    // <para>v2（sendrecv_v2）标识。库当前只讲 v1 帧格式，保留给未来 v2 实现。</para>
    /// <summary>v2 STAT id. v2 STAT 标识。</summary>
    public const string StatV2 = "STA2";
    /// <summary>v2 LIST id. v2 LIST 标识。</summary>
    public const string ListV2 = "LIS2";
    /// <summary>v2 DENT id. v2 DENT 标识。</summary>
    public const string DentV2 = "DNT2";
    /// <summary>v2 RECV id. v2 RECV 标识。</summary>
    public const string RecvV2 = "RCV2";
    /// <summary>v2 SEND id. v2 SEND 标识。</summary>
    public const string SendV2 = "SND2";
}

/// <summary>Directory entry from sync STAT / LIST. sync STAT / LIST 返回的目录条目。</summary>
public readonly struct SyncEntry
{
    /// <summary>Gets the entry mode bits (st_mode).
    /// <para>获取条目模式位（st_mode）。</para></summary>
    public uint Mode { get; }

    /// <summary>Gets the entry size in bytes.
    /// <para>获取条目字节大小。</para></summary>
    public uint Size { get; }

    /// <summary>Gets the modification time as Unix seconds.
    /// <para>获取修改时间（Unix 秒）。</para></summary>
    public uint Time { get; }

    /// <summary>Gets the entry name.
    /// <para>获取条目名称。</para></summary>
    public string Name { get; }

    /// <summary>
    /// Initializes a new sync entry.
    /// <para>初始化新的 sync 条目。</para>
    /// </summary>
    /// <param name="mode">Mode bits (st_mode). 模式位（st_mode）。</param>
    /// <param name="size">Size in bytes. 字节大小。</param>
    /// <param name="time">Modification time as Unix seconds. 修改时间（Unix 秒）。</param>
    /// <param name="name">Entry name. 条目名称。</param>
    public SyncEntry(uint mode, uint size, uint time, string name)
    {
        Mode = mode;
        Size = size;
        Time = time;
        Name = name;
    }
}

/// <summary>
/// Client for the ADB sync service ("sync:"), implementing file transfer
/// (SEND / RECV / STAT / LIST) aligned with AOSP file_sync_protocol.h. Speaks the
/// v1 wire format, which adbd accepts even when sendrecv_v2 is negotiated.
/// <para>ADB sync 服务客户端，实现文件传输（SEND / RECV / STAT / LIST），与 AOSP
/// file_sync_protocol.h 对齐。使用 v1 线上格式，即使协商了 sendrecv_v2，adbd 也接受。</para>
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
    /// <param name="useV2">Whether to enable sendrecv_v2 when supported. 支持时是否启用 sendrecv_v2。</param>
    /// <param name="maxChunk">Maximum chunk size for data payloads. 负载最大块大小。</param>
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
    /// Stats a remote file/directory. The v1 STAT response is a fixed
    /// <c>sync_stat_v1</c> struct (id + mode + size + mtime); there is no name field,
    /// so the entry name is derived from the requested path.
    /// <para>查询单个远端文件或目录状态。v1 STAT 响应为固定 sync_stat_v1 结构
    /// （id + mode + size + mtime），无名字字段，条目名取自请求路径。</para>
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
    /// Lists a remote directory. Each v1 DENT entry is id + mode + size + mtime +
    /// namelen + name; the list ends with a DONE status.
    /// <para>列出远端目录内容。每个 v1 DENT 条目为 id + mode + size + mtime +
    /// namelen + name，以 DONE 状态结束。</para>
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
    /// <param name="localPath">Path to the local file. 本地文件路径。</param>
    /// <param name="remotePath">Destination path on the device. 设备上的目标路径。</param>
    /// <param name="mode">Unix mode bits (default 0644). Unix 模式位（默认 0644）。</param>
    /// <param name="mtime">Modification time as Unix seconds (0 = now). 修改时间（Unix 秒，0 表示当前）。</param>
    public void Push(string localPath, string remotePath, uint mode = 0x81A4 /* 0644 */, uint mtime = 0)
    {
        if (localPath is null) throw new ArgumentNullException(nameof(localPath));
        if (remotePath is null) throw new ArgumentNullException(nameof(remotePath));

        using FileStream fs = File.OpenRead(localPath);
        PushStream(fs, remotePath, mode, mtime);
    }

    /// <summary>
    /// Pushes a stream to the device. The v1 SEND target is "path,mode" where AOSP
    /// formats the mode as ",0%o" (e.g. ",0100644"): the leading 0 is required
    /// because adbd parses the mode with strtolu(...,0) (auto-detect base), so
    /// without it "100644" is read as decimal and secure_mkdirs() fails.
    /// <para>将流推送到设备。v1 SEND 目标为 "path,mode"，AOSP 用 ",0%o" 格式化 mode
    /// （如 ",0100644"）：前导 0 是必需的，因为 adbd 用 strtolu(...,0) 自动识别进制。</para>
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

        string sendTarget = $"{remotePath},0{Convert.ToString(mode, 8)}";
        byte[] sendRequest = BuildV1Packet(SyncIds.Send, Encoding.UTF8.GetBytes(sendTarget));
        stream.Write(sendRequest);

        byte[] buffer = new byte[_maxChunk];
        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            byte[] chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            byte[] dataPacket = BuildV1Packet(SyncIds.Data, chunk);
            stream.Write(dataPacket);
        }

        // v1 DONE is an 8-byte status: id(4) + mtime in the msglen field (4).
        // Appending the mtime as a payload would leave stray bytes on the wire.
        byte[] donePacket = new byte[V1HeaderSize];
        Encoding.ASCII.GetBytes(SyncIds.Done).CopyTo(donePacket, 0);
        BitConverter.GetBytes(mtime).CopyTo(donePacket, 4);
        stream.Write(donePacket);

        ExpectResponse(stream, "push");
    }

    /// <summary>
    /// Pulls a remote file into a local file.
    /// <para>将设备上的远端文件拉取到本地文件。</para>
    /// </summary>
    /// <param name="remotePath">Source path on the device. 设备上的源路径。</param>
    /// <param name="localPath">Destination local path. 本地目标路径。</param>
    public void Pull(string remotePath, string localPath)
    {
        if (remotePath is null) throw new ArgumentNullException(nameof(remotePath));
        if (localPath is null) throw new ArgumentNullException(nameof(localPath));

        using FileStream fs = File.Create(localPath);
        PullStream(remotePath, fs);
    }

    /// <summary>Pulls a remote file into a stream. 将远端文件拉取到流中。</summary>
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

            if (id == SyncIds.Done) break;

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

    /// <summary>Closes the sync service stream. 关闭 sync 服务流。</summary>
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
    /// Releases the sync client and its underlying stream.
    /// <para>释放 sync 客户端及其底层流。</para>
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

        if (id == SyncIds.Okay) return;

        if (id == SyncIds.Fail)
        {
            byte[] err = ReadExact(stream, (int)length);
            throw new InvalidDataException($"sync {operation} failed: {Encoding.UTF8.GetString(err)}");
        }

        throw new InvalidDataException($"Unexpected sync response id '{id}' during {operation}.");
    }

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
    /// Reads exactly <paramref name="length"/> bytes, buffering excess bytes from a
    /// WRTE chunk that carries multiple sync responses.
    /// <para>精确读取指定字节数，并将承载多个 sync 响应的 WRTE 块中多余字节缓存。</para>
    /// </summary>
    private byte[] ReadExact(global::FirmwareKit.Comm.ADB.AdbStream stream, int length)
    {
        if (length < 0)
        {
            throw new InvalidDataException("Negative sync length.");
        }

        byte[] result = new byte[length];
        int offset = 0;

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
