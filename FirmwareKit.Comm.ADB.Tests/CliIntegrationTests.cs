using System.Diagnostics;
using System.Text;

namespace FirmwareKit.Comm.ADB.Tests;

/// <summary>
/// Integration tests that exercise the command-line tool (FirmwareKit.Comm.ADB.Cli)
/// as a real process against the live emulator over TCP (--host/--port). These
/// verify the CLI commands that must work against the emulator: shell, push, pull,
/// and the local-only commands (version, devices, targeting errors).
/// <para>以真实进程方式运行命令行工具（FirmwareKit.Comm.ADB.Cli），通过
/// --host/--port 对活动模拟器执行 CLI 命令的集成测试。覆盖必须对模拟器生效的
/// 命令（shell、push、pull）以及仅本地生效的命令（version、devices、目标选择错误）。</para>
/// </summary>
public class CliIntegrationTests
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 16416;

    /// <summary>
    /// Hard bound for a single test run (30s), consistent with the library tests.
    /// <para>单次测试运行的硬性上限（30 秒），与库测试保持一致。</para>
    /// </summary>
    private const int TestTimeoutMs = 30000;

    private static string Host => Environment.GetEnvironmentVariable("ADB_TEST_HOST") ?? DefaultHost;

    private static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("ADB_TEST_PORT"), out int p) ? p : DefaultPort;

    /// <summary>
    /// Isolates the `connect` state file so CLI tests never touch the user's
    /// real saved endpoint.
    /// <para>隔离 `connect` 状态文件，使 CLI 测试不触及用户真实保存的端点。</para>
    /// </summary>
    static CliIntegrationTests()
    {
        Environment.SetEnvironmentVariable(
            "ADB_CONNECT_FILE",
            Path.Combine(Path.GetTempPath(), $"fwkit_adb_connect_{Guid.NewGuid():N}.txt"));
    }

    [Fact]
    public Task Cli_Shell_RunsGetProp_OnEmulator() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("shell", "-H", Host, "-P", Port.ToString(),
            "getprop ro.build.version.release");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("15", result.Stdout);
    });

    [Fact]
    public Task Cli_Shell_ReportsRemoteExitCode() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("shell", "-H", Host, "-P", Port.ToString(),
            "sh -c 'exit 7'");

        // Like the official adb, the CLI exits with the remote command's code.
        // <para>与官方 adb 一致，CLI 以远端命令的退出码退出。</para>
        Assert.Equal(7, result.ExitCode);
    });

    [Fact]
    public Task Cli_Shell_SeparatesStdoutAndStderr() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("shell", "-H", Host, "-P", Port.ToString(),
            "sh -c 'echo OUT-LINE; echo ERR-LINE >&2'");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("OUT-LINE", result.Stdout);
        Assert.Contains("ERR-LINE", result.Stderr);
    });

    [Fact]
    public Task Cli_PushPull_RoundTrip() => RunWithTimeout(async () =>
    {
        const string remote = "/data/local/tmp/fwkit_cli_test.bin";
        string local = Path.Combine(Path.GetTempPath(), $"fwkit_cli_push_{Guid.NewGuid():N}.bin");
        string pulled = Path.Combine(Path.GetTempPath(), $"fwkit_cli_pull_{Guid.NewGuid():N}.bin");
        byte[] payload = Encoding.UTF8.GetBytes("cli round-trip payload 0123456789");

        try
        {
            File.WriteAllBytes(local, payload);

            CliResult push = await RunCliAsync("push", "-H", Host, "-P", Port.ToString(), local, remote);
            Assert.Equal(0, push.ExitCode);
            Assert.Contains("pushed", push.Stdout);

            CliResult pull = await RunCliAsync("pull", "-H", Host, "-P", Port.ToString(), remote, pulled);
            Assert.Equal(0, pull.ExitCode);
            Assert.Contains("pulled", pull.Stdout);

            Assert.Equal(payload, File.ReadAllBytes(pulled));
        }
        finally
        {
            File.Delete(local);
            File.Delete(pulled);
            await RunCliAsync("shell", "-H", Host, "-P", Port.ToString(), $"rm -f '{remote}'");
        }
    });

    [Fact]
    public Task Cli_PullMissingFile_Fails() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("pull", "-H", Host, "-P", Port.ToString(),
            "/data/local/tmp/fwkit_does_not_exist_xyz", Path.GetTempPath());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error", result.Stderr, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task Cli_Version_PrintsBanner() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Android Debug Bridge version", result.Stdout);
    });

    [Fact]
    public Task Cli_Devices_PrintsHeader() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("devices");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("List of devices attached", result.Stdout);
    });

    [Fact]
    public Task Cli_NoTargetDevice_ReportsError() => RunWithTimeout(async () =>
    {
        // Without --host, the CLI targets USB devices only; on this machine no
        // ADB USB device is attached, so it must fail with the standard message.
        // <para>未指定 --host 时 CLI 仅面向 USB 设备；本机未连接 ADB USB 设备，
        // 因此必须以标准消息报错。</para>
        CliResult result = await RunCliAsync("get-state");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no devices/emulators found", result.Stderr);
    });

    [Fact]
    public Task Cli_Features_ListsDeviceFeatures() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("features", "-H", Host, "-P", Port.ToString());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("shell_v2", result.Stdout);
    });

    [Fact]
    public Task Cli_ConnectSavesEndpoint_ThenCommandsTargetIt() => RunWithTimeout(async () =>
    {
        try
        {
            CliResult connect = await RunCliAsync("connect", $"{Host}:{Port}");
            Assert.Equal(0, connect.ExitCode);
            Assert.Contains($"connected to {Host}:{Port}", connect.Stdout);

            // Direct-write: without -H/-P, later commands target the saved endpoint.
            // <para>直写模式：未指定 -H/-P 时，后续命令直接定位已保存的端点。</para>
            CliResult shell = await RunCliAsync("shell", "getprop ro.build.version.release");
            Assert.Equal(0, shell.ExitCode);
            Assert.Contains("15", shell.Stdout);

            CliResult devices = await RunCliAsync("devices");
            Assert.Contains($"{Host}:{Port}", devices.Stdout);
        }
        finally
        {
            await RunCliAsync("disconnect");
        }
    });

    [Fact]
    public Task Cli_Disconnect_ClearsSavedEndpoint() => RunWithTimeout(async () =>
    {
        CliResult connect = await RunCliAsync("connect", $"{Host}:{Port}");
        Assert.Equal(0, connect.ExitCode);

        CliResult disconnect = await RunCliAsync("disconnect", $"{Host}:{Port}");
        Assert.Equal(0, disconnect.ExitCode);
        Assert.Contains("disconnected", disconnect.Stdout);

        CliResult devices = await RunCliAsync("devices");
        Assert.DoesNotContain($"{Host}:{Port}", devices.Stdout);
    });

    [Fact]
    public Task Cli_Shell_NoExitCodeOption_ReturnsZero() => RunWithTimeout(async () =>
    {
        // -x disables exit-code propagation, matching the official adb option.
        // <para>-x 禁用退出码透传，与官方 adb 选项一致。</para>
        CliResult result = await RunCliAsync(
            "shell", "-x", "-H", Host, "-P", Port.ToString(), "sh -c 'exit 7'");

        Assert.Equal(0, result.ExitCode);
    });

    [Fact]
    public Task Cli_Help_PrintsUsage() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("help");

        // Like the official adb, the help text goes to stderr and exits 0.
        // <para>与官方 adb 一致，帮助文本输出到 stderr 并以 0 退出。</para>
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("global options:", result.Stderr);
        Assert.Contains("devices [-l]", result.Stderr);
    });

    [Fact]
    public Task Cli_GlobalSerial_BeforeVerb_IsAccepted() => RunWithTimeout(async () =>
    {
        // `-s` before the verb used to fail with "Verb '-s' is not recognized.";
        // like the official adb it must be consumed as a global option and reach
        // the target resolver (which reports the unknown serial).
        // <para>命令前的 `-s` 原先报 "Verb '-s' is not recognized."；现在与官方 adb
        // 一致作为全局选项消费并到达目标解析器（其报告未知序列号）。</para>
        CliResult result = await RunCliAsync("-s", "ABC", "get-state");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("device 'ABC' not found", result.Stderr);
    });

    [Fact]
    public Task Cli_UnknownCommand_ReportsError() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("frobnicate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unknown command frobnicate", result.Stderr);
    });

    [Fact]
    public Task Cli_NoArgs_PrintsUsageToStderr() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("global options:", result.Stderr);
    });

    [Fact]
    public Task Cli_VersionFlag_PrintsBanner() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Android Debug Bridge version", result.Stdout);
    });

    [Fact]
    public Task Cli_UnknownGlobalOption_ReportsError() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("-z", "devices");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unknown option -z", result.Stderr);
    });

    [Fact]
    public Task Cli_MissingSerialArgument_ReportsError() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("-s");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing argument", result.Stderr);
    });

    [Fact]
    public Task Cli_Logcat_DumpBounded() => RunWithTimeout(async () =>
    {
        // `--` separates the CLI's own options from logcat's dash-prefixed args.
        // <para>`--` 分隔 CLI 自身选项与 logcat 以横线开头的参数。</para>
        CliResult result = await RunCliAsync(
            "logcat", "-H", Host, "-P", Port.ToString(), "--", "-d", "-t", "5");

        Assert.Equal(0, result.ExitCode);
    });

    [Fact]
    public Task Cli_Reverse_List() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("reverse", "-H", Host, "-P", Port.ToString(), "--list");

        Assert.Equal(0, result.ExitCode);
    });

    [Fact]
    public Task Cli_WaitForDevice_ReachableEndpoint() => RunWithTimeout(async () =>
    {
        CliResult result = await RunCliAsync("wait-for-device", "-H", Host, "-P", Port.ToString());

        Assert.Equal(0, result.ExitCode);
    });

    /// <summary>
    /// Runs the test body with a hard 30s bound; a timeout surfaces as a
    /// TimeoutException instead of hanging the suite.
    /// <para>以 30 秒硬性上限运行测试体；超时以 TimeoutException 呈现。</para>
    /// </summary>
    private static Task RunWithTimeout(Func<Task> body)
        => body().WaitAsync(TimeSpan.FromMilliseconds(TestTimeoutMs));

    /// <summary>
    /// Spawns the CLI (the built FirmwareKit.Comm.ADB.Cli.dll) as a child process,
    /// captures stdout / stderr / exit code, and kills it if it exceeds 30s.
    /// <para>以子进程方式启动 CLI（构建出的 FirmwareKit.Comm.ADB.Cli.dll），
    /// 捕获 stdout / stderr / 退出码，超过 30 秒则强制结束。</para>
    /// </summary>
    private static async Task<CliResult> RunCliAsync(params string[] args)
    {
        // The CLI project builds as "adb" (AssemblyName=adb), matching the
        // official binary name; the reference copies adb.dll into this output.
        // <para>CLI 项目以 "adb" 为程序集名（AssemblyName=adb），与官方二进制同名；
        // 项目引用会把 adb.dll 复制到本输出目录。</para>
        string cliDll = Path.Combine(AppContext.BaseDirectory, "adb.dll");
        Assert.True(File.Exists(cliDll), $"CLI assembly not found at {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the CLI process.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TestTimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }

            throw new TimeoutException($"CLI did not exit within {TestTimeoutMs} ms: dotnet {string.Join(' ', args)}");
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
