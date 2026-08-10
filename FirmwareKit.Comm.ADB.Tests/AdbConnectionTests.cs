using FirmwareKit.Comm.ADB.Protocol;
using FirmwareKit.Comm.ADB.Services;
using System.Text;

namespace FirmwareKit.Comm.ADB.Tests;

/// <summary>
/// Blocking in-memory ADB transport for exercising the connection logic without hardware.
/// <para>用于在无硬件情况下演练连接逻辑的阻塞式内存 ADB 传输层。</para>
/// </summary>
public sealed class LoopbackTransport : IAdbTransport
{
    private readonly object _lock = new();
    private readonly List<byte> _incoming = new(); // device -> client
    private readonly List<byte> _outgoing = new(); // client -> device
    private bool _closed;

    /// <summary>
    /// Feeds a raw message into the incoming (device) stream.
    /// <para>将原始消息写入输入（设备）流。</para>
    /// </summary>
    public void Feed(AdbMessage message)
    {
        byte[] wire = AdbMessaging.Serialize(message);
        lock (_lock)
        {
            _incoming.AddRange(wire);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Reads exactly the requested number of bytes, blocking until available.
    /// <para>精确读取指定数量的字节，阻塞至数据可用。</para>
    /// </summary>
    public byte[] Read(int length)
    {
        byte[] buffer = new byte[length];
        lock (_lock)
        {
            while (_incoming.Count < length)
            {
                if (_closed) throw new EndOfStreamException("Transport closed.");
                Monitor.Wait(_lock);
            }

            _incoming.CopyTo(0, buffer, 0, length);
            _incoming.RemoveRange(0, length);
        }

        return buffer;
    }

    /// <summary>
    /// Appends data to the outgoing (client) stream.
    /// <para>将数据追加到输出（客户端）流。</para>
    /// </summary>
    public long Write(byte[] data, int length)
    {
        lock (_lock)
        {
            _outgoing.AddRange(data.Take(length));
            Monitor.PulseAll(_lock);
        }

        return length;
    }

    /// <summary>
    /// Reads the next message the connection wrote, blocking up to the timeout;
    /// returns null when the timeout elapses or the transport is closed.
    /// <para>读取连接写入的下一条消息（阻塞至超时）；超时或传输关闭时返回 null。</para>
    /// </summary>
    public AdbMessage? TakeOutgoing(int timeoutMs = 5000)
    {
        lock (_lock)
        {
            if (!WaitForOutgoingBytes(AdbProtocol.MessageHeaderSize, timeoutMs)) return null;

            byte[] header = new byte[AdbProtocol.MessageHeaderSize];
            _outgoing.CopyTo(0, header, 0, header.Length);
            _outgoing.RemoveRange(0, header.Length);
            var (command, arg0, arg1, length, crc) = AdbMessaging.ParseHeader(header);

            byte[]? payload = null;
            if (length > 0)
            {
                if (!WaitForOutgoingBytes((int)length, timeoutMs)) return null;
                payload = new byte[length];
                _outgoing.CopyTo(0, payload, 0, (int)length);
                _outgoing.RemoveRange(0, (int)length);
            }

            return new AdbMessage(command, arg0, arg1, payload);
        }
    }

    private bool WaitForOutgoingBytes(int count, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (_outgoing.Count < count)
        {
            if (_closed) return false;
            if (Environment.TickCount64 > deadline) return false;
            Monitor.Wait(_lock, 100);
        }

        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            Monitor.PulseAll(_lock);
        }
    }
}

public class AdbConnectionTests
{
    [Fact]
    public void Connect_SendsCnxnWithFeatures()
    {
        using var transport = new LoopbackTransport();
        using var auth = AdbAuthentication.CreateNew();
        using var connection = new AdbConnection(transport, auth);

        connection.Connect();

        AdbMessage? cnxn = transport.TakeOutgoing();
        Assert.NotNull(cnxn);
        Assert.Equal(AdbCommand.Cnxn, cnxn!.Command);
        Assert.Equal(AdbProtocol.Version, cnxn.Arg0);
        Assert.Contains("features=", cnxn.PayloadAsString());
    }

    [Fact]
    public void AuthToken_TriggersSignatureReply()
    {
        using var transport = new LoopbackTransport();
        using var auth = AdbAuthentication.CreateNew();
        using var connection = new AdbConnection(transport, auth);

        connection.Connect();
        _ = transport.TakeOutgoing(); // consume CNXN

        byte[] token = new byte[20];
        transport.Feed(new AdbMessage(AdbCommand.Auth, (uint)AdbAuthType.Token, 0, token));
        Thread.Sleep(200); // let the message loop dispatch

        AdbMessage? reply = transport.TakeOutgoing();
        Assert.NotNull(reply);
        Assert.Equal(AdbCommand.Auth, reply!.Command);
        Assert.Equal((uint)AdbAuthType.Signature, reply.Arg0);
        Assert.Equal(256, reply.Payload!.Length);
    }

    [Fact]
    public void OpenStream_SendsOpenAndTracksRemoteId()
    {
        using var transport = new LoopbackTransport();
        using var auth = AdbAuthentication.CreateNew();
        using var connection = new AdbConnection(transport, auth);

        connection.Connect();
        _ = transport.TakeOutgoing(); // consume CNXN

        AdbStream stream = connection.OpenStream("shell:v2:echo hi");
        AdbMessage? open = transport.TakeOutgoing();
        Assert.NotNull(open);
        Assert.Equal(AdbCommand.Open, open!.Command);
        Assert.Equal(stream.LocalId, open.Arg0);
        Assert.EndsWith("\0", open.PayloadAsString());

        // Simulate the peer's OKAY: arg0 = peer's local id (our remote id),
        // arg1 = our local id.
        transport.Feed(new AdbMessage(AdbCommand.Okay, 42, stream.LocalId));
        Thread.Sleep(200);
        Assert.Equal(42u, stream.RemoteId);

        // Simulate incoming WRTE data (same id convention as OKAY).
        transport.Feed(new AdbMessage(AdbCommand.Wrte, 42, stream.LocalId,
            Encoding.UTF8.GetBytes("hi")));
        byte[]? data = stream.Read();
        Assert.NotNull(data);
        Assert.Equal("hi", Encoding.UTF8.GetString(data!));
    }

    [Fact]
    public void SyncClient_PushWritesSendDataDoneSequence()
    {
        using var transport = new LoopbackTransport();
        using var auth = AdbAuthentication.CreateNew();
        using var connection = new AdbConnection(transport, auth);

        connection.Connect();
        _ = transport.TakeOutgoing(); // consume CNXN

        using var sync = new AdbSyncClient(connection, useV2: false);

        // Device emulator: acknowledge the OPEN with OKAY, then respond OKAY to DONE.
        var deviceThread = new Thread(() => PumpSyncDevice(transport))
        {
            IsBackground = true,
        };
        deviceThread.Start();

        using var source = new MemoryStream(Encoding.UTF8.GetBytes("payload-data"));
        sync.PushStream(source, "/data/local/tmp/test.txt", mtime: 1700000000);

        Assert.True(deviceThread.Join(5000), "device emulator did not finish");
    }

    /// <summary>
    /// Emulates the device for a sync push: OKAY the OPEN, then answer the
    /// final DONE with a sync "OKAY" response.
    /// <para>模拟同步推送的设备端：对 OPEN 回 OKAY，并对最终 DONE 回复 sync "OKAY"。</para>
    /// </summary>
    private static void PumpSyncDevice(LoopbackTransport transport)
    {
        uint? localId = null;

        while (true)
        {
            AdbMessage? message = transport.TakeOutgoing(3000);
            if (message is null) return;

            switch (message.Command)
            {
                case AdbCommand.Open:
                    localId = message.Arg0;
                    transport.Feed(new AdbMessage(AdbCommand.Okay, message.Arg0, message.Arg0));
                    break;

                case AdbCommand.Wrte when message.Payload is { Length: >= 4 }:
                    string id = Encoding.ASCII.GetString(message.Payload, 0, 4);
                    if (id == "DONE" && localId.HasValue)
                    {
                        // Sync "OKAY" response (id + length 0) as stream data.
                        byte[] okay = new byte[8];
                        Encoding.ASCII.GetBytes("OKAY").CopyTo(okay, 0);
                        transport.Feed(new AdbMessage(AdbCommand.Wrte, localId.Value, localId.Value, okay));
                        return;
                    }

                    break;
            }
        }
    }
}
