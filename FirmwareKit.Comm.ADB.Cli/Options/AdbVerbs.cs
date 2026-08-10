using CommandLine;

namespace FirmwareKit.Comm.ADB.Cli.Options;

[Verb("devices", HelpText = "List connected ADB devices.")]
public class DevicesVerb : GlobalOptions
{
    [Option('l', HelpText = "Long listing (product/model/device/transport_id).")]
    public bool LongList { get; set; }
}

[Verb("get-state", HelpText = "Print offline | bootloader | device.")]
public class GetStateVerb : GlobalOptions
{
}

[Verb("get-serialno", HelpText = "Print the device serial number.")]
public class GetSerialNoVerb : GlobalOptions
{
}

[Verb("get-devpath", HelpText = "Print the device path.")]
public class GetDevPathVerb : GlobalOptions
{
}

[Verb("shell", HelpText = "Run a remote shell command.")]
public class ShellVerb : GlobalOptions
{
    [Option('t', HelpText = "Allocate a pseudo-terminal (pty).")]
    public bool Pty { get; set; }

    [Option('T', HelpText = "Disable pty allocation (default).")]
    public bool NoPty { get; set; }

    [Option('e', HelpText = "Escape character (accepted for compatibility; the shell is non-interactive).")]
    public string? Escape { get; set; }

    [Option('n', HelpText = "Do not read from stdin (accepted; the shell is non-interactive).")]
    public bool NoStdin { get; set; }

    [Option('x', HelpText = "Disable exit-code reporting; the CLI always exits 0.")]
    public bool NoExitCode { get; set; }

    [Option("term", HelpText = "Terminal type for the pty (e.g. xterm-256color).")]
    public string? Term { get; set; }

    [Value(0, Required = false, MetaName = "command...", HelpText = "The shell command and arguments to run.")]
    public IEnumerable<string> Command { get; set; } = [];
}

[Verb("push", HelpText = "Push a local file to the device.")]
public class PushVerb : GlobalOptions
{
    [Value(0, Required = true, MetaName = "local", HelpText = "Local file path.")]
    public string Local { get; set; } = "";

    [Value(1, Required = true, MetaName = "remote", HelpText = "Remote destination path.")]
    public string Remote { get; set; } = "";
}

[Verb("pull", HelpText = "Pull a remote file from the device.")]
public class PullVerb : GlobalOptions
{
    [Value(0, Required = true, MetaName = "remote", HelpText = "Remote source path.")]
    public string Remote { get; set; } = "";

    [Value(1, Required = false, MetaName = "local", HelpText = "Local destination path (defaults to basename).")]
    public string? Local { get; set; }
}

[Verb("install", HelpText = "Push and install an APK on the device.")]
public class InstallVerb : GlobalOptions
{
    [Value(0, Required = true, MetaName = "package", HelpText = "Path to the APK.")]
    public string Package { get; set; } = "";

    [Option('r', HelpText = "Reinstall, keeping data.")]
    public bool Reinstall { get; set; }

    [Option('d', HelpText = "Allow version code downgrade.")]
    public bool Downgrade { get; set; }

    [Option('s', HelpText = "Install on sdcard.")]
    public bool SdCard { get; set; }

    [Option('t', HelpText = "Allow test packages.")]
    public bool AllowTest { get; set; }

    [Option('l', HelpText = "Forward-lock the app (deprecated).")]
    public bool ForwardLock { get; set; }

    [Option('f', HelpText = "Install on internal flash (default).")]
    public bool InternalFlash { get; set; }

    [Option('g', HelpText = "Grant all runtime permissions.")]
    public bool GrantPermissions { get; set; }

    [Option('i', HelpText = "Installer package name to record.")]
    public string? Installer { get; set; }

    [Option("user", HelpText = "Install for the given user id (e.g. 0, 10).")]
    public string? User { get; set; }

    [Option("abi", HelpText = "Install for the given ABI (e.g. arm64-v8a).")]
    public string? Abi { get; set; }
}

[Verb("uninstall", HelpText = "Remove a package from the device.")]
public class UninstallVerb : GlobalOptions
{
    [Value(0, Required = true, MetaName = "package", HelpText = "Package name to uninstall.")]
    public string Package { get; set; } = "";

    [Option('k', HelpText = "Keep the data and cache directories.")]
    public bool KeepData { get; set; }
}

[Verb("reboot", HelpText = "Reboot the device.")]
public class RebootVerb : GlobalOptions
{
    [Value(0, Required = false, MetaName = "mode", HelpText = "Reboot mode: bootloader, recovery, sideload, or fastboot.")]
    public string? Mode { get; set; }
}

[Verb("reboot-bootloader", HelpText = "Reboot the device into bootloader.")]
public class RebootBootloaderVerb : GlobalOptions
{
}

[Verb("remount", HelpText = "Remount partitions read-write.")]
public class RemountVerb : GlobalOptions
{
}

[Verb("root", HelpText = "Restart adbd as root.")]
public class RootVerb : GlobalOptions
{
}

[Verb("unroot", HelpText = "Restart adbd without root.")]
public class UnrootVerb : GlobalOptions
{
}

[Verb("version", HelpText = "Show version information.")]
public class VersionVerb : GlobalOptions
{
}

[Verb("usb", HelpText = "Restart adbd listening on USB.")]
public class UsbVerb : GlobalOptions
{
}

[Verb("tcpip", HelpText = "Restart adbd listening on TCP at the given port.")]
public class TcpIpVerb : GlobalOptions
{
    [Value(0, Required = true, MetaName = "port", HelpText = "TCP port for adbd to listen on.")]
    public string ListenPort { get; set; } = "";
}

[Verb("logcat", HelpText = "Stream the device logcat output.")]
public class LogcatVerb : GlobalOptions
{
    [Value(0, Required = false, MetaName = "args...", HelpText = "Logcat arguments (e.g. -v threadtime).")]
    public IEnumerable<string> Args { get; set; } = [];
}

[Verb("bugreport", HelpText = "Capture a bugreport from the device (classic text format).")]
public class BugreportVerb : GlobalOptions
{
    [Value(0, Required = false, MetaName = "path", HelpText = "Local output file; defaults to stdout.")]
    public string? LocalPath { get; set; }
}

[Verb("features", HelpText = "List features supported by the device (from the connection banner).")]
public class FeaturesVerb : GlobalOptions
{
}

[Verb("wait-for-device", HelpText = "Wait until a device is available (USB or --host endpoint), then exit.")]
public class WaitForDeviceVerb : GlobalOptions
{
}

[Verb("help", HelpText = "Show command help.")]
public class HelpVerb : GlobalOptions
{
}

[Verb("reverse", HelpText = "Reverse socket connection from the device to the host (device-side service).")]
public class ReverseVerb : GlobalOptions
{
    [Option("list", HelpText = "List all reverse socket connections.")]
    public bool List { get; set; }

    [Option("remove", HelpText = "Remove the given reverse socket connection (e.g. tcp:8080).")]
    public string? Remove { get; set; }

    [Option("remove-all", HelpText = "Remove all reverse socket connections.")]
    public bool RemoveAll { get; set; }

    [Value(0, Required = false, MetaName = "remote", HelpText = "Device-side endpoint (e.g. tcp:8080).")]
    public string? Remote { get; set; }

    [Value(1, Required = false, MetaName = "local", HelpText = "Host-side endpoint (e.g. tcp:8080).")]
    public string? Local { get; set; }
}

[Verb("connect", HelpText = "Validate and remember a direct adbd TCP endpoint (host[:port]).")]
public class ConnectVerb : GlobalOptions
{
    [Value(0, Required = true, MetaName = "host:port", HelpText = "adbd TCP endpoint, e.g. 127.0.0.1:5555 (port defaults to 5555).")]
    public string Endpoint { get; set; } = "";
}

[Verb("disconnect", HelpText = "Forget a saved TCP endpoint (or all of them).")]
public class DisconnectVerb : GlobalOptions
{
    [Value(0, Required = false, MetaName = "host:port", HelpText = "Endpoint to forget; omit to forget all.")]
    public string? Endpoint { get; set; }
}

[Verb("mdns", HelpText = "mDNS discovery over native .NET UDP: 'mdns services' lists ADB devices on the LAN, 'mdns check' probes mDNS availability.")]
public class MdnsVerb : GlobalOptions
{
    [Value(0, Required = false, MetaName = "subcommand", HelpText = "services (default) or check.")]
    public string? Subcommand { get; set; }
}
