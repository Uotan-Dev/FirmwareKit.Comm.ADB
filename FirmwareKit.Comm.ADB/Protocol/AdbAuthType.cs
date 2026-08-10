namespace FirmwareKit.Comm.ADB.Protocol;

/// <summary>
/// ADB authentication message subtypes.
/// <para>ADB 认证消息子类型。</para>
/// </summary>
public enum AdbAuthType : uint
{
    /// <summary>Random device token to sign. 设备下发的随机令牌，客户端需签名。</summary>
    Token = 1,
    /// <summary>RSA-SHA1 signature of the token. 用客户端私钥对令牌做的 RSA-SHA1 签名。</summary>
    Signature = 2,
    /// <summary>Client RSA public key (base64 + user@host). 客户端 RSA 公钥（base64 + user@host）。</summary>
    RSAPublicKey = 3,
}
