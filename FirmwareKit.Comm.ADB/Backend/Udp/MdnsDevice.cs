namespace FirmwareKit.Comm.ADB.Backend.Udp;

/// <summary>
/// A device discovered on the local network via mDNS.
/// <para>通过 mDNS 在本地网络发现的设备。</para>
/// </summary>
public sealed class MdnsDevice
{
    /// <summary>
    /// Gets the full service instance name, e.g. "ALN-AL00._adb-tls-connect._tcp.local".
    /// <para>获取完整服务实例名，如 "ALN-AL00._adb-tls-connect._tcp.local"。</para>
    /// </summary>
    public string ServiceInstanceName { get; }

    /// <summary>
    /// Gets the SRV target host name, e.g. "ALN-AL00.local".
    /// <para>获取 SRV 目标主机名，如 "ALN-AL00.local"。</para>
    /// </summary>
    public string? HostName { get; }

    /// <summary>
    /// Gets the adbd TCP port from the SRV record (defaults to 5555).
    /// <para>获取 SRV 记录中的 adbd TCP 端口（默认 5555）。</para>
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets the resolved IP addresses for the device.
    /// <para>获取解析出的设备 IP 地址。</para>
    /// </summary>
    public IReadOnlyList<string> Addresses { get; }

    /// <summary>
    /// Gets the TXT-record properties (e.g. "model", "device", "serial").
    /// <para>获取 TXT 记录属性（如 "model"、"device"、"serial"）。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; }

    /// <summary>
    /// Initializes a new discovered device.
    /// <para>初始化一个新的发现设备。</para>
    /// </summary>
    public MdnsDevice(
        string serviceInstanceName,
        string? hostName,
        int port,
        IReadOnlyList<string> addresses,
        IReadOnlyDictionary<string, string> properties)
    {
        ServiceInstanceName = serviceInstanceName ?? throw new ArgumentNullException(nameof(serviceInstanceName));
        HostName = hostName;
        Port = port;
        Addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        Properties = properties ?? throw new ArgumentNullException(nameof(properties));
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{ServiceInstanceName} {string.Join(",", Addresses)}:{Port}";
}
