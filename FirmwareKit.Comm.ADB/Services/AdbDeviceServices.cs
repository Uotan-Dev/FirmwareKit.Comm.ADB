using System.Text;

namespace FirmwareKit.Comm.ADB.Services;

/// <summary>
/// Client for one-shot device-level ADB services (reboot, remount, getprop, ...).
/// <para>一次性设备级 ADB 服务（reboot、remount、getprop 等）客户端。</para>
/// </summary>
public sealed class AdbDeviceServices
{
    private readonly global::FirmwareKit.Comm.ADB.AdbConnection _connection;

    /// <summary>
    /// Initializes a new device services client.
    /// <para>初始化新的设备服务客户端。</para>
    /// </summary>
    /// <param name="connection">An established ADB connection. 已建立的 ADB 连接。</param>
    public AdbDeviceServices(global::FirmwareKit.Comm.ADB.AdbConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>Runs a one-shot service and returns all output as text. 运行一次性服务并将全部输出作为文本返回。</summary>
    public string RunService(string service, int timeoutMs = 30000)
    {
        using var stream = _connection.OpenStream(service);
        var sb = new StringBuilder();
        byte[]? chunk;
        while ((chunk = stream.Read()) is not null)
        {
            sb.Append(Encoding.UTF8.GetString(chunk));
        }
        return sb.ToString();
    }

    /// <summary>Reboots the device, optionally into a mode. 重启设备（可选进入指定模式）。</summary>
    public void Reboot(string? mode = null)
    {
        string service = string.IsNullOrEmpty(mode) ? "reboot:" : $"reboot:{mode}";
        using var stream = _connection.OpenStream(service);
        // The device reboots; the stream may close abruptly, which is expected.
    }

    /// <summary>Remounts the filesystem read-write (requires userdebug). 重新挂载为可读写（需 userdebug 构建）。</summary>
    public string Remount() => RunService("remount:");

    /// <summary>Reads a system property via the shell. 通过 shell 读取系统属性。</summary>
    public string GetProp(string key)
    {
        var shell = new AdbShellClient(_connection, $"getprop {key}");
        ShellResult result = shell.Execute();
        return Encoding.UTF8.GetString(result.Stdout).Trim();
    }
}
