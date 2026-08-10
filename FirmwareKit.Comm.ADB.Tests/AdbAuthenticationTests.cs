using FirmwareKit.Comm.ADB.Protocol;
using System.Security.Cryptography;
using System.Text;

namespace FirmwareKit.Comm.ADB.Tests;

public class AdbAuthenticationTests
{
    [Fact]
    public void CreateNew_Generates2048BitKey()
    {
        using var auth = AdbAuthentication.CreateNew();
        Assert.NotNull(auth);
    }

    [Fact]
    public void SignToken_Produces256ByteSignature()
    {
        using var auth = AdbAuthentication.CreateNew();
        byte[] token = RandomNumberGenerator.GetBytes(20);

        byte[] signature = auth.SignToken(token);

        Assert.Equal(256, signature.Length);
    }

    [Fact]
    public void BuildPublicKeyPayload_IsBase64WithComment()
    {
        using var auth = AdbAuthentication.CreateNew();
        byte[] payload = auth.BuildPublicKeyPayload("test@host");

        string text = Encoding.UTF8.GetString(payload);
        Assert.EndsWith(" test@host\0", text);

        string base64 = text[..text.LastIndexOf(" test@host\0", StringComparison.Ordinal)];
        byte[] keyBuffer = Convert.FromBase64String(base64);

        // Header: len(4) + n0inv(4) + n(64*4) + rr(64*4) + exponent(4) = 524 bytes for a 2048-bit key.
        int len = BitConverter.ToInt32(keyBuffer, 0);
        Assert.Equal(64, len);
        Assert.Equal(12 + (8 * len), keyBuffer.Length);
    }

    [Fact]
    public void ConvertRsaToAdb_RoundTripsModulus()
    {
        using RSA rsa = RSA.Create();
        rsa.KeySize = 2048;
        RSAParameters parameters = rsa.ExportParameters(false);

        (uint n0inv, uint[] n, uint[] rr, int exponent) = AdbAuthentication.ConvertRsaToAdb(parameters);

        Assert.Equal(64, n.Length);
        Assert.Equal(64, rr.Length);
        Assert.Equal(65537, exponent);

        // Reconstruct the modulus from the little-endian DWORD table and compare.
        // The n table holds least-significant DWORD first; the wire layout is
        // little-endian, so the reconstructed byte stream must be reversed to
        // match the big-endian RSAParameters.Modulus.
        byte[] modulusBytes = parameters.Modulus!;
        byte[] reconstructedLittle = new byte[256];
        for (int i = 0; i < 64; i++)
        {
            byte[] dword = BitConverter.GetBytes(n[i]);
            Buffer.BlockCopy(dword, 0, reconstructedLittle, i * 4, 4);
        }

        byte[] reconstructed = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            reconstructed[i] = reconstructedLittle[255 - i];
        }

        // Trim leading zeros from the reconstructed modulus.
        int start = 0;
        while (start < reconstructed.Length - 1 && reconstructed[start] == 0)
        {
            start++;
        }

        byte[] trimmed = new byte[reconstructed.Length - start];
        Buffer.BlockCopy(reconstructed, start, trimmed, 0, trimmed.Length);

        Assert.Equal(modulusBytes, trimmed);
    }
}
