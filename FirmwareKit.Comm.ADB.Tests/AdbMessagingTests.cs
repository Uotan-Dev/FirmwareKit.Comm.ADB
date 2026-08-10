using FirmwareKit.Comm.ADB.Protocol;
using System.Text;

namespace FirmwareKit.Comm.ADB.Tests;

public class AdbMessagingTests
{
    [Fact]
    public void Serialize_ProducesExpectedWireFormat()
    {
        var message = new AdbMessage(AdbCommand.Cnxn, 0x01000001, 0x00100000,
            Encoding.UTF8.GetBytes("host::features=shell_v2"));

        byte[] wire = AdbMessaging.Serialize(message);

        // "host::features=shell_v2" is 23 bytes; header is 24 bytes.
        Assert.Equal(24 + 23, wire.Length);
        Assert.Equal((uint)0x4E584E43, BitConverter.ToUInt32(wire, 0)); // CNXN
        Assert.Equal(0x01000001u, BitConverter.ToUInt32(wire, 4));
        Assert.Equal(0x00100000u, BitConverter.ToUInt32(wire, 8));
        Assert.Equal(23u, BitConverter.ToUInt32(wire, 12)); // payload length
        Assert.Equal((uint)0x4E584E43 ^ 0xFFFFFFFF, BitConverter.ToUInt32(wire, 20)); // magic
    }

    [Fact]
    public void ParseHeader_AcceptsSerializedHeader()
    {
        var message = new AdbMessage(AdbCommand.Okay, 1, 2);
        byte[] wire = AdbMessaging.Serialize(message);

        var (command, arg0, arg1, length, crc) = AdbMessaging.ParseHeader(wire);

        Assert.Equal(AdbCommand.Okay, command);
        Assert.Equal(1u, arg0);
        Assert.Equal(2u, arg1);
        Assert.Equal(0u, length);
        Assert.Equal(0u, crc);
    }

    [Fact]
    public void ParseHeader_RejectsBadMagic()
    {
        byte[] header = new byte[24];
        header[0] = 0x41;

        Assert.Throws<InvalidDataException>(() => AdbMessaging.ParseHeader(header));
    }

    [Fact]
    public void ComputeCrc_SumsBytes()
    {
        byte[] payload = [1, 2, 3, 4];
        Assert.Equal(10u, AdbMessaging.ComputeCrc(payload));
    }

    [Fact]
    public void VerifyCrc_MismatchThrows()
    {
        byte[] payload = [1, 2, 3];
        Assert.Throws<InvalidDataException>(() => AdbMessaging.VerifyCrc(payload, 99));
    }

    [Fact]
    public void BuildConnect_CarriesVersionAndFeatures()
    {
        AdbMessage message = AdbMessaging.BuildConnect();

        Assert.Equal(AdbCommand.Cnxn, message.Command);
        Assert.Equal(AdbProtocol.Version, message.Arg0);
        Assert.Equal(AdbProtocol.MaxPayload, message.Arg1);
        Assert.Contains("features=", message.PayloadAsString());
        Assert.Contains("shell_v2", message.PayloadAsString());
    }

    [Fact]
    public void BuildOpen_NullTerminatesDestination()
    {
        AdbMessage message = AdbMessaging.BuildOpen(5, "sync:");

        Assert.Equal(AdbCommand.Open, message.Command);
        Assert.Equal(5u, message.Arg0);
        Assert.Equal("sync:\0", Encoding.UTF8.GetString(message.Payload!));
    }
}
