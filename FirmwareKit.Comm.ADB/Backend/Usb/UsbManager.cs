using FirmwareKit.Comm.Abstractions;

namespace FirmwareKit.Comm.ADB.Backend.Usb;

/// <summary>
/// Manages USB ADB device discovery and enumeration through FirmwareKit.Comm.
/// <para>通过 FirmwareKit.Comm 管理 USB ADB 设备的发现与枚举。</para>
/// </summary>
public static class UsbManager
{
    // AOSP constants (adb/usb_linux.cpp / adb/usb_osx.cpp):
    //   ADB_CLASS     = 0xFF (vendor-specific)
    //   ADB_SUBCLASS  = 0x42
    //   ADB_PROTOCOL  = 0x01
    private const byte AdbInterfaceClass = 0xFF;
    private const byte AdbInterfaceSubClass = 0x42;
    private const byte AdbInterfaceProtocol = 0x01;

    /// <summary>
    /// Gets or sets whether to force the use of libusb-dotnet instead of the platform
    /// native backend. Defaults to <c>false</c>: the platform backend (WinUSB on
    /// Windows, usbfs on Linux, IOKit on macOS) is the default; libusb remains an
    /// opt-in alternative. There is NO automatic fallback — if the chosen backend
    /// cannot enumerate/open a device, the error surfaces to the caller instead of
    /// silently switching.
    /// <para>获取或设置是否强制使用 libusb-dotnet 而非平台原生后端。默认为
    /// <c>false</c>：平台后端（Windows 用 WinUSB、Linux 用 usbfs、macOS 用 IOKit）
    /// 是默认；libusb 作为可选的备选。不提供自动回退——所选后端无法枚举/打开设备时
    /// 错误会直接抛给调用方，而非静默切换。</para>
    /// </summary>
    public static bool ForceLibUsb { get; set; } = false;

    private static readonly global::FirmwareKit.Comm.IFirmwareKitComm Comm = new global::FirmwareKit.Comm.FirmwareKitComm();

    /// <summary>
    /// Gets capability summaries for the currently registered USB APIs (FirmwareKit.Comm 1.1.0).
    /// <para>获取当前已注册 USB API 的能力摘要（FirmwareKit.Comm 1.1.0）。</para>
    /// </summary>
    public static IReadOnlyList<UsbApiCapabilities> GetAvailableUsbApiCapabilities() => Comm.GetAvailableUsbApiCapabilities();

    /// <summary>
    /// Enumerates all connected ADB USB devices (standard ADB interface).
    /// <para>枚举所有已连接的 ADB USB 设备（标准 ADB 接口）。</para>
    /// </summary>
    public static List<UsbDevice> GetAllDevices()
    {
        try
        {
            var apiKind = ResolveApiKind();

            var result = new List<UsbDevice>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddDevices(Comm.EnumerateUsbDevices(apiKind, BuildAdbInterfaceFilter()), apiKind, seenPaths, result);
            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to enumerate ADB devices via FirmwareKit.Comm.", ex);
        }
    }

    /// <summary>
    /// Enumerates all connected ADB USB devices asynchronously (FirmwareKit.Comm 1.1.0).
    /// <para>异步枚举所有已连接的 ADB USB 设备（FirmwareKit.Comm 1.1.0）。</para>
    /// </summary>
    public static async Task<List<UsbDevice>> GetAllDevicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var apiKind = ResolveApiKind();
            IReadOnlyList<UsbDeviceInfo> discovered =
                await Comm.EnumerateUsbDevicesAsync(apiKind, BuildAdbInterfaceFilter(), cancellationToken).ConfigureAwait(false);

            var result = new List<UsbDevice>();
            AddDevices(discovered, apiKind, new HashSet<string>(StringComparer.OrdinalIgnoreCase), result);
            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to enumerate ADB devices via FirmwareKit.Comm.", ex);
        }
    }

    /// <summary>
    /// Enumerates ADB devices by VID/PID without interface constraints (fallback for
    /// devices whose ADB interface does not carry the standard protocol code).
    /// <para>按 VID/PID 枚举 ADB 设备（不限制接口，用于 ADB 接口协议码非标准的设备兜底）。</para>
    /// </summary>
    public static List<UsbDevice> GetDevicesByVidPid(ushort vendorId, ushort? productId = null)
    {
        var filter = new UsbDeviceFilter
        {
            VendorId = vendorId,
            ProductId = productId,
        };

        var apiKind = ResolveApiKind();
        var result = new List<UsbDevice>();
        AddDevices(Comm.EnumerateUsbDevices(apiKind, filter), apiKind, new HashSet<string>(StringComparer.OrdinalIgnoreCase), result);
        return result;
    }

    /// <summary>
    /// Enumerates ADB devices by VID/PID asynchronously (FirmwareKit.Comm 1.1.0).
    /// <para>按 VID/PID 异步枚举 ADB 设备（FirmwareKit.Comm 1.1.0）。</para>
    /// </summary>
    public static async Task<List<UsbDevice>> GetDevicesByVidPidAsync(ushort vendorId, ushort? productId = null, CancellationToken cancellationToken = default)
    {
        var filter = new UsbDeviceFilter
        {
            VendorId = vendorId,
            ProductId = productId,
        };

        var apiKind = ResolveApiKind();
        IReadOnlyList<UsbDeviceInfo> discovered =
            await Comm.EnumerateUsbDevicesAsync(apiKind, filter, cancellationToken).ConfigureAwait(false);

        var result = new List<UsbDevice>();
        AddDevices(discovered, apiKind, new HashSet<string>(StringComparer.OrdinalIgnoreCase), result);
        return result;
    }

    /// <summary>
    /// Waits until at least one ADB USB device appears (FirmwareKit.Comm 1.1.0 wait API).
    /// <para>等待至少一个 ADB USB 设备出现（FirmwareKit.Comm 1.1.0 等待 API）。</para>
    /// </summary>
    /// <param name="timeout">Maximum wait time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when an ADB device appeared before the timeout. <para>超时前出现 ADB 设备时返回 <c>true</c>。</para></returns>
    public static Task<bool> WaitForDeviceAppearAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Comm.WaitForUsbDeviceAppearAsync(ResolveApiKind(), BuildAdbInterfaceFilter(), timeout, cancellationToken);

    /// <summary>
    /// Waits until no ADB USB device remains (FirmwareKit.Comm 1.1.0 wait API).
    /// <para>等待不再存在 ADB USB 设备（FirmwareKit.Comm 1.1.0 等待 API）。</para>
    /// </summary>
    /// <param name="timeout">Maximum wait time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when no ADB device remains before the timeout. <para>超时前不再存在 ADB 设备时返回 <c>true</c>。</para></returns>
    public static Task<bool> WaitForDeviceDisappearAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Comm.WaitForUsbDeviceDisappearAsync(ResolveApiKind(), BuildAdbInterfaceFilter(), timeout, cancellationToken);

    /// <summary>
    /// Waits for a mode switch (e.g. adb → fastboot → EDL): the removed filter's devices
    /// disappear AND the appeared filter's devices show up (FirmwareKit.Comm 1.1.0).
    /// <para>等待模式切换（如 adb → fastboot → EDL）：removed 过滤器设备消失且
    /// appeared 过滤器设备出现（FirmwareKit.Comm 1.1.0）。</para>
    /// </summary>
    /// <param name="removedFilter">Filter for devices expected to disappear; pass <c>null</c> to skip. <para>预期消失的设备过滤器；传 <c>null</c> 跳过。</para></param>
    /// <param name="appearedFilter">Filter for devices expected to appear; pass <c>null</c> to skip. <para>预期出现的设备过滤器；传 <c>null</c> 跳过。</para></param>
    /// <param name="timeout">Maximum wait time (default 30 s). <para>最大等待时间（默认 30 秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when the mode switch completed before the timeout. <para>超时前完成模式切换时返回 <c>true</c>。</para></returns>
    public static Task<bool> WaitForModeSwitchAsync(UsbDeviceFilter? removedFilter, UsbDeviceFilter? appearedFilter, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Comm.WaitForUsbDeviceModeSwitchAsync(removedFilter, appearedFilter, ResolveApiKind(), timeout, cancellationToken);

    /// <summary>
    /// Monitors ADB USB device arrivals and removals (FirmwareKit.Comm 1.1.0 monitor API).
    /// <para>监视 ADB USB 设备的新增与移除（FirmwareKit.Comm 1.1.0 监视 API）。</para>
    /// </summary>
    /// <param name="onChanged">Change callback. <para>设备变化回调。</para></param>
    /// <param name="pollInterval">Polling interval. <para>轮询间隔。</para></param>
    /// <param name="fireInitialSnapshot">Whether to emit initial Added events. <para>是否触发初始 Added 事件。</para></param>
    /// <param name="onError">Optional error callback. <para>可选错误回调。</para></param>
    /// <returns>A disposable monitor handle. <para>可释放的监视句柄。</para></returns>
    public static IDisposable MonitorAdbDevices(
        Action<IReadOnlyList<UsbDeviceChange>> onChanged,
        TimeSpan? pollInterval = null,
        bool fireInitialSnapshot = false,
        Action<Exception>? onError = null)
        => Comm.MonitorUsbDevices(onChanged, ResolveApiKind(), BuildAdbInterfaceFilter(), pollInterval, fireInitialSnapshot, onError);

    /// <summary>
    /// Opens an ADB USB device by its stable device key (mode-switch reopen).
    /// <para>按稳定设备键打开 ADB USB 设备（模式切换重开）。</para>
    /// </summary>
    /// <param name="deviceKey">The stable device key from <see cref="UsbDevice.DeviceKey"/>. <para>来自 <see cref="UsbDevice.DeviceKey"/> 的稳定设备键。</para></param>
    /// <returns>The opened device, or <c>null</c> when no device matches. <para>打开的设备；无匹配设备时返回 <c>null</c>。</para></returns>
    public static UsbDevice? OpenDeviceByKey(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            throw new ArgumentException("Device key must not be empty.", nameof(deviceKey));
        }

        var device = new CommUsbDevice(Comm, new UsbDeviceInfo { DeviceKey = deviceKey }, ForceLibUsb);
        if (device.CreateHandle() == 0)
        {
            return device;
        }

        device.Dispose();
        return null;
    }

    private static UsbDeviceFilter BuildAdbInterfaceFilter() => new()
    {
        InterfaceClass = AdbInterfaceClass,
        InterfaceSubClass = AdbInterfaceSubClass,
        InterfaceProtocol = AdbInterfaceProtocol,
    };

    private static UsbApiKind ResolveApiKind()
    {
        // Explicit --libusb wins; otherwise the platform default backend
        // (macOS/Linux → libusb, Windows → native) applies, see UsbBackendSelection.
        // <para>显式 --libusb 优先；否则应用平台默认后端（macOS/Linux → libusb、
        // Windows → 原生），见 UsbBackendSelection。</para>
        if (ForceLibUsb)
        {
            return UsbApiKind.LibUsbDotNet;
        }
        return UsbBackendSelection.ResolveDefault();
    }

    private static void AddDevices(
        IReadOnlyList<UsbDeviceInfo> discovered,
        UsbApiKind apiKind,
        HashSet<string> seenPaths,
        List<UsbDevice> result)
    {
        foreach (var info in discovered)
        {
            string key = string.IsNullOrWhiteSpace(info.DevicePath) ? info.DeviceKey : info.DevicePath;
            if (!string.IsNullOrWhiteSpace(key) && !seenPaths.Add(key))
            {
                continue;
            }

            var device = new CommUsbDevice(Comm, info, ForceLibUsb);
            if (device.CreateHandle() == 0)
            {
                result.Add(device);
            }
            else
            {
                device.Dispose();
            }
        }
    }
}
