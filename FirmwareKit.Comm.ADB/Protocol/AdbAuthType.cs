namespace FirmwareKit.Comm.ADB.Protocol;

/// <summary>
/// ADB authentication message subtypes.
/// <para>ADB 认证消息子类型。</para>
/// </summary>
public enum AdbAuthType : uint
{
    /// <summary>
    /// Random token issued by the device; client must sign it.
    /// <para>设备下发的随机令牌，客户端需对其进行签名。</para>
    /// </summary>
    Token = 1,

    /// <summary>
    /// RSA-SHA1 signature of the token using the client's private key.
    /// <para>使用客户端私钥对令牌做 RSA-SHA1 签名。</para>
    /// </summary>
    Signature = 2,

    /// <summary>
    /// Client's RSA public key (ADB format, base64 + user@host).
    /// <para>客户端的 RSA 公钥（ADB 格式，base64 + user@host）。</para>
    /// </summary>
    RSAPublicKey = 3,
}
