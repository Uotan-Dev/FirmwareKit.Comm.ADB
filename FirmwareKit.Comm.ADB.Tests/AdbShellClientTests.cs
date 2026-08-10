using FirmwareKit.Comm.ADB.Services;
using System.Text;

namespace FirmwareKit.Comm.ADB.Tests;

public class AdbShellClientTests
{
    [Fact]
    public void ParseFrames_SingleStdoutFrame()
    {
        byte[] payload = BuildFrame(ShellFrameKind.Stdout, Encoding.UTF8.GetBytes("hello\n"));

        List<ShellFrame> frames = AdbShellClient.ParseFrames(payload);

        Assert.Single(frames);
        Assert.Equal(ShellFrameKind.Stdout, frames[0].Kind);
        Assert.Equal("hello\n", Encoding.UTF8.GetString(frames[0].Payload));
    }

    [Fact]
    public void ParseFrames_MultipleKinds()
    {
        byte[] stdout = BuildFrame(ShellFrameKind.Stdout, Encoding.UTF8.GetBytes("out"));
        byte[] stderr = BuildFrame(ShellFrameKind.Stderr, Encoding.UTF8.GetBytes("err"));
        byte[] exit = BuildFrame(ShellFrameKind.Exit, BitConverter.GetBytes(0));

        byte[] combined = stdout.Concat(stderr).Concat(exit).ToArray();
        List<ShellFrame> frames = AdbShellClient.ParseFrames(combined);

        Assert.Equal(3, frames.Count);
        Assert.Equal(ShellFrameKind.Stdout, frames[0].Kind);
        Assert.Equal(ShellFrameKind.Stderr, frames[1].Kind);
        Assert.Equal(ShellFrameKind.Exit, frames[2].Kind);
        Assert.Equal(0, BitConverter.ToInt32(frames[2].Payload, 0));
    }

    [Fact]
    public void ParseFrames_TruncatedTrailingDataBecomesStdout()
    {
        // A frame header claiming 100 bytes followed by only 3 bytes of data.
        byte[] payload = new byte[8];
        payload[0] = (byte)ShellFrameKind.Stdout;
        BitConverter.GetBytes(100).CopyTo(payload, 1);
        payload[5] = 0x61;
        payload[6] = 0x62;
        payload[7] = 0x63;

        List<ShellFrame> frames = AdbShellClient.ParseFrames(payload);

        Assert.Single(frames);
        Assert.Equal(ShellFrameKind.Stdout, frames[0].Kind);
        Assert.Equal("abc", Encoding.UTF8.GetString(frames[0].Payload));
    }

    private static byte[] BuildFrame(ShellFrameKind kind, byte[] data)
    {
        byte[] frame = new byte[5 + data.Length];
        frame[0] = (byte)kind;
        BitConverter.GetBytes(data.Length).CopyTo(frame, 1);
        data.CopyTo(frame, 5);
        return frame;
    }
}
