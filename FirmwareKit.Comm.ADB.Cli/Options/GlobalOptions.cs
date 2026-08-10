using CommandLine;

namespace FirmwareKit.Comm.ADB.Cli.Options;

/// <summary>
/// Options accepted by every adb verb.
/// <para>每个 adb 动词都接受的选项。</para>
/// </summary>
/// <remarks>
/// Google's adb treats <c>-a -d -e -s -t -H -P -L --one-device --exit-on-disconnect</c>
/// as *global* options that appear BEFORE the command verb; they are consumed by
/// the top-level parser in <c>Program.Main</c>, so the short forms are not defined
/// here (that also avoids colliding with verb-level options such as
/// <c>install -s</c> (sdcard) and <c>install -l</c> (forward-lock), which the
/// official adb disambiguates by position). This class keeps the long forms and
/// the options the CLI itself accepts after the verb (<c>-H</c>/<c>-P</c> may be
/// placed after the verb for convenience).
/// <para>谷歌 adb 将 <c>-a -d -e -s -t -H -P -L --one-device --exit-on-disconnect</c>
/// 视为位于命令动词之前的全局选项，由 <c>Program.Main</c> 中的顶层解析器消费，
/// 因此这里不定义短形式（同时也避免与动词级选项冲突，如 <c>install -s</c>
/// （sdcard）与 <c>install -l</c>（forward-lock），官方 adb 按位置区分它们）。
/// 本类保留长选项及动词之后 CLI 自身接受的选项（<c>-H</c>/<c>-P</c> 为方便可放
/// 在动词之后）。</para>
/// </remarks>
public class GlobalOptions
{
    [Option("serial", HelpText = "Specify device serial number (prefer -s before the command).")]
    public string? Serial { get; set; }

    [Option('H', "host", HelpText = "Connect directly to an adbd TCP endpoint (host) instead of USB.")]
    public string? Host { get; set; }

    [Option('P', "port", HelpText = "TCP port of the adbd endpoint (default 5555, or $ADB_TEST_PORT).")]
    public int? Port { get; set; }

    [Option("debug", HelpText = "Verbose debug logging output.")]
    public bool Debug { get; set; }

    /// <summary>Set by the global-option parser when <c>-d</c> is given (force USB transport).</summary>
    /// <para>给定 <c>-d</c> 时由全局选项解析器设置（强制 USB 传输）。</para>
    public bool UseUsb { get; set; }

    /// <summary>Set by the global-option parser when <c>-e</c> is given (force TCP/IP transport).</summary>
    /// <para>给定 <c>-e</c> 时由全局选项解析器设置（强制 TCP/IP 传输）。</para>
    public bool UseTcp { get; set; }
}
