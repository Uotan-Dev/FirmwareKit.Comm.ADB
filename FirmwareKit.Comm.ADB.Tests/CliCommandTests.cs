using System.Diagnostics;
using System.Text;
using FirmwareKit.Comm.ADB.Backend.Usb;

namespace FirmwareKit.Comm.ADB.Tests;

/// <summary>
/// End-to-end CLI tests that spawn the built <c>adb</c> binary against a real USB
/// device and/or a TCP emulator. Each test runs against both targets when the
/// command makes sense for both. Replaces the former CliIntegrationTests and
/// AdbCommandCompatibilityTests (which had substantial overlap).
/// <para>以子进程方式启动构建出的 <c>adb</c>，针对真实 USB 设备和/或 TCP 模拟器
/// 执行的端到端 CLI 测试。每个用例在命令对两类目标都适用时同时运行。合并了原先
/// 大量重复的 CliIntegrationTests 与 AdbCommandCompatibilityTests。</para>
/// </summary>
public class CliCommandTests
{
    public enum Target { Usb, Emulator }

    private const int TimeoutMs = 30_000;
    private const int BugreportMs = 180_000;

    private static string Host => Environment.GetEnvironmentVariable("ADB_TEST_HOST") ?? "127.0.0.1";
    private static int Port => int.TryParse(Environment.GetEnvironmentVariable("ADB_TEST_PORT"), out int p) ? p : 16416;

    static CliCommandTests()
    {
        Environment.SetEnvironmentVariable(
            "ADB_CONNECT_FILE",
            Path.Combine(Path.GetTempPath(), $"fwkit_cmd_connect_{Guid.NewGuid():N}.txt"));
    }

    // USB availability is checked once per class. GetAllDevices() opens a session
    // per device (claiming the interface), so the devices MUST be disposed or the
    // test host holds the ADB interface for the whole run and child CLI processes
    // fail to open it (winusb allows one handle per interface).
    private static readonly bool UsbReady = CheckUsbReady();

    private static bool CheckUsbReady()
    {
        var devices = UsbManager.GetAllDevices();
        try { return devices.Count > 0; }
        finally { foreach (var d in devices) d.Dispose(); }
    }

    // ---- local commands (no target) --------------------------------------

    [Fact]
    public Task Version() => Run(Target.Emulator, "version", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Android Debug Bridge version", r.Stdout);
        Assert.Contains("Running on", r.Stdout);
    });

    [Fact]
    public Task VersionFlag() => RunNoTarget("--version", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Android Debug Bridge version", r.Stdout);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Help(Target target) => Run(target, "help", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("global options:", r.Stderr);
        Assert.Contains("devices [-l]", r.Stderr);
    });

    [Fact]
    public Task NoArgs_ShowsUsage() => RunNoTarget(null, r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("global options:", r.Stderr);
    });

    [Fact]
    public Task UnknownCommand_Errors() => RunNoTarget("frobnicate", r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("unknown command frobnicate", r.Stderr);
    });

    [Fact]
    public Task UnknownGlobalOption_Errors() => RunNoTarget("-z", "devices", r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("unknown option -z", r.Stderr);
    });

    [Fact]
    public Task MissingSerialArgument_Errors() => RunNoTarget("-s", r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("missing argument", r.Stderr);
    });

    [Fact]
    public Task InvalidPort_Errors() => RunNoTarget("-P", "notaport", "shell", "x", r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("invalid port", r.Stderr);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Devices(Target target) => Run(target, "devices", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("List of devices attached", r.Stdout);
        Assert.Contains("\tdevice", r.Stdout);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Devices_Long(Target target) => Run(target, "devices", "-l", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("transport_id:", r.Stdout);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Mdns_Services(Target target) => Run(target, "mdns", "services", r =>
    {
        Assert.Equal(0, r.ExitCode);
    });

    // ---- networking (emulator only) --------------------------------------

    [Fact]
    public Task Connect_Disconnect() => RunWithTimeout(async () =>
    {
        string addr = $"{Host}:{Port}";
        try
        {
            CliResult c = await RunCliAsync(Target.Emulator, "connect", addr);
            Assert.Equal(0, c.ExitCode);
            Assert.Contains("connected to", c.Stdout);

            CliResult shell = await RunCliAsync(Target.Emulator, "shell", "getprop", "ro.build.version.release");
            Assert.Equal(0, shell.ExitCode);

            CliResult devices = await RunCliAsync(Target.Emulator, "devices");
            Assert.Contains(addr, devices.Stdout);
        }
        finally
        {
            await RunCliAsync(Target.Emulator, "disconnect", addr);
        }
    });

    // ---- shell -----------------------------------------------------------

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_GetProp(Target target) => Run(target, "shell", "getprop", "ro.product.model", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_WmSize(Target target) => Run(target, "shell", "wm", "size", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Physical size", r.Stdout);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_DumpsysBattery(Target target) => Run(target, "shell", "dumpsys", "battery", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Battery", r.Stdout);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_Df(Target target) => Run(target, "shell", "df", "/data", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Filesystem", r.Stdout);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_Uname(Target target) => Run(target, "shell", "--", "uname", "-r", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_Pipe(Target target) => Run(target, "shell", "cat", "/proc/meminfo", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("MemTotal", r.Stdout);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_RemoteExitCode(Target target) => Run(target, "shell", "sh", "-c", "exit 7", r =>
    {
        Assert.Equal(7, r.ExitCode);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_StdoutStderrSeparation(Target target) => Run(target,
        "shell", "sh", "-c", "echo OUT; echo ERR >&2", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("OUT", r.Stdout);
        Assert.Contains("ERR", r.Stderr);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_NoExitCode(Target target) => Run(target, "shell", "-x", "sh", "-c", "exit 7", r =>
    {
        Assert.Equal(0, r.ExitCode);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_GlobalOptionsAfterVerb(Target target) => Run(target,
        "shell", "-H", Host, "-P", Port.ToString(), "getprop", "ro.build.version.release", r =>
    {
        // -H/-P after the verb must be bound as globals, not forwarded to sh.
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    }, Target.Emulator); // only meaningful for TCP targets; USB ignores -H/-P

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_PmGrant_MissingPackage(Target target) => Run(target, "shell",
        "pm", "grant", "com.example.nonexistent.fwkit", "android.permission.DUMP", r =>
    {
        Assert.NotEqual(0, r.ExitCode);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_InputKeyeventHome(Target target) => Run(target, "shell", "input", "keyevent", "3", r =>
    {
        Assert.Equal(0, r.ExitCode);
    });

    // ---- file transfer ---------------------------------------------------

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task PushPull_RoundTrip(Target target) => RunWithTimeout(async () =>
    {
        string local = Path.Combine(Path.GetTempPath(), $"fwkit_cmd_{Guid.NewGuid():N}.bin");
        string remote = $"/data/local/tmp/fwkit_cmd_{Guid.NewGuid():N}.bin";
        byte[] payload = Encoding.UTF8.GetBytes("round-trip payload 0123456789");
        try
        {
            File.WriteAllBytes(local, payload);

            CliResult push = await RunCliAsync(target, "push", local, remote);
            Assert.Equal(0, push.ExitCode);
            Assert.Matches(@"1 file pushed, 0 skipped\.", push.Stdout);
            Assert.Contains("MB/s", push.Stdout);
            Assert.Contains("bytes in", push.Stdout);

            string pulled = Path.Combine(Path.GetTempPath(), $"fwkit_cmd_pull_{Guid.NewGuid():N}.bin");
            try
            {
                CliResult pull = await RunCliAsync(target, "pull", remote, pulled);
                Assert.Equal(0, pull.ExitCode);
                Assert.Matches(@"1 file pulled, 0 skipped\.", pull.Stdout);
                Assert.Equal(payload, File.ReadAllBytes(pulled));
            }
            finally { File.Delete(pulled); }
        }
        finally
        {
            File.Delete(local);
            await RunCliAsync(target, "shell", "--", "rm", "-f", remote);
        }
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Pull_MissingFile(Target target) => Run(target, "pull",
        "/data/local/tmp/fwkit_does_not_exist_xyz", Path.GetTempPath(), r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("error", r.Stderr, StringComparison.OrdinalIgnoreCase);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_FileOps(Target target) => RunWithTimeout(async () =>
    {
        string dir = $"/data/local/tmp/fwkit_cmd_{Guid.NewGuid():N}";
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

    // ---- app management --------------------------------------------------

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Install_NonApk_Fails(Target target) => RunWithTimeout(async () =>
    {
        string local = Path.Combine(Path.GetTempPath(), $"fwkit_cmd_{Guid.NewGuid():N}.txt");
        File.WriteAllText(local, "not an apk");
        try
        {
            CliResult r = await RunCliAsync(target, "install", local);
            Assert.NotEqual(0, r.ExitCode);
        }
        finally { File.Delete(local); }
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Uninstall_MissingPackage(Target target) => Run(target, "uninstall",
        "com.example.nonexistent.fwkit", r => Assert.NotEqual(0, r.ExitCode));

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Root_Reachable(Target target) => Run(target, "root", r =>
        Assert.True(r.ExitCode is 0 or 1, $"unexpected exit {r.ExitCode}"));

    // ---- device query / services -----------------------------------------

    [Theory]
    [InlineData(Target.Usb)]
    public Task GetState(Target target) => Run(target, "get-state", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(Target.Usb)]
    public Task GetSerialNo(Target target) => Run(target, "get-serialno", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(Target.Usb)]
    public Task GetDevPath(Target target) => Run(target, "get-devpath", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Features(Target target) => Run(target, "features", r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("shell_v2", r.Stdout);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task WaitForDevice(Target target) => Run(target, "wait-for-device", r =>
        Assert.Equal(0, r.ExitCode));

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Reverse_List(Target target) => Run(target, "reverse", "--list", r =>
        Assert.Equal(0, r.ExitCode));

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Logcat_Dump(Target target) => Run(target, "logcat", "--", "-d", "-t", "5", r =>
        Assert.Equal(0, r.ExitCode));

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Bugreport(Target target) => Run(target, BugreportMs, "bugreport", r =>
        Assert.Equal(0, r.ExitCode), BugreportMs + 10_000);

    // ---- no-target error (skip when USB device present) -------------------

    [Fact]
    public Task NoTarget_Errors() => RunWithTimeout(async () =>
    {
        // Query the CLI itself so the skip decision matches what the spawned
        // process sees (in-process enumeration can differ).
        CliResult devices = await RunCliRaw("devices");
        bool hasUsb = devices.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Any(l => l.Contains('\t') && l.Contains("device", StringComparison.Ordinal));
        if (hasUsb)
        {
            Assert.Skip("USB device attached; no-target error path unreachable.");
        }

        CliResult r = await RunCliRaw("get-state");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("no devices/emulators found", r.Stderr);
    });

    // ---- helpers ---------------------------------------------------------

    private static Task Run(Target target, params string[] args)
        => Run(target, TimeoutMs, args, r => { }, TimeoutMs + 5_000);

    private static Task Run(Target target, string[] args, Action<CliResult> assert)
        => Run(target, TimeoutMs, args, assert, TimeoutMs + 5_000);

    private static Task Run(Target target, int timeoutMs, string[] args, Action<CliResult> assert, int outerMs)
        => RunWithTimeout(async () => assert(await RunCliAsync(target, timeoutMs, args)), outerMs);

    private static Task RunNoTarget(string? arg, Action<CliResult> assert)
        => RunWithTimeout(async () => assert(await RunCliRaw(arg is null ? [] : [arg])));

    private static Task RunNoTarget(string arg1, string arg2, Action<CliResult> assert)
        => RunWithTimeout(async () => assert(await RunCliRaw([arg1, arg2])));

    private static Task RunNoTarget(string a1, string a2, string a3, string a4, Action<CliResult> assert)
        => RunWithTimeout(async () => assert(await RunCliRaw([a1, a2, a3, a4])));

    private static Task RunWithTimeout(Func<Task> body, int ms = TimeoutMs)
        => body().WaitAsync(TimeSpan.FromMilliseconds(ms));

    private static Task<CliResult> RunCliAsync(Target target, params string[] args)
        => RunCliAsync(target, TimeoutMs, args);

    private static async Task<CliResult> RunCliAsync(Target target, int timeoutMs, params string[] args)
    {
        if (target == Target.Usb && !UsbReady)
        {
            Assert.Skip("No USB ADB device attached; skipping USB case.");
        }

        var cliArgs = new List<string>();
        if (target == Target.Emulator)
        {
            cliArgs.AddRange(["-H", Host, "-P", Port.ToString()]);
        }
        cliArgs.AddRange(args);
        return await RunCliRaw(cliArgs.ToArray(), timeoutMs);
    }

    /// <summary>Spawns the CLI without any injected -H/-P (for local/error tests).</summary>
    private static Task<CliResult> RunCliRaw(string[] args, int timeoutMs = TimeoutMs)
    {
        string cliDll = Path.Combine(AppContext.BaseDirectory, "adb.dll");
        Assert.True(File.Exists(cliDll), $"CLI assembly not found at {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the CLI process.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"CLI did not exit within {timeoutMs} ms: {string.Join(' ', args)}");
        }

        return Task.FromResult(new CliResult(process.ExitCode, stdout.Result, stderr.Result));
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
