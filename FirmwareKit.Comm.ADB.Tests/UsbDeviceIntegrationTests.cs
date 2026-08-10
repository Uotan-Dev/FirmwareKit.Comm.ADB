using FirmwareKit.Comm.ADB.Backend.Usb;
using FirmwareKit.Comm.ADB.Protocol;
using FirmwareKit.Comm.ADB.Services;
using System.Security.Cryptography;
using System.Text;

namespace FirmwareKit.Comm.ADB.Tests;

/// <summary>
/// Integration tests against a REAL USB-attached ADB device (winUSB / libusb).
/// They exercise the whole USB stack: enumeration, session open, CNXN handshake,
/// shell v2, sync and device services over an actual USB transport. When no ADB
/// device is attached the tests are skipped via Assert.Skip so the suite still
/// passes on machines without hardware. Tests within the class run serially
/// (xunit default) so concurrent USB sessions never contend for the device.
/// <para>针对真实 USB 连接的 ADB 设备（winUSB / libusb）的集成测试，覆盖完整
/// USB 链路：枚举、会话打开、CNXN 握手、shell v2、sync 与设备服务。未连接 ADB
/// 设备时通过 Assert.Skip 跳过，保证无硬件的机器上套件仍可通过。类内测试串行
/// 执行（xunit 默认），避免并发 USB 会话争抢设备。</para>
/// </summary>
public class UsbDeviceIntegrationTests
{
    /// <summary>
    /// Hard bound for a single test run (30s), so a stalled device cannot hang
    /// the whole suite. <para>单次测试运行的硬性上限（30 秒），防止设备无响应时挂起整个套件。</para>
    /// </summary>
    private const int TestTimeoutMs = 30000;

    [Fact]
    public Task Usb_EnumeratesAttachedDevice() => RunWithTimeout(() =>
    {
        var devices = UsbManager.GetAllDevices();
        try
        {
            Assert.SkipWhen(devices.Count == 0, "No USB ADB device attached.");
            Assert.Single(devices);

            UsbDevice device = devices[0];
            Assert.False(string.IsNullOrWhiteSpace(device.SerialNumber));
            Assert.NotEqual(0, device.VendorId);
        }
        finally
        {
            foreach (var device in devices)
            {
                device.Dispose();
            }
        }
    });

    [Fact]
    public Task Usb_Handshake_Completes() => RunWithTimeout(() =>
    {
        using UsbSession session = UsbSession.Open();

        Assert.True(session.Connection.IsConnected);
        Assert.True(session.Connection.PeerVersion >= AdbProtocol.Version);
        Assert.False(string.IsNullOrEmpty(session.Connection.PeerBanner));
        // shell_v2 is required; other features (sendrecv_v2, ...) are device/version
        // dependent and must not be asserted unconditionally.
        // <para>shell_v2 为必需；其余特性与设备/版本相关，不硬性断言。</para>
        Assert.Contains("shell_v2", session.Connection.PeerFeatures);
    });

    [Fact]
    public Task Usb_Shell_RunsGetProp() => RunWithTimeout(() =>
    {
        using UsbSession session = UsbSession.Open();
        var shell = new AdbShellClient(session.Connection, "getprop ro.build.version.release");

        ShellResult result = shell.Execute();

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(result.Stdout)));
    });

    [Fact]
    public Task Usb_Shell_ReportsExitCode() => RunWithTimeout(() =>
    {
        using UsbSession session = UsbSession.Open();
        var shell = new AdbShellClient(session.Connection, "sh -c 'exit 7'");

        ShellResult result = shell.Execute();

        Assert.Equal(7, result.ExitCode);
    });

    [Fact]
    public Task Usb_Shell_CapturesStderrAndExitCode() => RunWithTimeout(() =>
    {
        using UsbSession session = UsbSession.Open();
        var shell = new AdbShellClient(
            session.Connection, "sh -c 'echo OUT-LINE; echo ERR-LINE >&2; exit 3'");

        ShellResult result = shell.Execute();

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("OUT-LINE", Encoding.UTF8.GetString(result.Stdout));
        Assert.Contains("ERR-LINE", Encoding.UTF8.GetString(result.Stderr));
    });

    [Fact]
    public Task Usb_Sync_PushStatPullRoundTrip() => RunWithTimeout(() =>
    {
        using UsbSession session = UsbSession.Open();
        using var sync = new AdbSyncClient(session.Connection);
        const string remote = "/data/local/tmp/fwkit_usb_integration_test.bin";
        byte[] payload = Encoding.UTF8.GetBytes("USB integration payload 0123456789");

        try
        {
            using var source = new MemoryStream(payload);
            sync.PushStream(source, remote);

            SyncEntry? stat = sync.Stat(remote);
            Assert.NotNull(stat);
            Assert.Equal(payload.Length, (int)stat!.Value.Size);
            Assert.Equal("fwkit_usb_integration_test.bin", stat.Value.Name);

            using var dest = new MemoryStream();
            sync.PullStream(remote, dest);
            Assert.Equal(payload, dest.ToArray());
        }
        finally
        {
            Cleanup(session, remote);
        }
    });

    [Fact]
    public Task Usb_DeviceServices_GetProp_MatchesShell() => RunWithTimeout(() =>
    {
        using UsbSession session = UsbSession.Open();
        var services = new AdbDeviceServices(session.Connection);
        string viaServices = services.GetProp("ro.product.model");

        var shell = new AdbShellClient(session.Connection, "getprop ro.product.model");
        ShellResult result = shell.Execute();
        string viaShell = Encoding.UTF8.GetString(result.Stdout).Trim();

        Assert.False(string.IsNullOrEmpty(viaServices));
        Assert.Equal(viaShell, viaServices);
    });

    /// <summary>
    /// Runs the test body with a hard 30s bound; a timeout surfaces as a
    /// TimeoutException instead of hanging the suite.
    /// <para>以 30 秒硬性上限运行测试体；超时以 TimeoutException 呈现，而不是挂起整个套件。</para>
    /// </summary>
    private static Task RunWithTimeout(Action body)
        => Task.Run(body).WaitAsync(TimeSpan.FromMilliseconds(TestTimeoutMs));

    private static void Cleanup(UsbSession session, string remotePath)
    {
        try
        {
            var shell = new AdbShellClient(session.Connection, $"rm -f '{remotePath}'");
            shell.Execute();
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    /// <summary>
    /// Opens a real ADB connection over USB: enumerates the attached ADB device,
    /// opens it through FirmwareKit.Comm (libusb), and completes the CNXN handshake
    /// using the device's trusted key (~/.android/adbkey) for ro.adb.secure devices.
    /// Skips the test when no USB ADB device is present.
    /// <para>通过 USB 打开真实 ADB 连接：枚举已连接的 ADB 设备，经 FirmwareKit.Comm
    /// （libusb）打开并完成 CNXN 握手，使用设备信任的密钥（~/.android/adbkey）以
    /// 通过 ro.adb.secure 设备认证。无 USB ADB 设备时跳过测试。</para>
    /// </summary>
    private sealed class UsbSession : IDisposable
    {
        public AdbConnection Connection { get; }

        private UsbSession(AdbConnection connection) => Connection = connection;

        public static UsbSession Open()
        {
            var devices = UsbManager.GetAllDevices();
            try
            {
                if (devices.Count == 0)
                {
                    Assert.Skip("No USB ADB device attached; skipping USB integration test.");
                }

                UsbDevice device = devices[0];
                AdbAuthentication auth = LoadTrustedAuthentication();
                AdbConnection? connection = null;

                try
                {
                    connection = new AdbConnection(device, auth);
                    connection.Connect();

                    // Wait for the peer CNXN banner (auth may need round trips).
                    int waited = 0;
                    while (connection.PeerVersion == 0 && waited < 200)
                    {
                        Thread.Sleep(50);
                        waited++;
                    }

                    if (connection.PeerVersion == 0)
                    {
                        throw new InvalidOperationException(
                            "ADB handshake over USB did not complete within 10s. " +
                            "The device may reject the authentication key (ro.adb.secure=1).");
                    }

                    return new UsbSession(connection);
                }
                catch
                {
                    connection?.Dispose(); // also disposes auth + transport (the UsbDevice)
                    throw;
                }
            }
            catch
            {
                // CommUsbDevice.Dispose is idempotent, so double-disposing the
                // device handed to the connection is safe.
                // <para>CommUsbDevice.Dispose 幂等，双重释放已交给连接的设备是安全的。</para>
                foreach (var device in devices)
                {
                    device.Dispose();
                }

                throw;
            }
        }

        private static AdbAuthentication LoadTrustedAuthentication()
        {
            string keyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "adbkey");
            if (File.Exists(keyPath))
            {
                var rsa = RSA.Create();
                rsa.ImportFromPem(File.ReadAllText(keyPath));
                return new AdbAuthentication(rsa);
            }

            return AdbAuthentication.CreateNew();
        }

        public void Dispose() => Connection.Dispose();
    }
}
