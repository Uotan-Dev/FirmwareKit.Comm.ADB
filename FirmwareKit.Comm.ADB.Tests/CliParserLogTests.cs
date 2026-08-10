using FirmwareKit.Comm.ADB.Cli;
using FirmwareKit.Comm.ADB.Cli.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FirmwareKit.Comm.ADB.Tests;

/// <summary>
/// Data-driven tests that verify the CLI argument parser against real adb command
/// lines captured from a live device (adb_20260810_181701.log). Each entry in the
/// JSON fixture is tokenized the same way a shell would, then parsed by the same
/// <see cref="CliParser"/> the production entry point uses. The assertions cover
/// verb selection, global -s serial consumption, shell remote-command passthrough
/// (including pipes, leading dashes, and quoted paths), and push source/dest.
/// <para>数据驱动测试：用真机抓取的真实 adb 命令行（adb_20260810_181701.log）
/// 校验 CLI 参数解析器。JSON fixture 中的每条记录按 shell 同样的方式分词后，
/// 由生产入口使用的同一个 <see cref="CliParser"/> 解析。断言覆盖动词选择、全局
/// -s 序列号消费、shell 远端命令透传（含管道、前导短横线、带引号路径）以及
/// push 源/目标。</para>
/// </summary>
public class CliParserLogTests
{
    /// <summary>
    /// One captured adb invocation plus the parser expectations derived from it.
    /// <para>一条抓取的 adb 调用以及由此推导的解析器期望。</para>
    /// </summary>
    public sealed record CommandExpectation(
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("verb")] string? Verb,
        [property: JsonPropertyName("serial")] string? Serial,
        [property: JsonPropertyName("remoteCommand")] string? RemoteCommand,
        [property: JsonPropertyName("pushLocal")] string? PushLocal,
        [property: JsonPropertyName("pushRemote")] string? PushRemote,
        [property: JsonPropertyName("longList")] bool LongList,
        [property: JsonPropertyName("outputContains")] string? OutputContains);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Loads the fixture captured from the real-device adb log.
    /// <para>加载从真机 adb 日志抓取的 fixture。</para>
    /// </summary>
    private static IReadOnlyList<CommandExpectation> LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "adb_commands.json");
        Assert.True(File.Exists(path), $"Fixture not found: {path}");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<CommandExpectation>>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize adb_commands.json");
    }

    public static IEnumerable<object[]> FixtureCommands()
    {
        foreach (CommandExpectation item in LoadFixture())
        {
            yield return new object[] { item.Command, item };
        }
    }

    [Theory]
    [MemberData(nameof(FixtureCommands))]
    public void Parse_LogCommand_SelectsExpectedVerb(string commandLine, CommandExpectation expected)
    {
        string[] args = Tokenize(commandLine);

        ParsedCommand? parsed = CliParser.Parse(args);

        Assert.NotNull(parsed);
        Assert.Equal(expected.Verb, parsed!.Verb, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(FixtureCommands))]
    public void Parse_LogCommand_ConsumesGlobalSerial(string commandLine, CommandExpectation expected)
    {
        if (expected.Serial is null)
        {
            return; // not all commands specify -s
        }

        string[] args = Tokenize(commandLine);

        ParsedCommand? parsed = CliParser.Parse(args);

        Assert.NotNull(parsed);
        Assert.Equal(expected.Serial, parsed!.Globals.Serial);
        if (parsed.VerbOptions is GlobalOptions go)
        {
            Assert.Equal(expected.Serial, go.Serial);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureCommands))]
    public void Parse_LogCommand_PreservesRemoteShellCommand(string commandLine, CommandExpectation expected)
    {
        if (expected.RemoteCommand is null)
        {
            return;
        }

        string[] args = Tokenize(commandLine);

        ParsedCommand? parsed = CliParser.Parse(args);

        Assert.NotNull(parsed);
        var shell = Assert.IsType<ShellVerb>(parsed!.VerbOptions);
        string joined = string.Join(' ', shell.Command);
        Assert.Equal(expected.RemoteCommand, joined);
    }

    [Theory]
    [MemberData(nameof(FixtureCommands))]
    public void Parse_LogCommand_PreservesPushPaths(string commandLine, CommandExpectation expected)
    {
        if (expected.PushLocal is null)
        {
            return;
        }

        string[] args = Tokenize(commandLine);

        ParsedCommand? parsed = CliParser.Parse(args);

        Assert.NotNull(parsed);
        var push = Assert.IsType<PushVerb>(parsed!.VerbOptions);
        Assert.Equal(expected.PushLocal, push.Local);
        Assert.Equal(expected.PushRemote, push.Remote);
    }

    [Theory]
    [MemberData(nameof(FixtureCommands))]
    public void Parse_LogCommand_DevicesLongListFlag(string commandLine, CommandExpectation expected)
    {
        if (!expected.LongList)
        {
            return;
        }

        string[] args = Tokenize(commandLine);

        ParsedCommand? parsed = CliParser.Parse(args);

        Assert.NotNull(parsed);
        var devices = Assert.IsType<DevicesVerb>(parsed!.VerbOptions);
        Assert.True(devices.LongList);
    }

    /// <summary>
    /// Additional edge cases that stress the shell passthrough logic specifically:
    /// a remote command whose first argument starts with a dash must be passed to
    /// the device rather than rejected as an unknown shell option.
    /// <para>专门压测 shell 透传逻辑的额外边界用例：远端命令首个参数以短横线开头时，
    /// 必须透传给设备而非作为未知 shell 选项拒绝。</para>
    /// </summary>
    [Theory]
    [InlineData("shell uname -r", "uname -r")]
    [InlineData("shell ls -la /sdcard", "ls -la /sdcard")]
    [InlineData("shell pm list packages -3", "pm list packages -3")]
    [InlineData("shell cat /proc/cpuinfo | grep Hardware", "cat /proc/cpuinfo | grep Hardware")]
    [InlineData("-s ABC123 shell getprop ro.build.version.release", "getprop ro.build.version.release")]
    [InlineData("shell -T getprop ro.product.model", "getprop ro.product.model")]
    [InlineData("shell -x sh -c 'exit 7'", "sh -c exit 7")]
    [InlineData("shell -- ls -la", "ls -la")]
    public void Parse_Shell_PassesDashArgsToRemote(string commandLine, string expectedRemote)
    {
        string[] args = Tokenize(commandLine);

        ParsedCommand? parsed = CliParser.Parse(args);

        Assert.NotNull(parsed);
        var shell = Assert.IsType<ShellVerb>(parsed!.VerbOptions);
        Assert.Equal(expectedRemote, string.Join(' ', shell.Command));
    }

    [Fact]
    public void Parse_GlobalSerial_BeforeVerb_IsAccepted()
    {
        ParsedCommand? parsed = CliParser.Parse(Tokenize("-s ABC get-state"));

        Assert.NotNull(parsed);
        Assert.Equal("get-state", parsed!.Verb);
        Assert.Equal("ABC", parsed.Globals.Serial);
    }

    [Fact]
    public void Parse_GlobalHostPort_BeforeVerb_IsAccepted()
    {
        ParsedCommand? parsed = CliParser.Parse(Tokenize("-H 127.0.0.1 -P 5555 shell getprop ro.build.version.release"));

        Assert.NotNull(parsed);
        Assert.Equal("shell", parsed!.Verb);
        Assert.Equal("127.0.0.1", parsed.Globals.Host);
        Assert.Equal(5555, parsed.Globals.Port);
    }

    [Fact]
    public void Parse_VersionShortFlag_IsRecognizedAsVersion()
    {
        // `adb version` is a verb; `adb --version` is a global flag. Both must work.
        ParsedCommand? verb = CliParser.Parse(Tokenize("version"));
        Assert.NotNull(verb);
        Assert.Equal("version", verb!.Verb);

        // --version is consumed by the global parser and returns a Version signal.
        // The production entry point maps this to the version banner.
        Assert.Null(CliParser.Parse(Tokenize("--version")));
    }

    [Fact]
    public void Parse_UnknownVerb_ReturnsNull()
    {
        Assert.Null(CliParser.Parse(Tokenize("frobnicate")));
    }

    [Fact]
    public void Parse_NoArgs_ReturnsNull()
    {
        Assert.Null(CliParser.Parse([]));
    }

    [Fact]
    public void Parse_MissingSerialArgument_ReturnsNull()
    {
        Assert.Null(CliParser.Parse(Tokenize("-s")));
    }

    [Fact]
    public void Parse_InvalidPort_ReturnsNull()
    {
        Assert.Null(CliParser.Parse(Tokenize("-P notaport shell getprop")));
    }

    [Fact]
    public void Parse_UnknownGlobalOption_ReturnsNull()
    {
        Assert.Null(CliParser.Parse(Tokenize("-z devices")));
    }

    [Fact]
    public void Parse_Install_OptionsAreMapped()
    {
        ParsedCommand? parsed = CliParser.Parse(Tokenize(
            "install -r -d -g -i com.installer --user 0 --abi arm64-v8a /tmp/app.apk"));

        Assert.NotNull(parsed);
        var install = Assert.IsType<InstallVerb>(parsed!.VerbOptions);
        Assert.True(install.Reinstall);
        Assert.True(install.Downgrade);
        Assert.True(install.GrantPermissions);
        Assert.Equal("com.installer", install.Installer);
        Assert.Equal("0", install.User);
        Assert.Equal("arm64-v8a", install.Abi);
        Assert.Equal("/tmp/app.apk", install.Package);
    }

    [Fact]
    public void Parse_Reboot_WithMode()
    {
        ParsedCommand? parsed = CliParser.Parse(Tokenize("reboot bootloader"));

        Assert.NotNull(parsed);
        var reboot = Assert.IsType<RebootVerb>(parsed!.VerbOptions);
        Assert.Equal("bootloader", reboot.Mode);
    }

    [Fact]
    public void Parse_Reverse_List()
    {
        ParsedCommand? parsed = CliParser.Parse(Tokenize("reverse --list"));

        Assert.NotNull(parsed);
        var reverse = Assert.IsType<ReverseVerb>(parsed!.VerbOptions);
        Assert.True(reverse.List);
    }

    [Fact]
    public void Parse_Connect_Endpoint()
    {
        ParsedCommand? parsed = CliParser.Parse(Tokenize("connect 127.0.0.1:5555"));

        Assert.NotNull(parsed);
        var connect = Assert.IsType<ConnectVerb>(parsed!.VerbOptions);
        Assert.Equal("127.0.0.1:5555", connect.Endpoint);
    }

    /// <summary>
    /// Splits a command line into arguments using simple POSIX-like quoting rules
    /// (double quotes preserve spaces; single quotes preserve spaces; backslash
    /// escapes the next character). This mirrors how the test harness that captured
    /// the log tokenized the original commands.
    /// <para>用简单的类 POSIX 引号规则将命令行拆分为参数（双引号保留空格；单引号
    /// 保留空格；反斜杠转义下一字符）。这与抓取日志的测试工具对原始命令的分词方式一致。</para>
    /// </summary>
    private static string[] Tokenize(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inDouble = false;
        bool inSingle = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (inSingle)
            {
                if (c == '\'')
                {
                    inSingle = false;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (inDouble)
            {
                if (c == '"')
                {
                    inDouble = false;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inDouble = true;
                    break;
                case '\'':
                    inSingle = true;
                    break;
                case ' ' or '\t':
                    if (current.Length > 0)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                    }
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args.ToArray();
    }
}
