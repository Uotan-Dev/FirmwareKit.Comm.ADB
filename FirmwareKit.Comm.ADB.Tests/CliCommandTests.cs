using FirmwareKit.Comm.ADB.Backend.Usb;
using System.Diagnostics;
using System.Text;

namespace FirmwareKit.Comm.ADB.Tests;

/// <summary>
/// End-to-end CLI tests that spawn the built <c>adb</c> binary against a real USB
/// device and/or a TCP emulator. Each test runs against both targets when the
/// command makes sense for both. Merges the former CliIntegrationTests and
/// AdbCommandCompatibilityTests (which had substantial overlap).
/// <para>以子进程方式启动构建出的 <c>adb</c>，针对真实 USB 设备和/或 TCP 模拟器
/// 执行的端到端 CLI 测试。每个用例在命令对两类目标都适用时同时运行。合并了原先
/// 大量重复的 CliIntegrationTests 与 AdbCommandCompatibilityTests。</para>
/// </summary>
public class CliCommandTests
{
    public enum Target { Usb, Emulator }

    private const int TimeoutMs = 30_000;
    private const int BugreportMs = 300_000; // real-device bugreport can stream for minutes

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
    public Task Version() => CheckRaw(r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Android Debug Bridge version", r.Stdout);
        Assert.Contains("Running on", r.Stdout);
    }, "version");

    [Fact]
    public Task VersionFlag() => CheckRaw(r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Android Debug Bridge version", r.Stdout);
    }, "--version");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Help(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("global options:", r.Stderr);
        Assert.Contains("devices [-l]", r.Stderr);
    }, "help");

    [Fact]
    public Task NoArgs_ShowsUsage() => CheckRaw(r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("global options:", r.Stderr);
    });

    [Fact]
    public Task UnknownCommand_Errors() => CheckRaw(r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("unknown command frobnicate", r.Stderr);
    }, "frobnicate");

    [Fact]
    public Task UnknownGlobalOption_Errors() => CheckRaw(r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("unknown option -z", r.Stderr);
    }, "-z", "devices");

    [Fact]
    public Task MissingSerialArgument_Errors() => CheckRaw(r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("missing argument", r.Stderr);
    }, "-s");

    [Fact]
    public Task InvalidPort_Errors() => CheckRaw(r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("invalid port", r.Stderr);
    }, "-P", "notaport", "shell", "x");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Devices(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("List of devices attached", r.Stdout);
    }, "devices");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Devices_Long(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("List of devices attached", r.Stdout);
    }, "devices", "-l");

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
    public Task Shell_GetProp(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    }, "shell", "getprop", "ro.product.model");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_WmSize(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Physical size", r.Stdout);
    }, "shell", "wm", "size");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_DumpsysBattery(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Battery", r.Stdout);
    }, "shell", "dumpsys", "battery");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_Df(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Filesystem", r.Stdout);
    }, "shell", "df", "/data");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_Uname(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    }, "shell", "--", "uname", "-r");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_CatProc(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("MemTotal", r.Stdout);
    }, "shell", "cat", "/proc/meminfo");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_RemoteExitCode(Target target) => Check(target,
        r => Assert.Equal(7, r.ExitCode), "shell", "sh -c 'exit 7'");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_StdoutStderrSeparation(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("OUT", r.Stdout);
        Assert.Contains("ERR", r.Stderr);
    }, "shell", "sh -c 'echo OUT; echo ERR >&2'");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_NoExitCode(Target target) => Check(target,
        r => Assert.Equal(0, r.ExitCode), "shell", "-x", "sh", "-c", "exit 7");

    [Theory]
    [InlineData(Target.Emulator)]
    public Task Shell_GlobalOptionsAfterVerb(Target target) => Check(target, r =>
    {
        // -H/-P after the verb must be bound as globals, not forwarded to sh.
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    }, "shell", "-H", Host, "-P", Port.ToString(), "getprop", "ro.build.version.release");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_PmGrant_MissingPackage(Target target) => Check(target,
        r => Assert.NotEqual(0, r.ExitCode),
        "shell", "pm", "grant", "com.example.nonexistent.fwkit", "android.permission.DUMP");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Shell_InputKeyeventHome(Target target) => Check(target,
        r => Assert.Equal(0, r.ExitCode), "shell", "input", "keyevent", "3");

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
            Assert.Matches(@"\d+(\.\d+)? [KMG]?B/s", push.Stdout);
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
    public Task Pull_MissingFile(Target target) => Check(target, r =>
    {
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("error", r.Stderr, StringComparison.OrdinalIgnoreCase);
    }, "pull", "/data/local/tmp/fwkit_does_not_exist_xyz", Path.GetTempPath());

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
    public Task Uninstall_MissingPackage(Target target) => Check(target,
        r => Assert.NotEqual(0, r.ExitCode),
        "uninstall", "com.example.nonexistent.fwkit");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Root_Reachable(Target target) => Check(target,
        r => Assert.True(r.ExitCode is 0 or 1, $"unexpected exit {r.ExitCode}"), "root");

    // ---- device query / services -----------------------------------------

    [Theory]
    [InlineData(Target.Usb)]
    public Task GetState(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    }, "get-state");

    [Theory]
    [InlineData(Target.Usb)]
    public Task GetSerialNo(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    }, "get-serialno");

    [Theory]
    [InlineData(Target.Usb)]
    public Task GetDevPath(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Stdout));
    }, "get-devpath");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Features(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("shell_v2", r.Stdout);
    }, "features");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task WaitForDevice(Target target) => Check(target,
        r => Assert.Equal(0, r.ExitCode), "wait-for-device");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Reverse_List(Target target) => RunWithTimeout(async () =>
    {
        // Clean slate (best effort).
        await RunCliAsync(target, "reverse", "--remove-all");

        // Empty list must produce no raw "0000"/"OKAY" markers (the wire-length
        // prefix must be parsed, not echoed).
        CliResult empty = await RunCliAsync(target, "reverse", "--list");
        Assert.Equal(0, empty.ExitCode);
        Assert.DoesNotContain("0000", empty.Stdout);
        Assert.DoesNotContain("OKAY", empty.Stdout);
        Assert.Equal(string.Empty, empty.Stdout.Trim());
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Root_Unroot(Target target) => RunWithTimeout(async () =>
    {
        // root/unroot are reachable; non-root builds report "adbd cannot run as
        // root" (exit 1) while userdebug builds restart adbd (exit 0). Accept both.
        CliResult root = await RunCliAsync(target, "root");
        Assert.True(root.ExitCode is 0 or 1, $"unexpected root exit {root.ExitCode}");

        // Give adbd a moment to restart if it did.
        await Task.Delay(1000);

        CliResult unroot = await RunCliAsync(target, "unroot");
        Assert.True(unroot.ExitCode is 0 or 1, $"unexpected unroot exit {unroot.ExitCode}");
        await Task.Delay(1000);
    });

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Mdns_Check(Target target) => Check(target, r =>
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("mDNS", r.Stdout, StringComparison.OrdinalIgnoreCase);
    }, "mdns", "check");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Mdns_Services(Target target) => Check(target,
        r => Assert.Equal(0, r.ExitCode), "mdns", "services");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Logcat_Dump(Target target) => Check(target,
        r => Assert.Equal(0, r.ExitCode), "logcat", "--", "-d", "-t", "5");

    [Theory]
    [InlineData(Target.Usb)]
    [InlineData(Target.Emulator)]
    public Task Bugreport(Target target) => RunWithTimeout(async () =>
    {
        CliResult r = await RunCliAsync(target, BugreportMs, "bugreport");
        Assert.Equal(0, r.ExitCode);
    }, BugreportMs + 10_000);

    // ---- no-target error (skip when USB device present) -------------------

    [Fact]
    public Task NoTarget_Errors() => RunWithTimeout(async () =>
    {
        // Query the CLI itself so the skip decision matches what the spawned
        // process sees (in-process enumeration can differ).
        CliResult devices = await RunCliRaw(["devices"]);
        bool hasUsb = devices.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Any(l => l.Contains('\t') && l.Contains("device", StringComparison.Ordinal));
        if (hasUsb)
        {
            Assert.Skip("USB device attached; no-target error path unreachable.");
        }

        CliResult r = await RunCliRaw(["get-state"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("no devices/emulators found", r.Stderr);
    });

    // ---- helpers ---------------------------------------------------------

    /// <summary>Runs the CLI against a target and asserts on the result.</summary>
    private static Task Check(Target target, Action<CliResult> assert, params string[] args)
        => RunWithTimeout(async () => assert(await RunCliAsync(target, args)));

    /// <summary>Runs the CLI without injected -H/-P (local/error tests).</summary>
    private static Task CheckRaw(Action<CliResult> assert, params string[] args)
        => RunWithTimeout(async () => assert(await RunCliRaw(args)));

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

        // On Windows, after an in-process test (UsbDeviceIntegrationTests) releases
        // the libusb handle, the OS needs a brief moment to fully release the USB
        // interface before a child process can re-claim it (winusb allows one
        // handle per interface). Retry on transient "no device" errors instead of
        // failing a healthy command.
        if (target == Target.Usb)
        {
            for (int attempt = 0; ; attempt++)
            {
                CliResult result = await RunCliRaw([.. cliArgs], timeoutMs);
                bool transient = result.ExitCode != 0
                    && (result.Stderr.Contains("no devices/emulators", StringComparison.OrdinalIgnoreCase)
                        || result.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
                        || result.Stderr.Contains("Unable to write data", StringComparison.OrdinalIgnoreCase));
                if (!transient || attempt >= 5)
                {
                    return result;
                }
                await Task.Delay(500 * (attempt + 1));
            }
        }

        return await RunCliRaw([.. cliArgs], timeoutMs);
    }

    /// <summary>Spawns the CLI with the given args (no injected globals).</summary>
    private static async Task<CliResult> RunCliRaw(string[] args, int timeoutMs = TimeoutMs)
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

        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
