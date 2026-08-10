using FirmwareKit.Comm.ADB.Backend.Tcp;
using FirmwareKit.Comm.ADB.Protocol;
using FirmwareKit.Comm.ADB.Services;
using System.Security.Cryptography;
using System.Text;

namespace FirmwareKit.Comm.ADB.Tests;

/// <summary>
/// Integration tests against a live emulator/device over TCP (adb connect host:port).
/// These exercise the real library logic (handshake, AUTH, shell v2, sync, device
/// services) against a running device. Override the target with the
/// ADB_TEST_HOST / ADB_TEST_PORT environment variables.
/// <para>针对真实模拟器 / 设备的 TCP 集成测试（adb connect host:port）。
/// 在真实设备上演练库的实际逻辑（握手、认证、shell v2、sync、设备服务）。
/// 可通过 ADB_TEST_HOST / ADB_TEST_PORT 环境变量覆盖目标。</para>
/// </summary>
public class DeviceIntegrationTests
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 16416;

    /// <summary>
    /// Hard bound for a single test run (30s), so a stalled device cannot hang
    /// the whole suite. <para>单次测试运行的硬性上限（30 秒），防止设备无响应时挂起整个套件。</para>
    /// </summary>
    private const int TestTimeoutMs = 30000;

    private static string Host => Environment.GetEnvironmentVariable("ADB_TEST_HOST") ?? DefaultHost;

    private static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("ADB_TEST_PORT"), out int p) ? p : DefaultPort;

    [Fact]
    public Task Connect_HandshakesWithEmulator() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();

        Assert.True(session.Connection.IsConnected);
        Assert.True(session.Connection.PeerVersion >= AdbProtocol.Version);
        Assert.False(string.IsNullOrEmpty(session.Connection.PeerBanner));
        Assert.Contains("shell_v2", session.Connection.PeerFeatures);
        Assert.Contains("sendrecv_v2", session.Connection.PeerFeatures);
    });

    [Fact]
    public Task Shell_RunsGetProp_OnAndroid15() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var shell = new AdbShellClient(session.Connection, "getprop ro.build.version.release");

        ShellResult result = shell.Execute();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("15", Encoding.UTF8.GetString(result.Stdout));
    });

    [Fact]
    public Task Shell_ReportsExitCode() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var shell = new AdbShellClient(session.Connection, "sh -c 'exit 7'");

        ShellResult result = shell.Execute();

        Assert.Equal(7, result.ExitCode);
    });

    [Fact]
    public Task Sync_PushStatPullRoundTrip() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        using var sync = new AdbSyncClient(session.Connection);
        const string remote = "/data/local/tmp/fwkit_integration_test.bin";
        byte[] payload = Encoding.UTF8.GetBytes("FirmwareKit.Comm.ADB integration payload 0123456789");

        try
        {
            using var source = new MemoryStream(payload);
            sync.PushStream(source, remote);

            SyncEntry? stat = sync.Stat(remote);
            Assert.NotNull(stat);
            Assert.Equal(payload.Length, (int)stat!.Value.Size);
            Assert.Equal("fwkit_integration_test.bin", stat.Value.Name);

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
    public Task Sync_ListDirectory_ContainsPushedFile() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        using var sync = new AdbSyncClient(session.Connection);
        const string remote = "/data/local/tmp/fwkit_list_test.bin";
        byte[] payload = Encoding.UTF8.GetBytes("list-me");

        try
        {
            using var source = new MemoryStream(payload);
            sync.PushStream(source, remote);

            IReadOnlyList<SyncEntry> entries = sync.List("/data/local/tmp");

            Assert.Contains(entries, e => e.Name == "fwkit_list_test.bin");
        }
        finally
        {
            Cleanup(session, remote);
        }
    });

    [Fact]
    public Task DeviceServices_GetProp_MatchesShell() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var services = new AdbDeviceServices(session.Connection);
        string viaServices = services.GetProp("ro.product.model");

        var shell = new AdbShellClient(session.Connection, "getprop ro.product.model");
        ShellResult result = shell.Execute();
        string viaShell = Encoding.UTF8.GetString(result.Stdout).Trim();

        Assert.False(string.IsNullOrEmpty(viaServices));
        Assert.Equal(viaShell, viaServices);
    });

    [Fact]
    public Task Shell_CapturesStderrAndExitCode() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var shell = new AdbShellClient(
            session.Connection, "sh -c 'echo OUT-LINE; echo ERR-LINE >&2; exit 3'");

        ShellResult result = shell.Execute();

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("OUT-LINE", Encoding.UTF8.GetString(result.Stdout));
        Assert.Contains("ERR-LINE", Encoding.UTF8.GetString(result.Stderr));
    });

    [Fact]
    public Task Shell_StreamsStdoutViaCallback() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var shell = new AdbShellClient(
            session.Connection, "for i in 1 2 3; do echo s$i; done");

        var stdout = new StringBuilder();
        int exitCode = shell.ExecuteStreaming(
            chunk => stdout.Append(Encoding.UTF8.GetString(chunk)),
            _ => { });

        Assert.Equal(0, exitCode);
        string text = stdout.ToString();
        Assert.Contains("s1", text);
        Assert.Contains("s2", text);
        Assert.Contains("s3", text);
    });

    [Fact]
    public Task Shell_ExecuteTimeout_Throws() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var shell = new AdbShellClient(session.Connection, "sleep 60");

        // A command that produces no output must not block forever: a bounded
        // read surfaces as TimeoutException.
        // <para>不产生输出的命令不能永久阻塞：有界读取应以 TimeoutException 呈现。</para>
        Assert.Throws<TimeoutException>(() => shell.Execute(timeoutMs: 1000));
    });

    [Fact]
    public Task Shell_PtyMode_ReturnsOutput() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var shell = new AdbShellClient(session.Connection, "echo PTY-LINE", pty: true);

        ShellResult result = shell.Execute();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PTY-LINE", Encoding.UTF8.GetString(result.Stdout));
    });

    [Fact]
    public Task Shell_Term_IsPropagated() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var shell = new AdbShellClient(session.Connection, "echo TERM=$TERM", term: "xterm-256color");

        ShellResult result = shell.Execute();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("xterm-256color", Encoding.UTF8.GetString(result.Stdout));
    });

    [Fact]
    public Task Shell_EmptyOutput_ExitZero() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var shell = new AdbShellClient(session.Connection, "true");

        ShellResult result = shell.Execute();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
    });

    [Fact]
    public Task Sync_StatMissingFile_ReturnsZeroedEntry() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        using var sync = new AdbSyncClient(session.Connection);
        const string remote = "/data/local/tmp/fwkit_missing_file_xyz";

        SyncEntry? stat = sync.Stat(remote);

        // The v1 wire protocol reports a failed lstat as a zeroed entry rather
        // than a FAIL message.
        // <para>v1 线上协议将失败的 lstat 报告为零值条目，而非 FAIL 消息。</para>
        Assert.NotNull(stat);
        Assert.Equal(0u, stat!.Value.Size);
        Assert.Equal(0u, stat.Value.Mode);
        Assert.Equal("fwkit_missing_file_xyz", stat.Value.Name);
    });

    [Fact]
    public Task Sync_ListMissingDirectory_ReturnsEmpty() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        using var sync = new AdbSyncClient(session.Connection);

        IReadOnlyList<SyncEntry> entries = sync.List("/data/local/tmp/fwkit_missing_dir_xyz");

        Assert.Empty(entries);
    });

    [Fact]
    public Task Sync_BinaryRoundTrip() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        using var sync = new AdbSyncClient(session.Connection);
        const string remote = "/data/local/tmp/fwkit_binary_test.bin";
        byte[] payload = new byte[512];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i; // every byte value, including NUL and non-UTF8
        }

        try
        {
            using var source = new MemoryStream(payload);
            sync.PushStream(source, remote);

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
    public Task Sync_LargeRoundTrip_MultiChunk() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        using var sync = new AdbSyncClient(session.Connection);
        const string remote = "/data/local/tmp/fwkit_large_test.bin";
        // > 64 KiB so the transfer spans multiple DATA packets.
        // <para>超过 64 KiB，使传输跨越多个 DATA 数据包。</para>
        byte[] payload = new byte[300 * 1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31);
        }

        try
        {
            using var source = new MemoryStream(payload);
            sync.PushStream(source, remote);

            SyncEntry? stat = sync.Stat(remote);
            Assert.NotNull(stat);
            Assert.Equal(payload.Length, (int)stat!.Value.Size);

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
    public Task Sync_EmptyFileRoundTrip() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        using var sync = new AdbSyncClient(session.Connection);
        const string remote = "/data/local/tmp/fwkit_empty_test.bin";

        try
        {
            using var source = new MemoryStream(Array.Empty<byte>());
            sync.PushStream(source, remote);

            SyncEntry? stat = sync.Stat(remote);
            Assert.NotNull(stat);
            Assert.Equal(0, (int)stat!.Value.Size);

            using var dest = new MemoryStream();
            sync.PullStream(remote, dest);
            Assert.Empty(dest.ToArray());
        }
        finally
        {
            Cleanup(session, remote);
        }
    });

    [Fact]
    public Task Sync_PushPullLocalFiles() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        using var sync = new AdbSyncClient(session.Connection);
        const string remote = "/data/local/tmp/fwkit_local_files.bin";
        string local = Path.Combine(Path.GetTempPath(), $"fwkit_push_{Guid.NewGuid():N}.bin");
        string pulled = Path.Combine(Path.GetTempPath(), $"fwkit_pull_{Guid.NewGuid():N}.bin");
        byte[] payload = Encoding.UTF8.GetBytes("local-file round trip 0123456789");

        try
        {
            File.WriteAllBytes(local, payload);
            sync.Push(local, remote);

            sync.Pull(remote, pulled);

            Assert.Equal(payload, File.ReadAllBytes(pulled));
        }
        finally
        {
            File.Delete(local);
            File.Delete(pulled);
            Cleanup(session, remote);
        }
    });

    [Fact]
    public Task Sync_CloseThenReuse_ReopensStream() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        using var sync = new AdbSyncClient(session.Connection);
        const string remoteA = "/data/local/tmp/fwkit_close_a.bin";
        const string remoteB = "/data/local/tmp/fwkit_close_b.bin";

        try
        {
            using (var source = new MemoryStream(Encoding.UTF8.GetBytes("first")))
            {
                sync.PushStream(source, remoteA);
            }

            sync.Close(); // closing the service stream must not poison the client
                          // <para>关闭服务流不应使客户端失效。</para>

            using (var source = new MemoryStream(Encoding.UTF8.GetBytes("second")))
            {
                sync.PushStream(source, remoteB);
            }

            Assert.NotNull(sync.Stat(remoteA));
            Assert.NotNull(sync.Stat(remoteB));
        }
        finally
        {
            Cleanup(session, remoteA);
            Cleanup(session, remoteB);
        }
    });

    [Fact]
    public Task DeviceServices_RunService_ReturnsOutput() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();
        var services = new AdbDeviceServices(session.Connection);

        string output = services.RunService("shell:echo RUNSVC-LINE");

        Assert.Contains("RUNSVC-LINE", output);
    });

    [Fact]
    public Task ConcurrentStreams_MultiplexOnOneConnection() => RunWithTimeout(() =>
    {
        using DeviceSession session = DeviceSession.Open();

        Task<string> taskA = Task.Run(() =>
        {
            var shell = new AdbShellClient(session.Connection, "echo AAA");
            return Encoding.UTF8.GetString(shell.Execute().Stdout).Trim();
        });

        Task<string> taskB = Task.Run(() =>
        {
            var shell = new AdbShellClient(session.Connection, "echo BBB");
            return Encoding.UTF8.GetString(shell.Execute().Stdout).Trim();
        });

        Task.WaitAll(taskA, taskB);

        Assert.Equal("AAA", taskA.Result);
        Assert.Equal("BBB", taskB.Result);
    });

    /// <summary>
    /// Runs the test body on a worker thread with a hard 30s bound; a timeout
    /// surfaces as a TimeoutException instead of hanging the suite.
    /// <para>在后台线程上运行测试体并施加 30 秒硬性上限；超时以
    /// TimeoutException 呈现，而不是挂起整个套件。</para>
    /// </summary>
    private static Task RunWithTimeout(Action body)
        => Task.Run(body).WaitAsync(TimeSpan.FromMilliseconds(TestTimeoutMs));

    private static void Cleanup(DeviceSession session, string remotePath)
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
    /// Opens a real connection to the emulator, authenticating with the device's
    /// trusted key (~/.android/adbkey) so that ro.adb.secure devices accept us.
    /// <para>打开到模拟器的真实连接，使用设备信任的密钥（~/.android/adbkey）认证，
    /// 以便 ro.adb.secure 设备接受我们。</para>
    /// </summary>
    private sealed class DeviceSession : IDisposable
    {
        public AdbConnection Connection { get; }

        private DeviceSession(AdbConnection connection) => Connection = connection;

        public static DeviceSession Open()
        {
            try
            {
                var transport = new AdbTcpTransport(Host, Port, connectTimeoutMs: 5000);
                AdbAuthentication auth = LoadTrustedAuthentication();
                AdbConnection? connection = null;

                try
                {
                    connection = new AdbConnection(transport, auth);
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
                            $"ADB handshake with {Host}:{Port} did not complete within 10s. " +
                            "The device may reject the authentication key (ro.adb.secure=1).");
                    }

                    return new DeviceSession(connection);
                }
                catch
                {
                    connection?.Dispose(); // also disposes auth + transport
                    throw;
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Cannot reach ADB device at {Host}:{Port}. Start the emulator and ensure " +
                    $"'adb connect {Host}:{Port}' succeeds, or set ADB_TEST_HOST/ADB_TEST_PORT.", ex);
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
