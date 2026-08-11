using CommandLine;
using FirmwareKit.Comm.ADB.Backend.Tcp;
using FirmwareKit.Comm.ADB.Backend.Udp;
using FirmwareKit.Comm.ADB.Backend.Usb;
using FirmwareKit.Comm.ADB.Protocol;
using FirmwareKit.Comm.ADB.Services;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FirmwareKit.Comm.ADB.Cli;

internal static class Program
{
    private const string VersionString = "1.0.0";

    // Protocol version advertised by the client library (A_VERSION, see AdbProtocol).
    // Shown on the version banner like the official adb does.
    // <para>客户端库通告的协议版本（A_VERSION，见 AdbProtocol），与官方 adb 一样
    // 显示在版本横幅上。</para>
    private const string ProtocolVersionString = "1.0.1";

    private static bool _debug;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Official adb syntax: global options precede the command verb
        // (`adb -s SERIAL shell ...`). Consume them from the front of the list
        // so `-s`, `-H`, `-P`, ... are accepted exactly like the official adb.
        // <para>官方 adb 语法：全局选项位于命令动词之前（`adb -s SERIAL shell ...`）。
        // 从参数列表前端消费它们，使 `-s`、`-H`、`-P` 等与官方 adb 完全一致地被接受。</para>
        var globals = new Options.GlobalOptions();
        switch (CliParser.ParseGlobalOptions(args, globals, out int commandIndex, out string? globalError))
        {
            case CliParser.GlobalParseResult.Help:
                Usage(Console.Error);
                return 0;
            case CliParser.GlobalParseResult.Version:
                PrintVersion(Console.Out);
                return 0;
            case CliParser.GlobalParseResult.Error:
                Console.Error.WriteLine($"adb: {globalError}");
                Usage(Console.Error);
                return 1;
        }

        if (commandIndex >= args.Length)
        {
            // `adb` with no command prints usage and fails, like the official adb.
            // <para>与官方 adb 一致：无命令时输出用法并失败。</para>
            Usage(Console.Error);
            return 1;
        }

        string command = args[commandIndex];
        if (command.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            Usage(Console.Error);
            return 0;
        }

        // Official adb treats everything after `shell` (past the shell's own
        // -e/-n/-T/-t/-x/--term options) as the remote command, so `adb shell
        // uname -r` must pass `-r` to the device instead of rejecting it as an
        // unknown shell option. Insert a `--` separator right before the command
        // starts so CommandLineParser treats the rest as positional values.
        // <para>官方 adb 中 `shell` 之后除自身选项（-e/-n/-T/-t/-x/--term）外
        // 全部是远端命令，因此 `adb shell uname -r` 应把 `-r` 传给设备而非报未知
        // 选项。在命令起始处插入 `--` 分隔符，使 CommandLineParser 将其后内容
        // 作为位置值。</para>
        string[] parseArgs = command.Equals("shell", StringComparison.OrdinalIgnoreCase)
            ? CliParser.PrepareShellArgs(args[commandIndex..])
            : args[commandIndex..];

        var parser = new Parser(settings =>
        {
            // Render parse errors ourselves (google style) instead of the library's
            // "adb 1.0.0 / Copyright (C) ..." screen.
            // <para>自行以谷歌风格渲染解析错误，而不是库自带的
            // "adb 1.0.0 / Copyright (C) ..." 屏幕。</para>
            settings.HelpWriter = TextWriter.Null;
            settings.AutoVersion = false;
            settings.AutoHelp = true;
            settings.EnableDashDash = true; // `cmd -- -arg` passes dash-args as values
        });

        ParserResult<object> result = parser.ParseArguments(parseArgs, CliParser.VerbTypes);
        if (result.Tag == ParserResultType.Parsed)
        {
            var opts = ((Parsed<object>)result).Value;
            CliParser.ApplyGlobals(opts, globals);

            // The user explicitly chooses the USB backend: libusb only with --libusb,
            // otherwise the platform native backend. No automatic fallback — if the
            // chosen backend cannot open the device, the error surfaces to the user.
            // <para>USB 后端由用户显式选择：仅指定 --libusb 时使用 libusb，否则使用
            // 平台原生后端。不自动回退——所选后端无法打开设备时错误直接呈现给用户。</para>
            UsbManager.ForceLibUsb = globals.UseLibUsb || ((opts as Options.GlobalOptions)?.UseLibUsb ?? false);

            return Dispatch(opts);
        }

        return HandleParseErrors(result.Errors);
    }

    private static int Dispatch(object opts) => opts switch
    {
        Options.DevicesVerb o => Run(o, ExecuteDevices),
        Options.GetStateVerb o => Run(o, ExecuteGetState),
        Options.GetSerialNoVerb o => Run(o, ExecuteGetSerialNo),
        Options.GetDevPathVerb o => Run(o, ExecuteGetDevPath),
        Options.ShellVerb o => Run(o, ExecuteShell),
        Options.PushVerb o => Run(o, ExecutePush),
        Options.PullVerb o => Run(o, ExecutePull),
        Options.InstallVerb o => Run(o, ExecuteInstall),
        Options.UninstallVerb o => Run(o, ExecuteUninstall),
        Options.RebootVerb o => Run(o, ExecuteReboot),
        Options.RebootBootloaderVerb o => Run(o, ExecuteRebootBootloader),
        Options.RemountVerb o => Run(o, ExecuteRemount),
        Options.RootVerb o => Run(o, ExecuteRoot),
        Options.UnrootVerb o => Run(o, ExecuteUnroot),
        Options.UsbVerb o => Run(o, ExecuteUsb),
        Options.TcpIpVerb o => Run(o, ExecuteTcpIp),
        Options.LogcatVerb o => Run(o, ExecuteLogcat),
        Options.BugreportVerb o => Run(o, ExecuteBugreport),
        Options.FeaturesVerb o => Run(o, ExecuteFeatures),
        Options.WaitForDeviceVerb o => Run(o, ExecuteWaitForDevice),
        Options.HelpVerb o => Run(o, ExecuteHelp),
        Options.ReverseVerb o => Run(o, ExecuteReverse),
        Options.ConnectVerb o => Run(o, ExecuteConnect),
        Options.DisconnectVerb o => Run(o, ExecuteDisconnect),
        Options.MdnsVerb o => Run(o, ExecuteMdns),
        Options.VersionVerb o => ExecuteVersion(o),
        _ => 1,
    };

    private static int Run<T>(T opts, Func<T, int> handler) where T : Options.GlobalOptions
    {
        _debug = opts.Debug;
        return handler(opts);
    }

    private static int HandleParseErrors(IEnumerable<Error> errors)
    {
        foreach (var error in errors)
        {
            switch (error)
            {
                case HelpRequestedError or HelpVerbRequestedError:
                    Usage(Console.Error);
                    return 0;
                case VersionRequestedError:
                    PrintVersion(Console.Out);
                    return 0;
                case NoVerbSelectedError:
                    Usage(Console.Error);
                    return 1;
                case BadVerbSelectedError badVerb:
                    // Matches the official adb: "unknown command foo" + usage.
                    // <para>与官方 adb 一致："unknown command foo" + 用法。</para>
                    Console.Error.WriteLine($"unknown command {badVerb.Token}");
                    Usage(Console.Error);
                    return 1;
                case UnknownOptionError unknown:
                    Console.Error.WriteLine($"adb: unknown option {FormatOption(unknown.Token)}");
                    return 1;
                case MissingValueOptionError missing:
                    Console.Error.WriteLine($"adb: missing argument for {missing.NameInfo.NameText}");
                    return 1;
                case RepeatedOptionError repeated:
                    Console.Error.WriteLine($"adb: duplicate option {repeated.NameInfo.NameText}");
                    return 1;
                default:
                    Console.Error.WriteLine($"adb: {error}");
                    return 1;
            }
        }

        return 1;
    }

    private static string FormatOption(string token)
        => token.StartsWith('-') ? token : "-" + token;

    /// <summary>
    /// Rewrites a `shell ...` argument list for CommandLineParser so tokens belonging
    /// to the remote command (including leading dashes like `uname -r`) are treated as
    /// positional values rather than unknown shell options. The shell's own options
    /// (-e/-n/-T/-t/-x/--term) may appear before the command; once the command starts
    /// (or after an explicit `--`), every remaining token is passed through.
    /// <para>重写 `shell ...` 参数列表，使属于远端命令的令牌（包括 `uname -r`
    /// 这类带前导短横线的参数）被当作位置值而非未知 shell 选项。shell 自身选项
    /// （-e/-n/-T/-t/-x/--term）可出现在命令之前；一旦命令开始（或遇到显式 `--`），
    /// 其后所有令牌均原样透传。</para>
    /// </summary>
    private static string[] PrepareShellArgs(string[] args)
    {
        var result = new List<string>(args.Length + 1) { args[0] };
        bool commandStarted = false;

        for (int idx = 1; idx < args.Length; idx++)
        {
            string arg = args[idx];

            if (!commandStarted)
            {
                if (arg == "--")
                {
                    commandStarted = true;
                    result.Add(arg);
                    continue;
                }

                // Shell's own boolean flags.
                if (arg is "-t" or "-T" or "-n" or "-x")
                {
                    result.Add(arg);
                    continue;
                }

                // Shell options that consume a value: -e ESCAPE, --term TERM.
                if (arg is "-e" or "--term" ||
                    arg.StartsWith("--term=", StringComparison.Ordinal) ||
                    arg.StartsWith("-e=", StringComparison.Ordinal))
                {
                    result.Add(arg);
                    if (arg is "-e" or "--term" && idx + 1 < args.Length)
                    {
                        result.Add(args[++idx]);
                    }

                    continue;
                }

                // Any other token starts the remote command; insert `--` so a leading
                // dash (e.g. `-r`) is passed through as a value.
                result.Add("--");
                commandStarted = true;
            }

            result.Add(arg);
        }

        return result.ToArray();
    }

    // ---- devices ------------------------------------------------------------

    private static int ExecuteDevices(Options.DevicesVerb opts)
    {
        var devices = UsbManager.GetAllDevices();

        Console.WriteLine("List of devices attached");
        // Official adb assigns a unique, monotonically increasing transport_id to
        // each attached device (it is not the USB bus number). Mirror that by
        // numbering devices in enumeration order, then continuing the sequence for
        // a saved TCP endpoint.
        // <para>官方 adb 为每个已连接设备分配唯一、单调递增的 transport_id（并非 USB
        // 总线号）。按枚举顺序编号，并为保存的 TCP 端点续接序号。</para>
        int transportId = 1;
        foreach (var device in devices)
        {
            string serial = string.IsNullOrEmpty(device.SerialNumber) ? "????????????" : device.SerialNumber;
            if (opts.LongList)
            {
                Console.WriteLine($"{serial,-22} device product:{GetProduct(device)} model:{GetModel(device)} device:{GetDevice(device)} transport_id:{transportId}");
            }
            else
            {
                // Official adb uses a single tab between serial and state.
                // <para>官方 adb 在序列号与状态之间使用单个制表符。</para>
                Console.WriteLine($"{serial}\tdevice");
            }

            transportId++;
            device?.Dispose();
        }

        // The endpoint remembered by `connect` shows up like a connected device,
        // with a live reachability probe. <para>`connect` 保存的端点像已连接设备
        // 一样列出，并做实时可达性探测。</para>
        string? saved = SavedEndpoint;
        if (saved is not null)
        {
            string state = TryConnectQuickly(saved) ? "device" : "offline";
            if (opts.LongList)
            {
                Console.WriteLine($"{saved,-22} {state} product:unknown model:unknown device:unknown transport_id:{transportId}");
            }
            else
            {
                Console.WriteLine($"{saved}\t{state}");
            }
        }

        return 0;
    }

    private static string GetProduct(UsbDevice device) => "usb";
    private static string GetModel(UsbDevice device) => string.IsNullOrEmpty(device.SerialNumber) ? "unknown" : device.SerialNumber;
    private static string GetDevice(UsbDevice device) => "usb";

    // ---- device helpers -----------------------------------------------------

    private static UsbDevice OpenTarget(Options.GlobalOptions opts)
    {
        // Toolbox integration spawns many CLI processes back-to-back; when the
        // previous process releases the USB interface, winusb needs a brief moment
        // before another process can claim it. Retry a few times instead of
        // failing with "device not found" / "no devices" on transient contention.
        Exception? lastError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                UsbDevice? result = TryOpenTarget(opts, out lastError);
                if (result is not null) return result;
                if (lastError is not null) return null!;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            Thread.Sleep(200 * (attempt + 1));
        }

        if (lastError is not null)
        {
            Console.Error.WriteLine($"error: {lastError.Message}");
        }
        return null!;
    }

    private static UsbDevice? TryOpenTarget(Options.GlobalOptions opts, out Exception? openError)
    {
        openError = null;
        var devices = UsbManager.GetAllDevices();
        try
        {
            UsbDevice? match = null;

            if (!string.IsNullOrEmpty(opts.Serial))
            {
                match = devices.FirstOrDefault(d =>
                    string.Equals(d.SerialNumber, opts.Serial, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    Console.Error.WriteLine($"error: device '{opts.Serial}' not found");
                    return null;
                }
            }
            else if (devices.Count == 1)
            {
                match = devices[0];
            }
            else if (devices.Count == 0)
            {
                Console.Error.WriteLine("error: no devices/emulators found");
                return null;
            }
            else
            {
                Console.Error.WriteLine("error: more than one device/emulator");
                foreach (var d in devices)
                {
                    Console.Error.WriteLine($"  {d.SerialNumber}    device");
                }

                return null;
            }

            foreach (var d in devices)
            {
                if (!ReferenceEquals(d, match))
                {
                    d.Dispose();
                }
            }

            return match;
        }
        catch (Exception ex)
        {
            openError = ex;
            foreach (var d in devices)
            {
                d.Dispose();
            }
            return null;
        }
    }

    private static AdbConnection Connect(UsbDevice device)
    {
        using var auth = LoadTrustedAuthentication();
        var connection = new AdbConnection(device, auth);
        connection.Connect();

        // Event-driven wait for the CNXN banner (auth may need a few round trips).
        if (!connection.WaitForPeer(10000))
        {
            connection.Dispose();
            throw new InvalidOperationException("Failed to establish ADB connection with the device.");
        }

        return connection;
    }

    /// <summary>
    /// Loads the user's trusted ADB key (~/.android/adbkey) so that
    /// ro.adb.secure devices accept us, mirroring the official adb client.
    /// Falls back to a freshly generated key when none is stored.
    /// <para>加载用户受信任的 ADB 密钥（~/.android/adbkey），使 ro.adb.secure
    /// 设备接受我们，与官方 adb 客户端行为一致。无存储密钥时回退为新建密钥。</para>
    /// </summary>
    private static AdbAuthentication LoadTrustedAuthentication()
    {
        string keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "adbkey");
        if (File.Exists(keyPath))
        {
            try
            {
                RSA rsa = RSA.Create();
                rsa.ImportFromPem(File.ReadAllText(keyPath));
                // AdbAuthentication owns the RSA instance (it disposes it),
                // so it must NOT be disposed here.
                // <para>AdbAuthentication 拥有 RSA 实例（由其负责释放），此处不得释放。</para>
                return new AdbAuthentication(rsa);
            }
            catch
            {
                // Stored key is unreadable; fall back to a fresh identity.
            }
        }

        return AdbAuthentication.CreateNew();
    }

    /// <summary>
    /// Opens a connection to the target: a direct TCP adbd endpoint when
    /// <c>--host</c> is given, otherwise the selected USB device. When USB is
    /// used, the opened <see cref="UsbDevice"/> is returned via
    /// <paramref name="device"/> and must be disposed by the caller.
    /// <para>打开到目标的连接：指定 <c>--host</c> 时直连 TCP adbd 端点，
    /// 否则连接选中的 USB 设备。使用 USB 时通过 <paramref name="device"/>
    /// 返回已打开的 <see cref="UsbDevice"/>，由调用方负责释放。</para>
    /// </summary>
    private static AdbConnection? OpenConnection(Options.GlobalOptions opts, out UsbDevice? device)
    {
        device = null;

        if (_debug)
        {
            Console.Error.WriteLine($"debug: host='{opts.Host}' port={opts.Port} serial='{opts.Serial}'");
        }

        if (ResolveTcpTarget(opts, out string host, out int port))
        {
            var transport = new AdbTcpTransport(host, port, connectTimeoutMs: 5000);
            var connection = new AdbConnection(transport, LoadTrustedAuthentication());
            connection.Connect();

            // Event-driven wait for the CNXN banner (sub-millisecond vs up to 10 s polling).
            if (!connection.WaitForPeer(10000))
            {
                connection.Dispose();
                throw new InvalidOperationException($"Cannot reach ADB device at {host}:{port}.");
            }

            return connection;
        }

        device = OpenTarget(opts);
        if (device is null)
        {
            return null;
        }

        return Connect(device);
    }

    /// <summary>
    /// Resolves the TCP target for a command: an explicit --host wins, otherwise
    /// the endpoint saved by `connect` (optionally selected via `-s host:port`).
    /// Returns false when no TCP target applies (USB mode).
    /// <para>解析命令的 TCP 目标：显式 --host 优先，其次 `connect` 保存的端点
    /// （可用 `-s host:port` 选择）。无 TCP 目标时（USB 模式）返回 false。</para>
    /// </summary>
    private static bool ResolveTcpTarget(Options.GlobalOptions opts, out string host, out int port)
    {
        host = opts.Host ?? string.Empty;
        port = ResolveTcpPort(opts);

        // `-d` (global) forces the USB transport; never fall back to TCP.
        // <para>`-d`（全局）强制 USB 传输，绝不回落到 TCP。</para>
        if (opts.UseUsb)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(host))
        {
            return true;
        }

        string? saved = SavedEndpoint;
        if (saved is not null
            && (opts.Serial is null || string.Equals(opts.Serial, saved, StringComparison.OrdinalIgnoreCase))
            && TryParseEndpoint(saved, out string savedHost, out int savedPort))
        {
            host = savedHost;
            port = savedPort;
            return true;
        }

        return false;
    }

    // ---- get-state / get-serialno / get-devpath ----------------------------

    private static int ExecuteGetState(Options.GetStateVerb opts)
    {
        var device = OpenTarget(opts);
        if (device is null) return 1;

        try
        {
            Console.WriteLine("device");
            return 0;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteGetSerialNo(Options.GetSerialNoVerb opts)
    {
        var device = OpenTarget(opts);
        if (device is null) return 1;

        try
        {
            Console.WriteLine(device.SerialNumber ?? "????????????");
            return 0;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteGetDevPath(Options.GetDevPathVerb opts)
    {
        var device = OpenTarget(opts);
        if (device is null) return 1;

        try
        {
            Console.WriteLine(device.DevicePath);
            return 0;
        }
        finally
        {
            device?.Dispose();
        }
    }

    // ---- shell --------------------------------------------------------------

    private static int ExecuteShell(Options.ShellVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            string command = string.Join(' ', opts.Command);

            // No command => interactive shell. Allocate a pty by default (matching
            // official adb); `-T` disables it, `-t` forces it. <para>无命令 =>
            // 交互式 shell。默认分配 pty（与官方 adb 一致）；`-T` 关闭，`-t` 强制。</para>
            bool interactive = string.IsNullOrEmpty(command);
            // Allocate a pty only for a real TTY session; piped stdin (e.g.
            // `echo cmd | adb shell`) runs a raw shell like the official adb.
            // <para>仅在真正的 TTY 会话分配 pty；管道 stdin（如 `echo cmd | adb shell`）
            // 运行原始 shell，与官方 adb 一致。</para>
            bool pty = interactive
                ? !opts.NoPty && !Console.IsInputRedirected
                : opts.Pty && !opts.NoPty;

            var shell = new AdbShellClient(connection, command,
                term: opts.Term ?? (pty ? "xterm-256color" : null),
                pty: pty);

            int exitCode;
            if (interactive)
            {
                // Forward Ctrl+C to the remote shell (TreatControlCAsInput is set in
                // RunInteractive); do not let it terminate the local process.
                ConsoleCancelEventHandler cancelOnCancel = (_, e) => e.Cancel = true;
                Console.CancelKeyPress += cancelOnCancel;
                try
                {
                    exitCode = shell.RunInteractive();
                }
                finally
                {
                    Console.CancelKeyPress -= cancelOnCancel;
                }
            }
            else
            {
                exitCode = shell.ExecuteStreaming(
                    chunk => Console.Out.Write(Encoding.UTF8.GetString(chunk)),
                    chunk => Console.Error.Write(Encoding.UTF8.GetString(chunk)));
            }

            Console.Out.Flush();
            Console.Error.Flush();
            return opts.NoExitCode ? 0 : exitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    // ---- push / pull ----------------------------------------------------------

    private static int ExecutePush(Options.PushVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            using var sync = new AdbSyncClient(connection);

            // When the destination ends with '/', adb treats it as a directory and
            // appends the source basename (matching Google adb push behavior).
            string remote = opts.Remote;
            if (remote.EndsWith('/') || remote.EndsWith('\\'))
            {
                remote = remote + Path.GetFileName(opts.Local);
            }

            long bytes = new FileInfo(opts.Local).Length;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            sync.Push(opts.Local, remote);
            sw.Stop();

            // Official adb format:
            //   <file>: 1 file pushed, 0 skipped. 9.4 MB/s (8832 bytes in 0.001s)
            Console.WriteLine($"{opts.Local}: 1 file pushed, 0 skipped. {FormatRate(bytes, sw.Elapsed.TotalSeconds)} ({bytes} bytes in {sw.Elapsed.TotalSeconds:F3}s)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecutePull(Options.PullVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            string local = opts.Local ?? Path.GetFileName(opts.Remote.TrimEnd('/'));
            using var sync = new AdbSyncClient(connection);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            sync.Pull(opts.Remote, local);
            sw.Stop();
            long bytes = new FileInfo(local).Length;

            // Official adb format:
            //   <file>: 1 file pulled, 0 skipped. 9.4 MB/s (8832 bytes in 0.001s)
            // <para>官方 adb 格式：<file>: 1 file pulled, 0 skipped. 9.4 MB/s (...)</para>
            Console.WriteLine($"{local}: 1 file pulled, 0 skipped. {FormatRate(bytes, sw.Elapsed.TotalSeconds)} ({bytes} bytes in {sw.Elapsed.TotalSeconds:F3}s)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    /// <summary>
    /// Formats a transfer rate the way the official adb does (e.g. "9.4 MB/s").
    /// <para>按官方 adb 的方式格式化传输速率（如 "9.4 MB/s"）。</para>
    /// </summary>
    private static string FormatRate(long bytes, double seconds)
    {
        if (seconds <= 0)
        {
            seconds = 0.001;
        }

        double rate = bytes / seconds;
        return rate switch
        {
            >= 1024.0 * 1024.0 * 1024.0 => $"{rate / (1024.0 * 1024.0 * 1024.0):F1} GB/s",
            >= 1024.0 * 1024.0 => $"{rate / (1024.0 * 1024.0):F1} MB/s",
            >= 1024.0 => $"{rate / 1024.0:F1} KB/s",
            _ => $"{rate:F1} B/s",
        };
    }

    // ---- install / uninstall ----------------------------------------------------

    private static int ExecuteInstall(Options.InstallVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            using var sync = new AdbSyncClient(connection);

            string remote = "/data/local/tmp/" + Path.GetFileName(opts.Package);
            sync.Push(opts.Package, remote);

            var shell = new AdbShellClient(connection, BuildInstallCommand(opts, remote));
            ShellResult result = shell.Execute();
            string output = Encoding.UTF8.GetString(result.Stdout) + Encoding.UTF8.GetString(result.Stderr);
            Console.Write(output);

            if (result.ExitCode == 0 && output.Contains("Success", StringComparison.Ordinal))
            {
                return 0;
            }

            Console.Error.WriteLine($"error: install failed");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static string BuildInstallCommand(Options.InstallVerb opts, string remote)
    {
        var sb = new StringBuilder("pm install");
        if (opts.Reinstall) sb.Append(" -r");
        if (opts.Downgrade) sb.Append(" -d");
        if (opts.SdCard) sb.Append(" -s");
        if (opts.AllowTest) sb.Append(" -t");
        if (opts.ForwardLock) sb.Append(" -l");
        if (opts.InternalFlash) sb.Append(" -f");
        if (opts.GrantPermissions) sb.Append(" -g");
        if (!string.IsNullOrEmpty(opts.Installer)) sb.Append(" -i ").Append(opts.Installer);
        if (!string.IsNullOrEmpty(opts.User)) sb.Append(" --user ").Append(opts.User);
        if (!string.IsNullOrEmpty(opts.Abi)) sb.Append(" --abi ").Append(opts.Abi);
        sb.Append(' ').Append(remote);
        return sb.ToString();
    }

    private static int ExecuteUninstall(Options.UninstallVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            string command = opts.KeepData
                ? $"pm uninstall -k {opts.Package}"
                : $"pm uninstall {opts.Package}";

            var shell = new AdbShellClient(connection, command);
            ShellResult result = shell.Execute();
            string output = Encoding.UTF8.GetString(result.Stdout) + Encoding.UTF8.GetString(result.Stderr);
            Console.Write(output);

            return result.ExitCode == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    // ---- reboot / remount / root / unroot ----------------------------------------

    private static int ExecuteReboot(Options.RebootVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            var services = new AdbDeviceServices(connection);
            services.Reboot(opts.Mode);
            return 0;
        }
        catch (Exception ex)
        {
            // Rebooting often kills the transport; treat a transport-level
            // failure during reboot as success unless it is a handshake error.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteRebootBootloader(Options.RebootBootloaderVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            var services = new AdbDeviceServices(connection);
            services.Reboot("bootloader");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteRemount(Options.RemountVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            var services = new AdbDeviceServices(connection);
            string output = services.Remount();
            Console.Write(output);
            return output.Contains("remount succeeded", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteRoot(Options.RootVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            var services = new AdbDeviceServices(connection);
            string output = services.RunService("root:");
            Console.Write(output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteUnroot(Options.UnrootVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            var services = new AdbDeviceServices(connection);
            string output = services.RunService("unroot:");
            Console.Write(output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    // ---- usb / tcpip / logcat / bugreport / features / wait-for-device / help ----

    private static int ExecuteUsb(Options.UsbVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            var services = new AdbDeviceServices(connection);
            Console.Write(services.RunService("usb:"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteTcpIp(Options.TcpIpVerb opts)
    {
        if (!int.TryParse(opts.ListenPort, out int port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine("error: invalid port");
            return 1;
        }

        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            var services = new AdbDeviceServices(connection);
            Console.Write(services.RunService($"tcpip:{port}"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteLogcat(Options.LogcatVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            string command = string.Join(' ', opts.Args.Prepend("logcat"));
            var shell = new AdbShellClient(connection, command);
            int exitCode = shell.ExecuteStreaming(
                chunk => Console.Out.Write(Encoding.UTF8.GetString(chunk)),
                chunk => Console.Error.Write(Encoding.UTF8.GetString(chunk)));
            Console.Out.Flush();
            return exitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteBugreport(Options.BugreportVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            var shell = new AdbShellClient(connection, "bugreport");

            if (string.IsNullOrEmpty(opts.LocalPath))
            {
                int exitCode = shell.ExecuteStreaming(
                    chunk => Console.Out.Write(Encoding.UTF8.GetString(chunk)),
                    chunk => Console.Error.Write(Encoding.UTF8.GetString(chunk)));
                Console.Out.Flush();
                return exitCode;
            }

            using var file = File.Create(opts.LocalPath);
            int code = shell.ExecuteStreaming(
                chunk => file.Write(chunk, 0, chunk.Length),
                chunk => Console.Error.Write(Encoding.UTF8.GetString(chunk)));
            Console.WriteLine(opts.LocalPath);
            return code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteFeatures(Options.FeaturesVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;
            foreach (string feature in connection.PeerFeatures)
            {
                Console.WriteLine(feature);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static int ExecuteWaitForDevice(Options.WaitForDeviceVerb opts)
    {
        // Direct-write equivalent of `adb wait-for-device`: poll until the target
        // (USB device or --host endpoint) becomes reachable, then exit 0.
        // <para>`adb wait-for-device` 的直写等价物：轮询直到目标（USB 设备或
        // --host 端点）可达，然后以 0 退出。</para>
        int deadline = Environment.TickCount + 600_000; // up to 10 minutes
        while (true)
        {
            if (ResolveTcpTarget(opts, out string host, out int port))
            {
                try
                {
                    using var transport = new AdbTcpTransport(host, port, connectTimeoutMs: 2000);
                    return 0;
                }
                catch
                {
                    // Endpoint not reachable yet; keep polling.
                }
            }
            else
            {
                // Use FirmwareKit.Comm 1.1.0's native wait API (250 ms polling) instead of
                // manual enumeration, and keep a small extra polling margin for the loop.
                // <para>使用 FirmwareKit.Comm 1.1.0 的原生等待 API（250 ms 轮询）替代
                // 手动枚举，并为循环保留一点额外轮询余量。</para>
                int remaining = Math.Max(0, deadline - Environment.TickCount);
                bool appeared = UsbManager
                    .WaitForDeviceAppearAsync(TimeSpan.FromMilliseconds(Math.Min(remaining, 2000)))
                    .GetAwaiter()
                    .GetResult();
                if (appeared) return 0;
            }

            if (Environment.TickCount >= deadline)
            {
                Console.Error.WriteLine("error: timed out waiting for device");
                return 1;
            }

            Thread.Sleep(500);
        }
    }

    private static int ExecuteHelp(Options.HelpVerb opts)
    {
        Usage(Console.Error);
        return 0;
    }

    private static int ExecuteReverse(Options.ReverseVerb opts)
    {
        UsbDevice? device = null;
        try
        {
            using var connection = OpenConnection(opts, out device);
            if (connection is null) return 1;

            if (opts.List || opts.RemoveAll || !string.IsNullOrEmpty(opts.Remove))
            {
                string service = opts.List
                    ? "reverse:list-forward"
                    : opts.RemoveAll
                        ? "reverse:killforward-all"
                        : $"reverse:killforward:{opts.Remove}";
                var services = new AdbDeviceServices(connection);
                string response = services.RunService(service);

                if (opts.List)
                {
                    // list-forward returns ADB's length-prefixed format:
                    //   <4 hex chars length><serial> <local> <remote>\n
                    // terminated by "0000" (length 0). The official `adb reverse
                    // --list` prints each entry on its own line and nothing for an
                    // empty list.
                    PrintForwardList(response);
                }
                // killforward(-all) returns "OKAY" on success, which the official
                // adb prints as nothing (no output on success).
                return 0;
            }

            if (!string.IsNullOrEmpty(opts.Remote) && !string.IsNullOrEmpty(opts.Local))
            {
                // Adding a reverse forward establishes a persistent listener on
                // the device; open the service and close immediately, like the
                // official `adb reverse`. In direct-write mode the forward lives
                // only as long as this connection (each CLI invocation opens its
                // own transport), so it is intended for scripted use on one
                // connection. <para>新增反向转发会在设备上建立常驻监听；与官方
                // `adb reverse` 一致，打开服务后立即关闭。直写模式下转发只在本连接
                // 存续期间有效（每次 CLI 调用各自打开一条传输）。</para>
                using var stream = connection.OpenStream($"reverse:forward:{opts.Remote}:{opts.Local}");
                Console.WriteLine(opts.Local);
                return 0;
            }

            Console.Error.WriteLine(
                "error: bad request: use 'reverse --list', 'reverse --remove <remote>', " +
                "'reverse --remove-all', or 'reverse <remote> <local>'");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            device?.Dispose();
        }
    }

    /// <summary>
    /// Parses and prints the ADB forward-list response (used by both host:list-forward
    /// and reverse:list-forward). The wire format is length-prefixed entries:
    /// <c>&lt;4 hex chars length&gt;&lt;serial&gt; &lt;local&gt; &lt;remote&gt;\n</c>,
    /// terminated by <c>0000</c>. The official adb prints each entry on its own line.
    /// <para>解析并打印 ADB forward-list 响应（host:list-forward 与 reverse:list-forward
    /// 通用）。线上格式为长度前缀条目：<c>&lt;4 个十六进制字符长度&gt;&lt;serial&gt;
    /// &lt;local&gt; &lt;remote&gt;\n</c>，以 <c>0000</c> 结束。官方 adb 每行打印一条。</para>
    /// </summary>
    private static void PrintForwardList(string response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return;
        }

        int i = 0;
        while (i + 4 <= response.Length)
        {
            string lengthHex = response.Substring(i, 4);
            i += 4;
            if (!int.TryParse(lengthHex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int length) || length == 0)
            {
                break; // "0000" terminator or malformed
            }

            if (i + length > response.Length)
            {
                break;
            }

            string entry = response.Substring(i, length).TrimEnd('\n', '\r');
            i += length;
            if (entry.Length > 0)
            {
                Console.WriteLine(entry);
            }
        }
    }

    private static int ExecuteConnect(Options.ConnectVerb opts)
    {
        if (!TryParseEndpoint(opts.Endpoint, out string host, out int port))
        {
            Console.Error.WriteLine($"error: bad request: invalid endpoint '{opts.Endpoint}'");
            return 1;
        }

        string endpoint = $"{host}:{port}";
        try
        {
            using var transport = new AdbTcpTransport(host, port, connectTimeoutMs: 5000);
            using var connection = new AdbConnection(transport, LoadTrustedAuthentication());
            connection.Connect();

            if (!connection.WaitForPeer(10000))
            {
                Console.Error.WriteLine($"error: failed to connect to '{endpoint}'");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: failed to connect to '{endpoint}': {ex.Message}");
            return 1;
        }

        // Direct-write: remember the endpoint so later commands target it without
        // a server registry. <para>直写模式：记住端点，后续命令无需服务端注册表
        // 即可直接定位它。</para>
        SaveEndpoint(endpoint);
        Console.WriteLine($"connected to {endpoint}");
        return 0;
    }

    private static int ExecuteDisconnect(Options.DisconnectVerb opts)
    {
        string? saved = SavedEndpoint;

        if (string.IsNullOrEmpty(opts.Endpoint))
        {
            if (saved is not null)
            {
                ClearEndpoint();
            }

            Console.WriteLine("disconnected everything");
            return 0;
        }

        if (saved is not null && string.Equals(saved, opts.Endpoint, StringComparison.OrdinalIgnoreCase))
        {
            ClearEndpoint();
            Console.WriteLine($"disconnected {saved}");
            return 0;
        }

        Console.Error.WriteLine($"error: no such device '{opts.Endpoint}'");
        return 1;
    }

    private static int ExecuteMdns(Options.MdnsVerb opts)
    {
        string subcommand = string.IsNullOrEmpty(opts.Subcommand) ? "services" : opts.Subcommand.ToLowerInvariant();

        switch (subcommand)
        {
            case "check":
                // Probe whether native UDP multicast mDNS is usable on this host.
                // <para>探测本机原生 UDP 组播 mDNS 是否可用。</para>
                try
                {
                    using var probe = new UdpClient();
                    probe.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    probe.Client.Bind(new IPEndPoint(IPAddress.Any, AdbMdnsDiscovery.MdnsPort));
                    Console.WriteLine("mDNS daemon is running");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"error: mDNS is not available: {ex.Message}");
                    return 1;
                }

            case "services":
                try
                {
                    IReadOnlyList<MdnsDevice> devices =
                        AdbMdnsDiscovery.DiscoverAdbDevicesAsync().GetAwaiter().GetResult();
                    Console.WriteLine("List of discovered devices:");
                    foreach (MdnsDevice device in devices)
                    {
                        string address = device.Addresses.Count > 0 ? device.Addresses[0] : "?";
                        Console.WriteLine($"{device.ServiceInstanceName}  {address}:{device.Port}");
                    }

                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"error: mdns services failed: {ex.Message}");
                    return 1;
                }

            default:
                Console.Error.WriteLine($"error: unknown mdns subcommand '{opts.Subcommand}' (expected: check | services)");
                return 1;
        }
    }

    // ---- version ---------------------------------------------------------------

    /// <summary>
    /// Resolves the TCP port for a direct adbd endpoint: explicit -P wins,
    /// then $ADB_TEST_PORT, then the adbd default 5555.
    /// <para>解析直连 adbd 端点的 TCP 端口：显式 -P 优先，其次 $ADB_TEST_PORT，
    /// 最后回退到 adbd 默认端口 5555。</para>
    /// </summary>
    private static int ResolveTcpPort(Options.GlobalOptions opts)
        => opts.Port
            ?? (int.TryParse(Environment.GetEnvironmentVariable("ADB_TEST_PORT"), out int envPort) ? envPort : 5555);

    /// <summary>
    /// Path of the file that remembers the endpoint from `connect`; overridable
    /// via $ADB_CONNECT_FILE (used by tests).
    /// <para>`connect` 保存端点的文件路径；可用 $ADB_CONNECT_FILE 覆盖（测试用）。</para>
    /// </summary>
    private static string ConnectStateFile =>
        Environment.GetEnvironmentVariable("ADB_CONNECT_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".firmwarekit_adb_endpoint");

    private static string? SavedEndpoint
    {
        get
        {
            try
            {
                if (!File.Exists(ConnectStateFile))
                {
                    return null;
                }

                string value = File.ReadAllText(ConnectStateFile).Trim();
                return value.Length == 0 ? null : value;
            }
            catch
            {
                return null;
            }
        }
    }

    private static void SaveEndpoint(string endpoint)
    {
        string dir = Path.GetDirectoryName(ConnectStateFile)!;
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(ConnectStateFile, endpoint);
    }

    private static void ClearEndpoint()
    {
        try
        {
            if (File.Exists(ConnectStateFile))
            {
                File.Delete(ConnectStateFile);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Parses "host" or "host:port" (default port 5555). IPv6 literals are not
    /// supported; use the bracketed form only for hostnames without colons.
    /// <para>解析 "host" 或 "host:port"（默认端口 5555）。不支持 IPv6 字面量。</para>
    /// </summary>
    private static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        int colon = endpoint.LastIndexOf(':');
        if (colon > 0
            && int.TryParse(endpoint[(colon + 1)..], out port)
            && port is >= 1 and <= 65535
            && !string.IsNullOrWhiteSpace(endpoint[..colon]))
        {
            host = endpoint[..colon];
            return true;
        }

        if (colon < 0)
        {
            host = endpoint;
            port = 5555;
            return true;
        }

        return false;
    }

    private static bool TryConnectQuickly(string endpoint)
    {
        if (!TryParseEndpoint(endpoint, out string host, out int port))
        {
            return false;
        }

        try
        {
            using var transport = new AdbTcpTransport(host, port, connectTimeoutMs: 2000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ExecuteVersion(Options.VersionVerb opts)
    {
        PrintVersion(Console.Out);
        return 0;
    }

    /// <summary>
    /// Prints the version banner in the official adb layout: protocol version,
    /// tool version, and the installed binary path.
    /// <para>按官方 adb 布局输出版本横幅：协议版本、工具版本、已安装二进制路径。</para>
    /// </summary>
    private static void PrintVersion(TextWriter writer)
    {
        writer.WriteLine($"Android Debug Bridge version {ProtocolVersionString}");
        writer.WriteLine($"Version {VersionString}");
        writer.WriteLine($"Installed as {Environment.ProcessPath ?? "adb"}");
        writer.WriteLine($"Running on {FormatOsVersion()}");
    }

    /// <summary>
    /// Formats the OS version the way Google adb does ("Windows 10.0.26200"
    /// instead of "Microsoft Windows NT 10.0.26200.0").
    /// <para>按 Google adb 的方式格式化系统版本（"Windows 10.0.26200"，
    /// 而非 "Microsoft Windows NT 10.0.26200.0"）。</para>
    /// </summary>
    private static string FormatOsVersion()
    {
        if (OperatingSystem.IsWindows())
        {
            // Environment.OSVersion.Version gives major.minor.build.revision; adb
            // shows major.minor.build without the revision suffix.
            Version v = Environment.OSVersion.Version;
            return $"Windows {v.Major}.{v.Minor}.{v.Build}";
        }
        if (OperatingSystem.IsMacOS()) return $"macOS {Environment.OSVersion.VersionString}";
        if (OperatingSystem.IsLinux()) return $"Linux {Environment.OSVersion.VersionString}";
        return Environment.OSVersion.VersionString;
    }

    /// <summary>
    /// Prints the Google-adb style help text. Like the official adb, usage is
    /// written to the given writer (stderr for errors and for <c>help</c>);
    /// only <c>version</c> prints to stdout.
    /// <para>输出谷歌 adb 风格的帮助文本。与官方 adb 一致，用法写到指定写入器
    /// （错误与 <c>help</c> 均输出到 stderr）；仅 <c>version</c> 输出到 stdout。</para>
    /// </summary>
    private static void Usage(TextWriter writer)
    {
        PrintVersion(writer);
        writer.WriteLine();
        writer.WriteLine("global options:");
        writer.WriteLine(" -a                     listen on all network interfaces, not just localhost");
        writer.WriteLine(" -d                     use USB device (error if multiple devices connected)");
        writer.WriteLine(" -e                     use TCP/IP device (error if multiple TCP/IP devices available)");
        writer.WriteLine(" -s SERIAL              use device with given serial number (overrides $ANDROID_SERIAL)");
        writer.WriteLine(" -t ID                  use device with given transport id");
        writer.WriteLine(" -H HOST                connect directly to an adbd TCP endpoint instead of USB");
        writer.WriteLine(" -P PORT                TCP port of the adbd endpoint (default 5555 or $ADB_TEST_PORT)");
        writer.WriteLine(" -L SOCKET              listen on given socket for adb server");
        writer.WriteLine(" --one-device SERIAL    use device with given serial number (fail if more than one device is present)");
        writer.WriteLine(" --exit-on-disconnect   if the device disconnects, exit with code 1");
        writer.WriteLine();
        writer.WriteLine("general commands:");
        writer.WriteLine(" devices [-l]             list connected devices (-l for long output)");
        writer.WriteLine(" help                     show this help message");
        writer.WriteLine(" version                  show version num");
        writer.WriteLine();
        writer.WriteLine("networking:");
        writer.WriteLine(" connect HOST[:PORT]      connect to a device via TCP/IP [default port=5555]");
        writer.WriteLine(" disconnect [HOST[:PORT]] disconnect from given TCP/IP device [default port=5555], or all");
        writer.WriteLine(" reverse --list           list all reverse socket connections from device");
        writer.WriteLine(" reverse [--no-rebind] REMOTE LOCAL");
        writer.WriteLine("                          reverse socket connections using the given spec");
        writer.WriteLine(" reverse --remove REMOTE  remove specific reverse socket connection");
        writer.WriteLine(" reverse --remove-all     remove all reverse socket connections from device");
        writer.WriteLine(" mdns check               check if mdns discovery is available");
        writer.WriteLine(" mdns services            list all discovered services");
        writer.WriteLine();
        writer.WriteLine("file transfer:");
        writer.WriteLine(" push LOCAL... REMOTE     copy local files/directories to device");
        writer.WriteLine(" pull REMOTE... [LOCAL]   copy files/dirs from device");
        writer.WriteLine();
        writer.WriteLine("shell:");
        writer.WriteLine(" shell [-e ESCAPE] [-n] [-Tt] [-x] [COMMAND...]");
        writer.WriteLine("                          run remote shell command (interactive shell if no command given)");
        writer.WriteLine();
        writer.WriteLine("app installation:");
        writer.WriteLine(" install [-lrtsdg] [--instant] PACKAGE");
        writer.WriteLine("                          push a single package to the device and install it");
        writer.WriteLine(" uninstall [-k] PACKAGE   remove this app package from the device");
        writer.WriteLine();
        writer.WriteLine("debugging:");
        writer.WriteLine(" bugreport [PATH]         write bugreport to given PATH");
        writer.WriteLine(" logcat                   show device log (logcat --help for more)");
        writer.WriteLine();
        writer.WriteLine("device control:");
        writer.WriteLine(" get-state                print offline | bootloader | device");
        writer.WriteLine(" get-serialno             print the device serial number");
        writer.WriteLine(" get-devpath              print the device path");
        writer.WriteLine(" wait-for-device          wait until a device is available, then exit");
        writer.WriteLine(" reboot [MODE]            reboot the device (bootloader, recovery, sideload, fastboot)");
        writer.WriteLine(" reboot-bootloader        reboot the device into bootloader");
        writer.WriteLine(" remount                  remount partitions read-write");
        writer.WriteLine(" root                     restart adbd as root");
        writer.WriteLine(" unroot                   restart adbd without root");
        writer.WriteLine(" usb                      restart adbd listening on USB");
        writer.WriteLine(" tcpip PORT               restart adbd listening on TCP at PORT");
        writer.WriteLine(" features                 list features supported by the device");
    }
}
