namespace FirmwareKit.Comm.ADB.Protocol;

/// <summary>
/// ADB protocol constants, aligned with AOSP (adb/protocol.h, adb.h).
/// <para>ADB 协议常量，与 AOSP（adb/protocol.h、adb.h）对齐。</para>
/// </summary>
public static class AdbProtocol
{
    /// <summary>
    /// Version of the protocol supported by this client (A_VERSION, 1.0.1).
    /// <para>本客户端支持的协议版本（A_VERSION，1.0.1）。</para>
    /// </summary>
    public const uint Version = 0x01000001;

    /// <summary>
    /// Maximum data payload size in a single message (A_MAX_PAYLOAD, 1 MiB).
    /// <para>单条消息的最大数据负载长度（A_MAX_PAYLOAD，1 MiB）。</para>
    /// </summary>
    public const uint MaxPayload = 0x00100000;

    /// <summary>
    /// Size of the fixed ADB message header.
    /// <para>ADB 消息固定头部的大小。</para>
    /// </summary>
    public const int MessageHeaderSize = 24;

    /// <summary>
    /// Features advertised by this client in the CNXN payload.
    /// <para>客户端在 CNXN 负载中宣告的特性。</para>
    /// </summary>
    public const string Features =
        "shell_v2,cmd,stat_v2,ls_v2,fixed_push_mkdir,apex,abb,fixed_push_symlink_timestamp," +
        "abb_exec,remount_shell,track_app,sendrecv_v2,sendrecv_v2_brotli,sendrecv_v2_lz4," +
        "sendrecv_v2_zstd,sendrecv_v2_dry_run_send";

    /// <summary>
    /// Builds the CNXN system-information payload ("host::features=...").
    /// <para>构建 CNXN 系统信息负载（"host::features=..."）。</para>
    /// </summary>
    public static string BuildConnectPayload() => $"host::features={Features}";
}
