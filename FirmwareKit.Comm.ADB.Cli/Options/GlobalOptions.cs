using CommandLine;

namespace FirmwareKit.Comm.ADB.Cli.Options;

/// <summary>
/// Options accepted by every adb verb.
/// <para>每个 adb 动词都接受的选项。</para>
/// </summary>
/// <remarks>
/// Google's adb treats <c>-a -d -e -s -t -H -P -L --one-device --exit-on-disconnect</c>
/// as global options that appear BEFORE the verb; they are consumed by the top-level
/// parser, so the short forms are not defined here (this also avoids colliding with
/// verb-level options such as <c>install -s</c> (sdcard), which adb disambiguates by
/// position). Long forms and post-verb <c>-H</c>/<c>-P</c> are kept for convenience.
/// <para>全局选项由顶层解析器消费，这里不定义短形式以避免与动词级选项（如 install -s）
/// 冲突；保留长选项及动词后的 -H/-P。</para>
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

    [Option("libusb", HelpText = "Force the libusb-dotnet USB backend instead of the platform native backend.")]
    public bool UseLibUsb { get; set; }

    /// <summary>Set by the global parser for <c>-d</c> (force USB transport). -d 时由全局解析器设置。</summary>
    public bool UseUsb { get; set; }

    /// <summary>Set by the global parser for <c>-e</c> (force TCP transport). -e 时由全局解析器设置。</summary>
    public bool UseTcp { get; set; }
}
