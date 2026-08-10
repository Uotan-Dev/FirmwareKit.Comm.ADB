using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace FirmwareKit.Comm.ADB.Protocol;

/// <summary>
/// Converts between .NET RSA keys and the ADB wire format, and performs
/// ADB RSA-SHA1 token signing. Aligned with AOSP (adb_auth_host.cpp).
/// <para>在 .NET RSA 密钥与 ADB 线上格式之间转换，并执行 ADB RSA-SHA1 令牌签名。
/// 与 AOSP（adb_auth_host.cpp）对齐。</para>
/// </summary>
public sealed class AdbAuthentication : IDisposable
{
    /// <summary>
    /// Number of DWORDs in a 2048-bit ADB RSA key (64 DWORDs = 256 bytes).
    /// <para>2048 位 ADB RSA 密钥的 DWORD 数（64 DWORD = 256 字节）。</para>
    /// </summary>
    private const int KeyLengthInDwords = 64;

    private readonly RSA _rsa;

    /// <summary>
    /// Initializes authentication with the supplied RSA key.
    /// <para>使用提供的 RSA 密钥初始化认证。</para>
    /// </summary>
    public AdbAuthentication(RSA rsa)
    {
        _rsa = rsa ?? throw new ArgumentNullException(nameof(rsa));
        if (rsa.KeySize != 2048)
        {
            throw new ArgumentException("ADB authentication requires a 2048-bit RSA key.", nameof(rsa));
        }
    }

    /// <summary>
    /// Creates authentication with a newly generated 2048-bit RSA key.
    /// <para>使用新生成的 2048 位 RSA 密钥创建认证。</para>
    /// </summary>
    public static AdbAuthentication CreateNew()
    {
        RSA rsa = RSA.Create();
        rsa.KeySize = 2048;
        return new AdbAuthentication(rsa);
    }

    /// <summary>
    /// Signs the given token with the private key using RSA-PKCS1 + SHA1 (ADB signature).
    /// <para>使用私钥以 RSA-PKCS1 + SHA1 对给定令牌签名（ADB 签名）。</para>
    /// </summary>
    public byte[] SignToken(byte[] token)
    {
        if (token is null || token.Length == 0)
        {
            throw new ArgumentException("Token must not be empty.", nameof(token));
        }

        using SHA1 sha1 = SHA1.Create();
        byte[] hash = sha1.ComputeHash(token);
        return _rsa.SignHash(hash, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
    }

    /// <summary>
    /// Builds the ADB public key payload: base64(RSAPublicKey) + " user@host\0".
    /// <para>构建 ADB 公钥负载：base64(RSAPublicKey) + " user@host\0"。</para>
    /// </summary>
    public byte[] BuildPublicKeyPayload(string? comment = null)
    {
        RSAParameters publicKey = _rsa.ExportParameters(false);
        (uint n0inv, uint[] n, uint[] rr, int exponent) = ConvertRsaToAdb(publicKey);
        byte[] adbKey = AdbRsaToBuffer(n0inv, n, rr, exponent);

        string userComment = comment ?? string.Empty;
        if (string.IsNullOrEmpty(userComment))
        {
            userComment = $"{Environment.UserName}@{Environment.MachineName}";
        }

        string payload = $"{Convert.ToBase64String(adbKey)} {userComment}\0";
        return Encoding.UTF8.GetBytes(payload);
    }

    /// <summary>
    /// Serializes an ADB RSAPublicKey structure to its wire buffer.
    /// <para>将 ADB RSAPublicKey 结构序列化为线上缓冲区。</para>
    /// </summary>
    public static byte[] AdbRsaToBuffer(uint n0inv, uint[] n, uint[] rr, int exponent)
    {
        int len = n.Length;

        byte[] buffer = new byte[12 + (8 * len)];
        using MemoryStream ms = new(buffer);
        using BinaryWriter bw = new(ms);

        bw.Write(len);
        bw.Write(n0inv);

        foreach (uint element in n)
        {
            bw.Write(element);
        }

        foreach (uint element in rr)
        {
            bw.Write(element);
        }

        bw.Write(exponent);
        return buffer;
    }

    /// <summary>
    /// Converts .NET RSA parameters into the ADB RSAPublicKey fields (n0inv, n, rr, exponent).
    /// <para>将 .NET RSA 参数转换为 ADB RSAPublicKey 字段（n0inv、n、rr、exponent）。</para>
    /// </summary>
    public static (uint n0inv, uint[] n, uint[] rr, int exponent) ConvertRsaToAdb(RSAParameters parameters)
    {
        byte[] modulus = parameters.Modulus ?? throw new ArgumentException("Modulus is missing.", nameof(parameters));
        byte[] exponentBytes = parameters.Exponent ?? throw new ArgumentException("Exponent is missing.", nameof(parameters));

        int e = (int)FromBigEndianUnsigned(exponentBytes);

        BigInteger r32 = BigInteger.One << 32;
        BigInteger n = FromBigEndianUnsigned(modulus);
        BigInteger r = BigInteger.One << (KeyLengthInDwords * 32);
        BigInteger rr = BigInteger.ModPow(r, 2, n);

        BigInteger remainder = BigInteger.Remainder(n, r32);
        BigInteger tn0inv = ModInverse(remainder, r32);
        uint n0inv = (uint)(BigInteger.Negate(tn0inv) & uint.MaxValue);

        uint[] nTable = new uint[KeyLengthInDwords];
        uint[] rrTable = new uint[KeyLengthInDwords];

        for (int i = 0; i < KeyLengthInDwords; i++)
        {
            rr = BigInteger.DivRem(rr, r32, out remainder);
            rrTable[i] = (uint)remainder;

            n = BigInteger.DivRem(n, r32, out remainder);
            nTable[i] = (uint)(remainder & uint.MaxValue);
        }

        return (n0inv, nTable, rrTable, e);
    }

    /// <summary>
    /// Converts a big-endian unsigned byte array into a <see cref="BigInteger"/>.
    /// <para>将大端无符号字节数组转换为 <see cref="BigInteger"/>。</para>
    /// </summary>
    private static BigInteger FromBigEndianUnsigned(byte[] bytes)
    {
        // BigInteger expects little-endian; reverse and keep the value positive.
        byte[] littleEndian = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            littleEndian[i] = bytes[bytes.Length - 1 - i];
        }

        if ((littleEndian[littleEndian.Length - 1] & 0x80) != 0)
        {
            Array.Resize(ref littleEndian, littleEndian.Length + 1);
            littleEndian[littleEndian.Length - 1] = 0;
        }

        return new BigInteger(littleEndian);
    }

    private static BigInteger ModInverse(BigInteger value, BigInteger modulo)
    {
        BigInteger egcd = ExtendedGcd(value, modulo, out BigInteger x, out _);
        if (egcd != BigInteger.One)
        {
            throw new InvalidOperationException("Invalid modulus for ADB key conversion.");
        }

        if (x < 0)
        {
            x += modulo;
        }

        return x % modulo;
    }

    private static BigInteger ExtendedGcd(BigInteger left, BigInteger right, out BigInteger leftFactor, out BigInteger rightFactor)
    {
        leftFactor = 0;
        rightFactor = 1;
        BigInteger u = 1;
        BigInteger v = 0;
        BigInteger gcd = 0;

        while (left != 0)
        {
            BigInteger q = right / left;
            BigInteger r = right % left;

            BigInteger m = leftFactor - (u * q);
            BigInteger n = rightFactor - (v * q);

            right = left;
            left = r;
            leftFactor = u;
            rightFactor = v;
            u = m;
            v = n;

            gcd = right;
        }

        return gcd;
    }

    /// <summary>
    /// Releases the underlying RSA key.
    /// <para>释放底层 RSA 密钥。</para>
    /// </summary>
    public void Dispose()
    {
        _rsa.Dispose();
        GC.SuppressFinalize(this);
    }
}
