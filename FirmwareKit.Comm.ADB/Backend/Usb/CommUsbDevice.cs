using FirmwareKit.Comm.Abstractions;

namespace FirmwareKit.Comm.ADB.Backend.Usb;

/// <summary>
/// Concrete ADB USB device using FirmwareKit.Comm for USB communication.
/// <para>使用 FirmwareKit.Comm 进行 USB 通信的具体 ADB USB 设备。</para>
/// </summary>
public sealed class CommUsbDevice : UsbDevice
{
    private const int DefaultIoTimeoutMs = 30000;
    private readonly global::FirmwareKit.Comm.IFirmwareKitComm _comm;
    private readonly UsbDeviceInfo _deviceInfo;
    private readonly bool _forceLibUsb;
    private IUsbDeviceSession? _session;

    // Set by Dispose() so a blocked message-loop read returns promptly instead of
    // waiting out the 30 s bulk-read timeout. <para>由 Dispose() 置位，使被阻塞的
    // 消息循环读取能快速返回，而不必等待 30 秒读超时。</para>
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new CommUsbDevice with the specified communication interface,
    /// device info, and libusb preference.
    /// <para>使用指定的通信接口、设备信息和 libusb 偏好初始化新的 CommUsbDevice。</para>
    /// </summary>
    public CommUsbDevice(global::FirmwareKit.Comm.IFirmwareKitComm comm, UsbDeviceInfo deviceInfo, bool forceLibUsb)
    {
        _comm = comm ?? throw new ArgumentNullException(nameof(comm));
        _deviceInfo = deviceInfo ?? throw new ArgumentNullException(nameof(deviceInfo));
        _forceLibUsb = forceLibUsb;

        DevicePath = _deviceInfo.DevicePath ?? string.Empty;
        SerialNumber = _deviceInfo.SerialNumber;
        VendorId = _deviceInfo.VendorId;
        ProductId = _deviceInfo.ProductId;
        DeviceKey = string.IsNullOrWhiteSpace(_deviceInfo.DeviceKey) ? null : _deviceInfo.DeviceKey;
    }

    /// <summary>
    /// Gets the bound bulk IN endpoint address (bit 7 set), or 0 when the backend does
    /// not expose endpoint-level binding.
    /// <para>获取绑定的批量 IN 端点地址（bit 7 置位），后端未暴露端点绑定时为 0。</para>
    /// </summary>
    public byte EndpointIn
    {
        get { EnsureSession(); return _session!.EndpointIn; }
    }

    /// <summary>
    /// Gets the bound bulk OUT endpoint address (bit 7 clear), or 0 when the backend does
    /// not expose endpoint-level binding.
    /// <para>获取绑定的批量 OUT 端点地址（bit 7 清零），后端未暴露端点绑定时为 0。</para>
    /// </summary>
    public byte EndpointOut
    {
        get { EnsureSession(); return _session!.EndpointOut; }
    }

    /// <summary>
    /// Creates a handle to the USB device. Returns 0 on success, -1 on failure.
    /// <para>创建 USB 设备句柄。成功返回 0，失败返回 -1。</para>
    /// </summary>
    public override int CreateHandle()
    {
        if (_session != null)
        {
            return 0;
        }

        // When a stable device key is known (e.g. captured before a mode switch), open by
        // key so the same physical device is reached even after VID/PID change.
        // <para>当已知稳定设备键（例如模式切换前捕获）时按键打开，即使 VID/PID 变化
        // 也能命中同一物理设备。</para>
        string? deviceKey = DeviceKey;
        if (!string.IsNullOrWhiteSpace(deviceKey))
        {
            // netstandard2.0 reference assemblies lack the [NotNullWhen] annotation on
            // IsNullOrWhiteSpace, so the compiler cannot narrow deviceKey itself.
            // <para>netstandard2.0 引用程序集缺少 IsNullOrWhiteSpace 上的 [NotNullWhen]
            // 标注，编译器无法自行收窄 deviceKey。</para>
            _session = _comm.OpenUsbDeviceSessionByKey(deviceKey!, ResolveApiKind());
            if (_session == null)
            {
                // FirmwareKit.Comm's by-key open fails for ADB-interface keys (the key
                // embeds FF/42/01, e.g. from GetAllDevices); fall back to opening by
                // descriptor filter, which succeeds for the same device.
                // <para>FirmwareKit.Comm 按键打开对 ADB 接口键（键内嵌 FF/42/01，如
                // GetAllDevices 枚举所得）失败；回退为按描述符过滤器打开，同一设备
                // 可成功打开。</para>
                _session = OpenByFilter();
                if (_session == null)
                {
                    return -1;
                }
            }

            SerialNumber = _session.DeviceInfo.SerialNumber;
            return 0;
        }

        _session = OpenByFilter();
        if (_session == null)
        {
            return -1;
        }

        SerialNumber = _session.DeviceInfo.SerialNumber;
        return 0;
    }

    /// <summary>
    /// Opens a USB device session using a descriptor filter built from this device's
    /// info (VID/PID/serial/path plus the observed interface class when available).
    /// <para>用根据本设备信息构造的描述符过滤器（VID/PID/序列号/路径，以及可用的
    /// 观测接口类）打开 USB 设备会话。</para>
    /// </summary>
    private IUsbDeviceSession? OpenByFilter()
    {
        byte? interfaceClass = _deviceInfo.InterfaceMetadataObserved ? _deviceInfo.InterfaceClass : null;
        byte? interfaceSubClass = _deviceInfo.InterfaceMetadataObserved ? _deviceInfo.InterfaceSubClass : null;
        byte? interfaceProtocol = _deviceInfo.InterfaceMetadataObserved ? _deviceInfo.InterfaceProtocol : null;

        var filter = new UsbDeviceFilter
        {
            VendorId = _deviceInfo.VendorId,
            ProductId = _deviceInfo.ProductId,
            SerialNumber = _deviceInfo.SerialNumber,
            DevicePathContains = string.IsNullOrWhiteSpace(_deviceInfo.DevicePath) ? null : _deviceInfo.DevicePath,
            InterfaceClass = interfaceClass,
            InterfaceSubClass = interfaceSubClass,
            InterfaceProtocol = interfaceProtocol,
        };

        return _comm.OpenUsbDeviceSession(ResolveApiKind(), filter);
    }

    /// <summary>
    /// Reopens the device session by its stable device key (mode-switch pattern:
    /// adb → fastboot → EDL). The previous session, if any, is disposed first.
    /// <para>按键重开设备会话（模式切换模式：adb → fastboot → EDL）。
    /// 若存在旧会话则先释放。</para>
    /// </summary>
    /// <returns>0 on success, -1 when the device key no longer resolves.
    /// <para>成功返回 0；设备键无法解析时返回 -1。</para></returns>
    public int ReopenByKey()
    {
        if (string.IsNullOrWhiteSpace(DeviceKey))
        {
            throw new InvalidOperationException("Device has no stable device key to reopen by.");
        }

        _session?.Dispose();
        _session = null;
        return CreateHandle();
    }

    /// <summary>
    /// Reads data from the USB device with the specified maximum length.
    /// <para>从 USB 设备读取指定最大长度的数据。</para>
    /// Uses the exact-read loop from FirmwareKit.Comm 1.1.0 so short bulk packets are
    /// accumulated until the requested byte count is reached (ADB framing depends on
    /// complete 24-byte headers and exact-length payloads).
    /// <para>使用 FirmwareKit.Comm 1.1.0 的精确读取循环，短 bulk 包会持续累积直至
    /// 达到请求的字节数（ADB 帧依赖完整的 24 字节头部与定长负载）。</para>
    /// </summary>
    public override byte[] Read(int length)
    {
        EnsureSession();
        if (length <= 0) return Array.Empty<byte>();

        // Read in short timeout slices and poll the disposed flag so the message
        // loop (blocked waiting for the next ADB message) returns promptly when the
        // connection/session is disposed. A single 30 s blocking bulk read would
        // otherwise keep ReleaseInterface/Close busy for the full timeout, making
        // Dispose hang and tripping the test's 30 s bound. This mirrors the
        // reference client, which drives reads through cancellable async BeginRead.
        // <para>以短超时切片读取并轮询 disposed 标志，使消息循环（阻塞等待下一条
        // ADB 消息）在连接/会话释放时能快速返回。单次 30 秒阻塞 bulk 读会让
        // ReleaseInterface/Close 卡满整个超时时长，导致 Dispose 挂起并触发测试
        // 的 30 秒上限。这与参考客户端用可取消的异步 BeginRead 驱动读取一致。</para>
        var buffer = new byte[length];
        int count = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int sliceMs = 200;

        while (count < length)
        {
            if (_disposed)
            {
                return Array.Empty<byte>();
            }

            int read = ReadExactInto(buffer, count, length - count, sliceMs);
            if (read > 0)
            {
                count += read;
                continue;
            }

            // No data in this slice. If the overall I/O timeout has elapsed without
            // receiving the requested bytes, return what we have (empty for the
            // header read, which the message loop treats as a read failure).
            if (stopwatch.ElapsedMilliseconds >= DefaultIoTimeoutMs)
            {
                break;
            }
        }

        if (count == 0)
        {
            return Array.Empty<byte>();
        }

        if (count == length)
        {
            return buffer;
        }

        var partial = new byte[count];
        Buffer.BlockCopy(buffer, 0, partial, 0, count);
        return partial;
    }

    /// <summary>
    /// Reads data directly into the specified buffer for zero-allocation reads.
    /// <para>将数据直接读入指定缓冲区，实现零分配读取。</para>
    /// </summary>
    public override int ReadInto(byte[] buffer, int offset, int length)
    {
        EnsureSession();
        if (length <= 0) return 0;
        if (offset < 0 || offset + length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return ReadExactInto(buffer, offset, length, DefaultIoTimeoutMs);
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes into the buffer, looping over short
    /// reads until the buffer is filled or the total timeout elapses.
    /// <para>将恰好 <paramref name="length"/> 字节读入缓冲区：短读时循环累积，
    /// 直至缓冲区填满或总超时耗尽。</para>
    /// </summary>
    private int ReadExactInto(byte[] buffer, int offset, int length, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int count = 0;
        while (count < length)
        {
            long elapsed = stopwatch.ElapsedMilliseconds;
            if (elapsed >= timeoutMs)
            {
                break;
            }

            int read = _session!.ReadInto(buffer, offset + count, length - count, timeoutMs - (int)elapsed);
            if (read <= 0)
            {
                break;
            }

            count += read;
        }

        return count;
    }

    /// <summary>
    /// Writes data to the USB device, returning the number of bytes written.
    /// <para>向 USB 设备写入数据，返回写入的字节数。</para>
    /// </summary>
    public override long Write(byte[] data, int length)
    {
        EnsureSession();
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (length < 0 || length > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return _session!.Write(data, 0, length, DefaultIoTimeoutMs);
    }

    /// <summary>
    /// Retrieves the serial number of the USB device. Returns 0 on success, -1 on failure.
    /// <para>获取 USB 设备的序列号。成功返回 0，失败返回 -1。</para>
    /// </summary>
    public override int GetSerialNumber()
    {
        EnsureSession();
        SerialNumber = _session!.DeviceInfo.SerialNumber;
        return string.IsNullOrEmpty(SerialNumber) ? -1 : 0;
    }

    /// <summary>
    /// Resets the USB device connection.
    /// <para>重置 USB 设备连接。</para>
    /// </summary>
    public override void Reset()
    {
        if (_session == null) return;
        _session.Reset();
    }

    /// <summary>
    /// Releases the USB device session and all associated resources.
    /// <para>释放 USB 设备会话及所有关联资源。</para>
    /// </summary>
    public override void Dispose()
    {
        _disposed = true;
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Reads data from the USB device asynchronously, accumulating short packets until
    /// the requested length is reached.
    /// <para>异步从 USB 设备读取数据，短包持续累积直至达到请求长度。</para>
    /// </summary>
    public Task<byte[]> ReadAsync(int length, CancellationToken cancellationToken = default)
    {
        EnsureSession();
        if (length <= 0) return Task.FromResult(Array.Empty<byte>());
        return _session!.AsAsync().ReadExactAsync(length, DefaultIoTimeoutMs, cancellationToken);
    }

    /// <summary>
    /// Writes data to the USB device asynchronously, returning the number of bytes written.
    /// <para>异步向 USB 设备写入数据，返回写入的字节数。</para>
    /// </summary>
    public Task<long> WriteAsync(byte[] data, int length, CancellationToken cancellationToken = default)
    {
        EnsureSession();
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (length < 0 || length > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return _session!.AsAsync().WriteAsync(data, 0, length, DefaultIoTimeoutMs, cancellationToken);
    }

    /// <summary>
    /// Resets the USB device connection asynchronously.
    /// <para>异步重置 USB 设备连接。</para>
    /// </summary>
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        EnsureSession();
        return _session!.AsAsync().ResetAsync(cancellationToken);
    }

    private void EnsureSession()
    {
        if (_session == null && CreateHandle() != 0)
        {
            throw new InvalidOperationException("Unable to open USB session through FirmwareKit.Comm.");
        }
    }

    private UsbApiKind ResolveApiKind()
    {
        if (_forceLibUsb)
        {
            return UsbApiKind.LibUsbDotNet;
        }

        if (_deviceInfo.SourceApiKind == UsbApiKind.LibUsbDotNet)
        {
            return UsbApiKind.LibUsbDotNet;
        }

        return UsbApiKind.Native;
    }
}
