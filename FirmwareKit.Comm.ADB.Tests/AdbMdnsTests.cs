using FirmwareKit.Comm.ADB.Backend.Udp;
using Makaretu.Dns;
using System.Collections.Concurrent;
using System.Net;

namespace FirmwareKit.Comm.ADB.Tests;

/// <summary>
/// Unit tests for the mDNS discovery mapping (built on Makaretu.Dns.Multicast):
/// feeds constructed DNS messages into the accumulator and asserts the devices
/// it produces — no network required.
/// <para>mDNS 发现映射逻辑的单元测试（基于 Makaretu.Dns.Multicast）：
/// 将构造的 DNS 报文喂给累积器并断言其产出的设备，无需网络。</para>
/// </summary>
public class AdbMdnsTests
{
    [Fact]
    public void AccumulateAndBuild_MapRecordsToDevice()
    {
        var message = new Message();
        message.Answers.Add(new PTRRecord
        {
            Name = new DomainName(AdbMdnsDiscovery.AdbTlsService),
            DomainName = new DomainName("MyPhone._adb-tls-connect._tcp.local"),
        });
        message.Answers.Add(new SRVRecord
        {
            Name = new DomainName("MyPhone._adb-tls-connect._tcp.local"),
            Port = 5555,
            Target = new DomainName("MyPhone.local"),
        });
        message.Answers.Add(new TXTRecord
        {
            Name = new DomainName("MyPhone._adb-tls-connect._tcp.local"),
            Strings = new List<string> { "model=Pixel", "serial=ABC123" },
        });
        message.Answers.Add(new ARecord
        {
            Name = new DomainName("MyPhone.local"),
            Address = IPAddress.Parse("192.168.1.50"),
        });

        var byInstance = new ConcurrentDictionary<string, AdbMdnsDiscovery.DeviceAccumulator>(StringComparer.OrdinalIgnoreCase);
        var addressesByHost = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AdbMdnsDiscovery.Accumulate(message, byInstance, addressesByHost);
        IReadOnlyList<MdnsDevice> devices = AdbMdnsDiscovery.BuildDevices(byInstance, addressesByHost);

        MdnsDevice device = Assert.Single(devices);
        Assert.Equal("MyPhone._adb-tls-connect._tcp.local", device.ServiceInstanceName);
        Assert.Equal("MyPhone.local", device.HostName);
        Assert.Equal(5555, device.Port);
        Assert.Equal("192.168.1.50", Assert.Single(device.Addresses));
        Assert.Equal("Pixel", device.Properties["model"]);
        Assert.Equal("ABC123", device.Properties["serial"]);
    }

    [Fact]
    public void BuildDevices_FiltersNonAdbServices()
    {
        var message = new Message();
        message.Answers.Add(new PTRRecord
        {
            Name = new DomainName(AdbMdnsDiscovery.AdbTlsService),
            DomainName = new DomainName("PhoneA._adb-tls-connect._tcp.local"),
        });
        message.Answers.Add(new PTRRecord
        {
            Name = new DomainName("_airplay._tcp.local"),
            DomainName = new DomainName("Other._airplay._tcp.local"),
        });
        message.Answers.Add(new SRVRecord
        {
            Name = new DomainName("Other._airplay._tcp.local"),
            Port = 7000,
            Target = new DomainName("Other.local"),
        });

        var byInstance = new ConcurrentDictionary<string, AdbMdnsDiscovery.DeviceAccumulator>(StringComparer.OrdinalIgnoreCase);
        var addressesByHost = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AdbMdnsDiscovery.Accumulate(message, byInstance, addressesByHost);
        IReadOnlyList<MdnsDevice> devices = AdbMdnsDiscovery.BuildDevices(byInstance, addressesByHost);

        MdnsDevice device = Assert.Single(devices);
        Assert.Equal("PhoneA._adb-tls-connect._tcp.local", device.ServiceInstanceName);
    }

    [Fact]
    public void BuildDevices_DefaultsPortAndToleratesMissingAddress()
    {
        var message = new Message();
        message.Answers.Add(new PTRRecord
        {
            Name = new DomainName(AdbMdnsDiscovery.AdbLegacyService),
            DomainName = new DomainName("Old._adb._tcp.local"),
        });

        var byInstance = new ConcurrentDictionary<string, AdbMdnsDiscovery.DeviceAccumulator>(StringComparer.OrdinalIgnoreCase);
        var addressesByHost = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AdbMdnsDiscovery.Accumulate(message, byInstance, addressesByHost);
        IReadOnlyList<MdnsDevice> devices = AdbMdnsDiscovery.BuildDevices(byInstance, addressesByHost);

        MdnsDevice device = Assert.Single(devices);
        Assert.Equal(5555, device.Port); // no SRV record → adb default port
        Assert.Empty(device.Addresses); // no A/AAAA record → no address
    }
}
