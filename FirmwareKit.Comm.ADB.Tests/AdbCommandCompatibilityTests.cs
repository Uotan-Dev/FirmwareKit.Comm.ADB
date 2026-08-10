using System.Diagnostics;
using System.Text;
using FirmwareKit.Comm.ADB.Backend.Usb;

namespace FirmwareKit.Comm.ADB.Tests;

/// <summary>
/// Real-device command compatibility tests derived from adb_commands_dataset.txt
/// (extracted from UotanToolboxNT). Each non-destructive subcommand gets ONE test
/// case, run against BOTH a real USB device and a TCP emulator. Destructive or
/// environment-dependent commands (reboot*, sideload, pair, TWRP, partition tools,
/// dpm/device-owner) are intentionally excluded so no device is ever modified.
/// <para>依据 adb_commands_dataset.txt（提取自 UotanToolboxNT）设计的实机命令兼容
/// 测试。每个非破坏性子命令一个用例，同时针对真实 USB 设备与 TCP 模拟器运行。
/// 破坏性或依赖环境的命令（reboot 系列、sideload、pair、TWRP、分区工具、设备所有者）
/// 有意排除，确保不修改设备。</para>
/// </summary>
public class AdbCommandCompatibilityTests
{
    public enum AdbTarget { Usb, Emulator }

    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 16416;
    private const int TestTimeoutMs = 30000;

    // A real-device `bugreport` streams tens of megabytes and routinely takes well
    // over the default 30 s; give it a generous budget so it is not flaky on
    // otherwise healthy devices. <para>真机 `bugreport` 会流式输出数十 MB，耗时通常
    // 远超默认 30 秒；给其宽裕的预算以免在正常设备上误报超时。</para>
    private const int BugreportTimeoutMs = 180000;

    private static string Host => Environment.GetEnvironmentVariable("ADB_TEST_HOST") ?? DefaultHost;

    private static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("ADB_TEST_PORT"), out int p) ? p : DefaultPort;

    /// <summary>
    /// Isolates the `connect` state file so tests never touch the user's real
    /// saved endpoint. <para>隔离 `connect` 状态文件，测试不触及用户真实保存的端点。</para>
    /// </summary>
    static AdbCommandCompatibilityTests()
    {
        Environment.SetEnvironmentVariable(
            "ADB_CONNECT_FILE",
            Path.Combine(Path.GetTempPath(), $"fwkit_compat_connect_{Guid.NewGuid():N}.txt"));
    }

    /// <summary>
    /// Snapshot of whether a USB ADB device is attached when the class loads; USB
    /// cases skip when absent. <para>类加载时 USB ADB 设备是否在线的快照；无设备时
    /// USB 用例跳过。</para>
    /// NOTE: GetAllDevices() opens a session per device (claiming the interface), so
    /// the returned devices MUST be disposed here — otherwise the test host process
    /// holds the ADB interface for the whole run and every child CLI process fails to
    /// open it (winusb allows one handle per interface).
    /// <para>注意：GetAllDevices() 会为每个设备打开会话（claim 接口），因此这里
    /// 必须释放返回的设备——否则测试宿主进程会占用 ADB 接口整个运行周期，
    /// 每个子进程 CLI 都无法再打开它（winusb 每接口仅允许一个句柄）。</para>
    /// </summary>
    private static readonly bool UsbReady = CheckUsbReady();

    private static bool CheckUsbReady()
    {
        var devices = UsbManager.GetAllDevices();
        try
        {
            return devices.Count > 0;
        }
        finally
        {
            foreach (var d in devices)
            {
                d.Dispose();
            }
        }
    }

    // ---- 设备发现 / 服务器 ------------------------------------------------

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Devices_ListsAttached(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "devices");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("List of devices attached", r.Stdout);
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Mdns_Services(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "mdns", "services");
        Assert.Equal(0, r.ExitCode);
    });

    // `connect`/`disconnect` are TCP network verbs that target a host:port
    // endpoint; they have no meaning for a USB-attached device, so only the
    // emulator (TCP) case is exercised. <para>`connect`/`disconnect` 是面向
    // host:port 端点的 TCP 网络动词，对 USB 直连设备无意义，因此仅运行模拟器
    // （TCP）用例。</para>
    [Theory]
    [InlineData(AdbTarget.Emulator)]
    public Task Connect_Disconnect_RoundTrip(AdbTarget target) => RunWithTimeout(async () =>
    {
        string addr = $"{Host}:{Port}";
        try
        {
            CliResult c = await RunCliAsync(target, "connect", addr);
            Assert.Equal(0, c.ExitCode);
            Assert.Contains("connected to", c.Stdout);
        }
        finally
        {
            await RunCliAsync(target, "disconnect", $"{Host}:{Port}");
        }
    });

    // ---- 设备信息采集 (shell getprop/cat/uname) ---------------------------

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_GetProp_ProductModel(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "getprop", "ro.product.model");
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_WmSize(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "wm", "size");
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_DumpsysBattery(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "dumpsys", "battery");
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_Df_Data(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "df", "/data");
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_Ls_Tmp(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "--", "ls", "-la", "/data/local/tmp");
        Assert.Equal(0, r.ExitCode);
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_Uname_Kernel(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "--", "uname", "-r");
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_Cat_Uptime(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "cat", "/proc/uptime");
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    // ---- 输入事件 (Scrcpy keyevent，HOME 键，非破坏) ----------------------

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_Input_KeyeventHome(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "input", "keyevent", "3");
        Assert.Equal(0, r.ExitCode);
    });

    // ---- 应用管理 (预期错误路径 = 命令链路可用) ----------------------------

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_PmGrant_MissingPackage_Fails(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "pm", "grant",
            "com.example.nonexistent.fwkit", "android.permission.DUMP");
        Assert.NotEqual(0, r.ExitCode); // Package not found，但命令被正确执行
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_AmForceStop_MissingPackage(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "shell", "am", "force-stop",
            "com.example.nonexistent.fwkit");
        Assert.Equal(0, r.ExitCode); // am force-stop 对不存在的包仍返回 0
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Install_NonApk_FailsAsExpected(AdbTarget target) => RunWithTimeout(async () =>
    {
        string local = Path.Combine(Path.GetTempPath(), $"fwkit_compat_{Guid.NewGuid():N}.txt");
        File.WriteAllText(local, "not an apk");
        try
        {
            CliResult r = await RunCliAsync(target, "install", local);
            Assert.NotEqual(0, r.ExitCode); // 安装失败但命令链路可用
            Assert.False(string.IsNullOrWhiteSpace(r.Stdout + r.Stderr));
        }
        finally
        {
            File.Delete(local);
        }
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Uninstall_MissingPackage_FailsAsExpected(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "uninstall", "com.example.nonexistent.fwkit");
        Assert.NotEqual(0, r.ExitCode); // 删除失败但命令链路可用
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Root_Command_Reachable(AdbTarget target) => RunWithTimeout(async () =>
    {
        // 非 root 设备会返回错误；命令被正确识别并执行即为可用。
        CliResult r = await RunCliAsync(target, "root");
        Assert.True(r.ExitCode is 0 or 1, $"unexpected exit {r.ExitCode}");
    });

    // ---- 文件操作 (shell，/data/local/tmp 内临时目录，测后清理) -----------

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Shell_FileOps_MkdirTouchChmodCpMvRm(AdbTarget target) => RunWithTimeout(async () =>
    {
        string dir = $"/data/local/tmp/fwkit_compat_{Guid.NewGuid():N}";
        try
        {
            Assert.Equal(0, (await RunCliAsync(target, "shell", "--", "mkdir", "-p", dir)).ExitCode);
            Assert.Equal(0, (await RunCliAsync(target, "shell", "touch", $"{dir}/a.txt")).ExitCode);
            Assert.Equal(0, (await RunCliAsync(target, "shell", "chmod", "644", $"{dir}/a.txt")).ExitCode);
            Assert.Equal(0, (await RunCliAsync(target, "shell", "cp", $"{dir}/a.txt", $"{dir}/b.txt")).ExitCode);
            Assert.Equal(0, (await RunCliAsync(target, "shell", "mv", $"{dir}/b.txt", $"{dir}/c.txt")).ExitCode);

            CliResult ls = await RunCliAsync(target, "shell", "--", "ls", "-la", dir);
            Assert.Equal(0, ls.ExitCode);
            Assert.Contains("c.txt", ls.Stdout);
        }
        finally
        {
            await RunCliAsync(target, "shell", "--", "rm", "-rf", dir);
        }
    });

    // ---- push / pull ------------------------------------------------------

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task PushPull_RoundTrip(AdbTarget target) => RunWithTimeout(async () =>
    {
        string local = Path.Combine(Path.GetTempPath(), $"fwkit_compat_{Guid.NewGuid():N}.bin");
        string remote = $"/data/local/tmp/fwkit_compat_{Guid.NewGuid():N}.bin";
        byte[] payload = Encoding.UTF8.GetBytes("compat round-trip 0123456789");
        try
        {
            File.WriteAllBytes(local, payload);

            CliResult push = await RunCliAsync(target, "push", local, remote);
            Assert.Equal(0, push.ExitCode);
            Assert.Contains("pushed", push.Stdout);

            string pulled = Path.Combine(Path.GetTempPath(), $"fwkit_compat_pull_{Guid.NewGuid():N}.bin");
            try
            {
                CliResult pull = await RunCliAsync(target, "pull", remote, pulled);
                Assert.Equal(0, pull.ExitCode);
                Assert.Contains("pulled", pull.Stdout);
                Assert.Equal(payload, File.ReadAllBytes(pulled));
            }
            finally
            {
                File.Delete(pulled);
            }
        }
        finally
        {
            File.Delete(local);
            await RunCliAsync(target, "shell", "--", "rm", "-f", remote);
        }
    });

    // ---- 设备查询 / 日志 / 服务 -------------------------------------------

    // get-state / get-serialno / get-devpath 在 CLI 中仅支持 USB 目标（走 OpenTarget），
    // 因此只保留 USB 行。 <para>get-state/get-serialno/get-devpath are USB-only in the
    // CLI (they use OpenTarget), so only the USB case is kept.</para>
    [Theory]
    [InlineData(AdbTarget.Usb)]
    public Task GetState_PrintsDevice(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "get-state");
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    public Task GetSerialNo_PrintsSerial(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "get-serialno");
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    public Task GetDevPath_PrintsPath(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "get-devpath");
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Logcat_DumpBounded(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "logcat", "--", "-d", "-t", "5");
        Assert.Equal(0, r.ExitCode);
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Bugreport_Executes(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, BugreportTimeoutMs, "bugreport");
        Assert.Equal(0, r.ExitCode);
    }, BugreportTimeoutMs + 10000);

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Features_ListsShellV2(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "features");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("shell_v2", r.Stdout);
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task WaitForDevice_Reachable(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "wait-for-device");
        Assert.Equal(0, r.ExitCode);
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Reverse_List(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "reverse", "--list");
        Assert.Equal(0, r.ExitCode);
    });

    // ---- 本地命令 ---------------------------------------------------------

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Help_PrintsUsage(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "help");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("global options:", r.Stdout + r.Stderr);
    });

    [Theory]
    [InlineData(AdbTarget.Usb)]
    [InlineData(AdbTarget.Emulator)]
    public Task Version_PrintsBanner(AdbTarget target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, "version");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Android Debug Bridge version", r.Stdout);
    });

    /// <summary>
    /// Runs the test body with a hard timeout bound; a timeout surfaces as a
    /// TimeoutException instead of hanging the suite.
    /// <para>以硬性超时上限运行测试体；超时以 TimeoutException 呈现而非挂起套件。</para>
    /// </summary>
    private static Task RunWithTimeout(Func<Task> body, int timeoutMs = TestTimeoutMs)
        => body().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

    /// <summary>
    /// Spawns the CLI (the built adb.dll) as a child process. The emulator target
    /// prepends the global <c>-H/-P</c> options (accepted before the verb, like the
    /// official adb); the USB target relies on USB enumeration.
    /// <para>以子进程方式启动 CLI（构建出的 adb.dll）。模拟器目标前置全局
    /// <c>-H/-P</c> 选项（与官方 adb 一致允许在动词之前）；USB 目标走 USB 枚举。</para>
    /// </summary>
    private static async Task<CliResult> RunCliAsync(AdbTarget target, int timeoutMs, params string[] args)
    {
        if (target == AdbTarget.Usb && !UsbReady)
        {
            Assert.Skip("No USB ADB device attached; skipping USB case.");
        }

        string cliDll = Path.Combine(AppContext.BaseDirectory, "adb.dll");
        Assert.True(File.Exists(cliDll), $"CLI assembly not found at {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        if (target == AdbTarget.Emulator)
        {
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add(Host);
            psi.ArgumentList.Add("-P");
            psi.ArgumentList.Add(Port.ToString());
        }
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the CLI process.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }

            throw new TimeoutException($"CLI did not exit within {timeoutMs} ms: dotnet {string.Join(' ', args)}");
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static Task<CliResult> RunCliAsync(AdbTarget target, params string[] args)
        => RunCliAsync(target, TestTimeoutMs, args);

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
