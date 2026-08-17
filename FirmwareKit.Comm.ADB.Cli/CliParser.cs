using CommandLine;

namespace FirmwareKit.Comm.ADB.Cli;

/// <summary>
/// Result of parsing a raw command line: the global options consumed before the
/// verb, the concrete verb options instance, and the verb name. Mirrors the
/// split the official adb makes between global flags and the subcommand.
/// <para>解析原始命令行的结果：动词之前消费的全局选项、具体动词选项实例以及动词名。
/// 与官方 adb 在全局标志和子命令之间的划分一致。</para>
/// </summary>
internal sealed class ParsedCommand
{
    public required Options.GlobalOptions Globals { get; init; }
    public required object VerbOptions { get; init; }
    public required string Verb { get; init; }
}

/// <summary>
/// Pure parser for the adb command line. It performs no I/O and opens no devices;
/// callers inspect the returned <see cref="ParsedCommand"/> (or error) to verify
/// argument handling. The execution entry point (<see cref="Program"/>) uses the
/// same logic, so parser tests exercise the real code path.
/// <para>adb 命令行的纯解析器。不执行 I/O、不打开设备；调用方检查返回的
/// <see cref="ParsedCommand"/>（或错误）以验证参数处理。执行入口
/// （<see cref="Program"/>）使用相同逻辑，因此解析器测试覆盖真实代码路径。</para>
/// </summary>
internal static class CliParser
{
    /// <summary>
    /// The set of supported verbs, in declaration order.
    /// <para>支持的动词集合，按声明顺序。</para>
    /// </summary>
    public static readonly Type[] VerbTypes =
    [
        typeof(Options.DevicesVerb),
        typeof(Options.GetStateVerb),
        typeof(Options.GetSerialNoVerb),
        typeof(Options.GetDevPathVerb),
        typeof(Options.ShellVerb),
        typeof(Options.PushVerb),
        typeof(Options.PullVerb),
        typeof(Options.InstallVerb),
        typeof(Options.UninstallVerb),
        typeof(Options.RebootVerb),
        typeof(Options.RebootBootloaderVerb),
        typeof(Options.RemountVerb),
        typeof(Options.RootVerb),
        typeof(Options.UnrootVerb),
        typeof(Options.UsbVerb),
        typeof(Options.TcpIpVerb),
        typeof(Options.LogcatVerb),
        typeof(Options.BugreportVerb),
        typeof(Options.FeaturesVerb),
        typeof(Options.WaitForDeviceVerb),
        typeof(Options.HelpVerb),
        typeof(Options.ReverseVerb),
        typeof(Options.ConnectVerb),
        typeof(Options.DisconnectVerb),
        typeof(Options.MdnsVerb),
        typeof(Options.VersionVerb),
    ];

    public enum GlobalParseResult
    {
        Continue,
        Help,
        Version,
        Error,
    }

    /// <summary>
    /// Parses the Google-adb style global options that appear before the command
    /// verb. Options the CLI does not act on are consumed for binary compatibility.
    /// On failure, <paramref name="errorMessage"/> receives a human-readable
    /// message (without the "adb: " prefix) for the caller to print.
    /// <para>解析命令动词之前谷歌 adb 风格的全局选项。CLI 不生效的选项为二进制兼容而消费。
    /// 失败时 <paramref name="errorMessage"/> 接收可读消息（不含 "adb: " 前缀）由调用方打印。</para>
    /// </summary>
    public static GlobalParseResult ParseGlobalOptions(
        string[] args, Options.GlobalOptions globals, out int commandIndex,
        out string? errorMessage)
    {
        commandIndex = 0;
        errorMessage = null;
        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];
            if (arg.Length == 0 || arg[0] != '-' || arg == "--")
            {
                break;
            }

            switch (arg)
            {
                case "-a":
                case "--exit-on-disconnect":
                    i++;
                    break;
                case "-d":
                    globals.UseUsb = true;
                    i++;
                    break;
                case "-e":
                    globals.UseTcp = true;
                    i++;
                    break;
                case "--libusb":
                    globals.UseLibUsb = true;
                    i++;
                    break;
                case "-s":
                case "--serial":
                case "--one-device":
                    if (i + 1 >= args.Length)
                    {
                        errorMessage = $"missing argument for {arg}";
                        return GlobalParseResult.Error;
                    }
                    globals.Serial = args[i + 1];
                    i += 2;
                    break;
                case "-t":
                case "-L":
                    if (i + 1 >= args.Length)
                    {
                        errorMessage = $"missing argument for {arg}";
                        return GlobalParseResult.Error;
                    }
                    i += 2;
                    break;
                case "-H":
                    if (i + 1 >= args.Length)
                    {
                        errorMessage = $"missing argument for {arg}";
                        return GlobalParseResult.Error;
                    }
                    globals.Host = args[i + 1];
                    i += 2;
                    break;
                case "-P":
                    if (i + 1 >= args.Length)
                    {
                        errorMessage = $"missing argument for {arg}";
                        return GlobalParseResult.Error;
                    }
                    if (!int.TryParse(args[i + 1], out int port))
                    {
                        errorMessage = $"invalid port '{args[i + 1]}'";
                        return GlobalParseResult.Error;
                    }
                    globals.Port = port;
                    i += 2;
                    break;
                case "--version":
                    return GlobalParseResult.Version;
                case "--help":
                case "-h":
                case "-?":
                    return GlobalParseResult.Help;
                default:
                    errorMessage = $"unknown option {arg}";
                    return GlobalParseResult.Error;
            }
        }

        commandIndex = i;
        return GlobalParseResult.Continue;
    }

    /// <summary>
    /// Rewrites a <c>shell ...</c> argument list so tokens belonging to the remote
    /// command (including leading dashes like <c>uname -r</c>) are treated as
    /// positional values rather than unknown shell options. Post-verb global
    /// options (<c>-H</c>/<c>-P</c>/<c>--serial</c>/<c>--debug</c>) are bound to
    /// <see cref="Options.GlobalOptions"/> rather than forwarded.
    /// <para>重写 <c>shell ...</c> 参数列表，使属于远端命令的令牌（包括 <c>uname -r</c>
    /// 这类带前导短横线的参数）被当作位置值而非未知 shell 选项。动词后的全局选项
    /// （<c>-H</c>/<c>-P</c>/<c>--serial</c>/<c>--debug</c>）绑定到
    /// <see cref="Options.GlobalOptions"/> 而非转发。</para>
    /// </summary>
    public static string[] PrepareShellArgs(string[] args)
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

                // Post-verb global options (defined on GlobalOptions). They take a
                // value and must not be forwarded to the remote shell.
                if (arg is "-H" or "--host" or "-P" or "--port" or "-s" or "--serial")
                {
                    result.Add(arg);
                    if (idx + 1 < args.Length)
                    {
                        result.Add(args[++idx]);
                    }

                    continue;
                }

                // Boolean global flag (no value).
                if (arg is "--debug")
                {
                    result.Add(arg);
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

    /// <summary>
    /// Rewrites a <c>logcat ...</c> argument list so tokens belonging to the
    /// device-side logcat (including dash-prefixed options like <c>-d</c>,
    /// <c>-v threadtime</c>, <c>-t 5</c>) are treated as positional values rather
    /// than unknown verb options. Only the global options logcat does not define
    /// (<c>-H</c>/<c>-P</c>/<c>--serial</c>/<c>--debug</c>/<c>--libusb</c>) are
    /// bound to <see cref="Options.GlobalOptions"/>; <c>-s</c> (silent filter)
    /// and <c>-t</c> (last N lines) are logcat options and MUST be forwarded.
    /// <para>重写 <c>logcat ...</c> 参数列表，使属于设备端 logcat 的令牌（包括
    /// <c>-d</c>、<c>-v threadtime</c>、<c>-t 5</c> 这类带前导短横线的选项）被当作
    /// 位置值而非未知动词选项。仅保留 logcat 未定义的全局选项
    /// （<c>-H</c>/<c>-P</c>/<c>--serial</c>/<c>--debug</c>/<c>--libusb</c>）绑定到
    /// <see cref="Options.GlobalOptions"/>；<c>-s</c>（静默过滤器）与 <c>-t</c>
    /// （最近 N 行）是 logcat 选项，必须转发。</para>
    /// </summary>
    public static string[] PrepareLogcatArgs(string[] args)
    {
        var result = new List<string>(args.Length + 1) { args[0] };
        bool forwarded = false;

        for (int idx = 1; idx < args.Length; idx++)
        {
            string arg = args[idx];

            if (!forwarded)
            {
                if (arg == "--")
                {
                    forwarded = true;
                    result.Add(arg);
                    continue;
                }

                // Post-verb global options (defined on GlobalOptions) that logcat
                // does not define. They take a value and must not be forwarded.
                if (arg is "-H" or "--host" or "-P" or "--port" or "--serial")
                {
                    result.Add(arg);
                    if (idx + 1 < args.Length)
                    {
                        result.Add(args[++idx]);
                    }

                    continue;
                }

                // Boolean global flags (no value).
                if (arg is "--debug" or "--libusb")
                {
                    result.Add(arg);
                    continue;
                }

                // Any other token is a device-side logcat argument; insert `--` so
                // a leading dash (e.g. `-d`) is passed through as a value.
                result.Add("--");
                forwarded = true;
            }

            result.Add(arg);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Parses a full command line into a <see cref="ParsedCommand"/>. Returns null
    /// when the arguments request help/version or contain a parse error; the
    /// <paramref name="errorWriter"/> receives the human-readable message in those
    /// cases.
    /// <para>将完整命令行解析为 <see cref="ParsedCommand"/>。当参数请求帮助/版本或
    /// 包含解析错误时返回 null；这些情况下 <paramref name="errorWriter"/> 接收可读消息。</para>
    /// </summary>
    public static ParsedCommand? Parse(string[] args, TextWriter? errorWriter = null)
    {
        errorWriter ??= TextWriter.Null;

        var globals = new Options.GlobalOptions();
        GlobalParseResult globalResult = ParseGlobalOptions(args, globals, out int commandIndex, out string? globalError);
        switch (globalResult)
        {
            case GlobalParseResult.Help:
                return null;
            case GlobalParseResult.Version:
                return null;
            case GlobalParseResult.Error:
                return null;
        }

        if (commandIndex >= args.Length)
        {
            return null;
        }

        string command = args[commandIndex];
        if (command.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] parseArgs = command switch
        {
            "shell" => PrepareShellArgs(args[commandIndex..]),
            "logcat" => PrepareLogcatArgs(args[commandIndex..]),
            _ => args[commandIndex..],
        };

        var parser = new Parser(settings =>
        {
            settings.HelpWriter = TextWriter.Null;
            settings.AutoVersion = false;
            settings.AutoHelp = true;
            settings.EnableDashDash = true;
        });

        ParserResult<object> result = parser.ParseArguments(parseArgs, VerbTypes);
        if (result.Tag != ParserResultType.Parsed)
        {
            return null;
        }

        var opts = ((Parsed<object>)result).Value;
        ApplyGlobals(opts, globals);
        string verb = opts.GetType().GetCustomAttributes(typeof(VerbAttribute), true)
            .OfType<VerbAttribute>()
            .First().Name;

        return new ParsedCommand
        {
            Globals = globals,
            VerbOptions = opts,
            Verb = verb,
        };
    }

    internal static void ApplyGlobals(object opts, Options.GlobalOptions globals)
    {
        if (opts is not Options.GlobalOptions target)
        {
            return;
        }

        target.Serial ??= globals.Serial;
        target.Host ??= globals.Host;
        target.Port ??= globals.Port;
        target.UseUsb |= globals.UseUsb;
        target.UseTcp |= globals.UseTcp;
        target.UseLibUsb |= globals.UseLibUsb;
        target.Debug |= globals.Debug;
    }
}
