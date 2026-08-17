using System.Runtime.InteropServices;
using FirmwareKit.Comm.Abstractions;

namespace FirmwareKit.Comm.ADB.Backend.Usb;

/// <summary>
/// Platform USB backend selection, aligned with Google adb's
/// <c>transport_usb.cpp is_libusb_enabled</c>: non-Windows platforms (macOS,
/// Linux) default to libusb, Windows defaults to the native backend. The
/// defaults can be overridden via <c>ADB_LIBUSB</c> (official adb semantics)
/// and the project-specific <c>FIRMWAREKIT_USB_BACKEND</c> selector.
/// <para>平台 USB 后端选择，与谷歌 adb 的 <c>is_libusb_enabled</c> 对齐：非
/// Windows 平台（macOS、Linux）默认 libusb，Windows 默认原生后端。可通过
/// <c>ADB_LIBUSB</c>（官方 adb 语义）与项目专属 <c>FIRMWAREKIT_USB_BACKEND</c>
/// 覆盖默认值。</para>
/// </summary>
internal static class UsbBackendSelection
{
    // ADB_LIBUSB toggles the libusb transport (is_libusb_enabled): "1" forces
    // libusb, any other value disables it.
    // <para>ADB_LIBUSB 切换 libusb 传输（is_libusb_enabled）："1" 强制 libusb，任何其他值禁用。</para>
    private const string AdbLibUsbEnvVar = "ADB_LIBUSB";

    // Project-specific selector: "native", "libusb", or "auto" (platform default).
    // <para>项目专属选择器："native"、"libusb" 或 "auto"（平台默认）。</para>
    private const string FirmwareKitBackendEnvVar = "FIRMWAREKIT_USB_BACKEND";

    /// <summary>
    /// Resolves the effective default backend for the current platform after
    /// applying environment overrides: <c>FIRMWAREKIT_USB_BACKEND</c> selects
    /// native/libusb directly; otherwise <c>ADB_LIBUSB=1</c> prefers libusb and
    /// any other value forces native; without overrides the platform defaults
    /// apply (macOS/Linux → libusb, Windows → native).
    /// <para>应用环境变量覆盖后解析当前平台的有效默认后端：<c>FIRMWAREKIT_USB_BACKEND</c>
    /// 直接选择 native/libusb；否则 <c>ADB_LIBUSB=1</c> 优先 libusb、其他值强制原生；
    /// 无覆盖时应用平台默认（macOS/Linux → libusb，Windows → 原生）。</para>
    /// </summary>
    public static UsbApiKind ResolveDefault()
    {
        string? projectSelector = Environment.GetEnvironmentVariable(FirmwareKitBackendEnvVar);
        if (!string.IsNullOrWhiteSpace(projectSelector))
        {
            switch (projectSelector.Trim().ToLowerInvariant())
            {
                case "native":
                    return UsbApiKind.Native;
                case "libusb":
                    return UsbApiKind.LibUsbDotNet;
                default:
                    // Unknown value: fall back to the platform defaults.
                    // <para>未知值：回退到平台默认。</para>
                    break;
            }
        }

        string? adbLibUsb = Environment.GetEnvironmentVariable(AdbLibUsbEnvVar);
        if (adbLibUsb != null)
        {
            // ADB_LIBUSB=1 forces libusb; any other value disables it (matches
            // is_libusb_enabled: strcmp(env, "1") == 0).
            // <para>ADB_LIBUSB=1 强制 libusb；任何其他值禁用（与 is_libusb_enabled
            // 一致：strcmp(env, "1") == 0）。</para>
            return string.Equals(adbLibUsb.Trim(), "1", StringComparison.Ordinal)
                ? UsbApiKind.LibUsbDotNet
                : UsbApiKind.Native;
        }

        // Platform defaults (is_libusb_enabled semantics): Windows prefers the
        // native backend; macOS and other Unix-likes prefer libusb.
        // <para>平台默认（is_libusb_enabled 语义）：Windows 优先原生后端；
        // macOS 及其他 Unix 系优先 libusb。</para>
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return UsbApiKind.Native;
        }

        return UsbApiKind.LibUsbDotNet;
    }
}
