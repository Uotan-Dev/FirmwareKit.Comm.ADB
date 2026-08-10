using System.Buffers.Binary;
using System.Text;

namespace FirmwareKit.Comm.ADB.Services;

/// <summary>
/// Shell v2 protocol frame kinds (AOSP shell_v2_protocol.h).
/// <para>Shell v2 协议帧类型（AOSP shell_v2_protocol.h）。</para>
/// </summary>
public enum ShellFrameKind : byte
{
    /// <summary>STDIN data. 标准输入数据。</summary>
    Stdin = 0,
    /// <summary>STDOUT data. 标准输出数据。</summary>
    Stdout = 1,
    /// <summary>STDERR data. 标准错误数据。</summary>
    Stderr = 2,
    /// <summary>Exit request carrying the exit code. 携带退出码的退出请求。</summary>
    Exit = 3,
    /// <summary>Close STDIN. 关闭标准输入。</summary>
    CloseStdin = 4,
    /// <summary>Window size change. 窗口尺寸变化。</summary>
    WindowSizeChange = 5,
}

/// <summary>
/// A parsed shell v2 protocol frame.
/// <para>解析后的 shell v2 协议帧。</para>
/// </summary>
public readonly struct ShellFrame
{
    /// <summary>Gets the frame kind.
    /// <para>获取帧类型。</para></summary>
    public ShellFrameKind Kind { get; }

    /// <summary>Gets the frame payload.
    /// <para>获取帧负载。</para></summary>
    public byte[] Payload { get; }

    /// <summary>
    /// Initializes a new shell frame.
    /// <para>初始化新的 shell 帧。</para>
    /// </summary>
    /// <param name="kind">Frame kind. 帧类型。</param>
    /// <param name="payload">Frame payload. 帧负载。</param>
    public ShellFrame(ShellFrameKind kind, byte[] payload)
    {
        Kind = kind;
        Payload = payload;
    }
}

/// <summary>
/// Client for the ADB shell v2 protocol: executes commands on the device and
/// exposes stdout / stderr / exit code.
/// <para>ADB shell v2 协议客户端：在设备上执行命令并暴露 stdout / stderr / 退出码。</para>
/// </summary>
public sealed class AdbShellClient
{
    private const int FrameHeaderSize = 5; // kind(1) + length(4)

    private readonly global::FirmwareKit.Comm.ADB.AdbConnection _connection;
    private readonly string _command;
    private readonly string? _term;
    private readonly bool _pty;

    /// <summary>
    /// Initializes a new shell v2 client for the given command.
    /// <para>为给定命令初始化新的 shell v2 客户端。</para>
    /// </summary>
    /// <param name="connection">An established ADB connection. 已建立的 ADB 连接。</param>
    /// <param name="command">The shell command to execute. 待执行的 shell 命令。</param>
    /// <param name="term">Optional TERM value for a pty session. pty 会话的可选 TERM 值。</param>
    /// <param name="pty">Whether to allocate a pty. 是否分配伪终端。</param>
    public AdbShellClient(global::FirmwareKit.Comm.ADB.AdbConnection connection, string command, string? term = null, bool pty = false)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _term = term;
        _pty = pty;
    }

    private string BuildServiceString()
    {
        var sb = new StringBuilder("shell,v2");
        if (!string.IsNullOrEmpty(_term))
        {
            sb.Append(",TERM=").Append(_term);
        }

        if (_pty)
        {
            sb.Append(",pty:");
        }
        else
        {
            sb.Append(':');
        }

        sb.Append(_command);
        return sb.ToString();
    }

    /// <summary>Executes the command and returns captured stdout/stderr/exit code.
    /// 执行命令并返回捕获的输出、错误与退出码。</summary>
    public ShellResult Execute(int timeoutMs = 30000)
    {
        using var stream = _connection.OpenStream(BuildServiceString());

        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        int? exitCode = null;

        while (exitCode is null)
        {
            byte[]? chunk = stream.Read(timeoutMs);
            if (chunk is null)
            {
                if (!stream.IsClosed)
                {
                    throw new TimeoutException($"Shell command did not complete within {timeoutMs} ms.");
                }

                break;
            }

            foreach (ShellFrame frame in ParseFrames(chunk))
            {
                switch (frame.Kind)
                {
                    case ShellFrameKind.Stdout:
                        stdout.Write(frame.Payload, 0, frame.Payload.Length);
                        break;
                    case ShellFrameKind.Stderr:
                        stderr.Write(frame.Payload, 0, frame.Payload.Length);
                        break;
                    case ShellFrameKind.Exit:
                        exitCode = ReadExitCode(frame.Payload);
                        break;
                }
            }
        }

        return new ShellResult(
            stdout.ToArray(),
            stderr.ToArray(),
            exitCode);
    }

    /// <summary>Executes the command, streaming stdout/stderr chunks to the callbacks.
    /// 执行命令，将 stdout/stderr 数据块流式传递给回调。</summary>
    public int ExecuteStreaming(Action<byte[]>? onStdout, Action<byte[]>? onStderr, CancellationToken cancellationToken = default)
    {
        using var stream = _connection.OpenStream(BuildServiceString());
        int? exitCode = null;

        while (exitCode is null && !cancellationToken.IsCancellationRequested)
        {
            byte[]? chunk = stream.Read(250);
            if (chunk is null)
            {
                if (!stream.IsClosed)
                {
                    // Poll slice elapsed without data; re-check cancellation.
                    continue;
                }

                break;
            }

            foreach (ShellFrame frame in ParseFrames(chunk))
            {
                switch (frame.Kind)
                {
                    case ShellFrameKind.Stdout:
                        onStdout?.Invoke(frame.Payload);
                        break;
                    case ShellFrameKind.Stderr:
                        onStderr?.Invoke(frame.Payload);
                        break;
                    case ShellFrameKind.Exit:
                        exitCode = ReadExitCode(frame.Payload);
                        break;
                }
            }
        }

        return exitCode ?? 0;
    }

    /// <summary>Extracts the exit code from an Exit frame. Modern adbd sends one byte;
    /// legacy devices send a 32-bit little-endian value.
    /// <para>从 Exit 帧负载提取退出码。现代 adbd 发单字节；旧设备发 32 位小端值。</para></summary>
    private static int? ReadExitCode(byte[] payload)
    {
        if (payload is { Length: >= 4 })
        {
            return BitConverter.ToInt32(payload, 0);
        }

        if (payload is { Length: >= 1 })
        {
            return payload[0];
        }

        return null;
    }

    /// <summary>
    /// Runs an interactive shell: opens a shell v2 stream, sends STDIN frames as
    /// the user types (with the device pty handling echo/editing), relays STDOUT/
    /// STDERR frames to the console, sends the initial window-size frame, and
    /// returns the shell's exit code. Mirrors the reference client's interactive
    /// loop (AndroidDebugBridgeTransport.Shell).
    /// <para>运行交互式 shell：打开 shell v2 流，将用户按键以 STDIN 帧发送
    /// （回显与编辑由设备 pty 处理），把 STDOUT/STDERR 帧转发到控制台，
    /// 发送初始窗口尺寸帧，并返回 shell 退出码。与参考客户端的交互循环一致。</para>
    /// </summary>
    public int RunInteractive(CancellationToken cancellationToken = default)
    {
        using var stream = _connection.OpenStream(BuildServiceString());

        if (_pty && !Console.IsInputRedirected)
        {
            SendWindowSizeChange(stream, TryGetWindowHeight(), TryGetWindowWidth());
        }

        int? exitCode = null;
        bool stdinRedirected = Console.IsInputRedirected;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputThread = new Thread(() => PumpStdin(stream, stdinRedirected, cts.Token))
        {
            IsBackground = true,
            Name = "ADB interactive stdin",
        };

        // TreatControlCAsInput throws IOException when stdin is redirected (no console handle).
        bool treatCChanged = false;
        bool previousTreatControlCAsInput = false;
        try
        {
            if (!stdinRedirected)
            {
                try
                {
                    previousTreatControlCAsInput = Console.TreatControlCAsInput;
                    // Forward Ctrl+C to the remote shell as a byte instead of killing
                    // the local CLI; the pty passes it to the foreground process.
                    Console.TreatControlCAsInput = true;
                    treatCChanged = true;
                }
                catch (IOException)
                {
                    // No console handle; run without the Ctrl+C tweak.
                }
            }

            inputThread.Start();

            while (exitCode is null)
            {
                byte[]? chunk = stream.Read(200);
                if (chunk is null)
                {
                    if (stream.IsClosed || cts.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                foreach (ShellFrame frame in ParseFrames(chunk))
                {
                    switch (frame.Kind)
                    {
                        case ShellFrameKind.Stdout:
                            Console.OpenStandardOutput().Write(frame.Payload, 0, frame.Payload.Length);
                            break;
                        case ShellFrameKind.Stderr:
                            Console.OpenStandardError().Write(frame.Payload, 0, frame.Payload.Length);
                            break;
                        case ShellFrameKind.Exit:
                            exitCode = ReadExitCode(frame.Payload);
                            break;
                        case ShellFrameKind.CloseStdin:
                            cts.Cancel();
                            break;
                    }
                }
            }
        }
        finally
        {
            cts.Cancel();
            if (treatCChanged)
            {
                try { Console.TreatControlCAsInput = previousTreatControlCAsInput; }
                catch (IOException) { /* best effort */ }
            }
        }

        return exitCode ?? 0;
    }

    private static int TryGetWindowHeight()
    {
        try { return Console.WindowHeight; }
        catch (IOException) { return 24; }
    }

    private static int TryGetWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch (IOException) { return 80; }
    }

    private static void PumpStdin(AdbStream stream, bool stdinRedirected, CancellationToken token)
    {
        try
        {
            if (stdinRedirected)
            {
                var stdin = Console.OpenStandardInput();
                var buffer = new byte[4096];
                while (!token.IsCancellationRequested && !stream.IsClosed)
                {
                    var readTask = stdin.ReadAsync(buffer, 0, buffer.Length, token);
                    if (!readTask.Wait(150, token) && !token.IsCancellationRequested)
                    {
                        continue;
                    }

                    int n = readTask.Result;
                    if (n <= 0)
                    {
                        SendFrame(stream, ShellFrameKind.CloseStdin, Array.Empty<byte>());
                        break;
                    }

                    var data = new byte[n];
                    Buffer.BlockCopy(buffer, 0, data, 0, n);
                    SendFrame(stream, ShellFrameKind.Stdin, data);
                }
            }
            else
            {
                while (!token.IsCancellationRequested && !stream.IsClosed)
                {
                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(30);
                        continue;
                    }

                    ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                    byte[] bytes = EncodeKey(key);
                    if (bytes.Length > 0)
                    {
                        SendFrame(stream, ShellFrameKind.Stdin, bytes);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch
        {
            // The connection closed; stop pumping input.
        }
    }

    private static byte[] EncodeKey(ConsoleKeyInfo key)
    {
        // Map Ctrl+D/C/Z explicitly so behaviour is stable across consoles
        // (KeyChar already carries the control char on Windows).
        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            switch (key.Key)
            {
                case ConsoleKey.D:
                    return new byte[] { 0x04 };
                case ConsoleKey.C:
                    return new byte[] { 0x03 };
                case ConsoleKey.Z:
                    return new byte[] { 0x1A };
            }
        }

        if (key.KeyChar != '\0')
        {
            return Encoding.UTF8.GetBytes(new[] { key.KeyChar });
        }

        // Function / navigation keys have no KeyChar; map to ANSI escape sequences.
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: return new byte[] { 0x1B, (byte)'[', (byte)'A' };
            case ConsoleKey.DownArrow: return new byte[] { 0x1B, (byte)'[', (byte)'B' };
            case ConsoleKey.RightArrow: return new byte[] { 0x1B, (byte)'[', (byte)'C' };
            case ConsoleKey.LeftArrow: return new byte[] { 0x1B, (byte)'[', (byte)'D' };
            case ConsoleKey.Home: return new byte[] { 0x1B, (byte)'[', (byte)'H' };
            case ConsoleKey.End: return new byte[] { 0x1B, (byte)'[', (byte)'F' };
            case ConsoleKey.Delete: return new byte[] { 0x1B, (byte)'[', (byte)'3', (byte)'~' };
            case ConsoleKey.PageUp: return new byte[] { 0x1B, (byte)'[', (byte)'5', (byte)'~' };
            case ConsoleKey.PageDown: return new byte[] { 0x1B, (byte)'[', (byte)'6', (byte)'~' };
            case ConsoleKey.Insert: return new byte[] { 0x1B, (byte)'[', (byte)'2', (byte)'~' };
            default: return Array.Empty<byte>();
        }
    }

    private static void SendWindowSizeChange(AdbStream stream, int rows, int cols)
    {
        // AOSP shell v2 window-size payload: "HEIGHTxWIDTH,xpixelsxypixels\0".
        // <para>AOSP shell v2 窗口尺寸负载："HEIGHTxWIDTH,xpixelsxypixels\0"。</para>
        if (rows <= 0) rows = 24;
        if (cols <= 0) cols = 80;
        byte[] payload = Encoding.UTF8.GetBytes($"{rows}x{cols},0x0\0");
        SendFrame(stream, ShellFrameKind.WindowSizeChange, payload);
    }

    private static void SendFrame(AdbStream stream, ShellFrameKind kind, byte[] payload)
    {
        byte[] frame = new byte[FrameHeaderSize + payload.Length];
        frame[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, frame, FrameHeaderSize, payload.Length);
        stream.Write(frame);
    }

    /// <summary>
    /// Parses a raw WRTE payload into shell v2 frames.
    /// <para>将原始 WRTE 负载解析为 shell v2 帧。</para>
    /// </summary>
    public static List<ShellFrame> ParseFrames(byte[] data)
    {
        var frames = new List<ShellFrame>();
        int offset = 0;

        while (offset + FrameHeaderSize <= data.Length)
        {
            var kind = (ShellFrameKind)data[offset];
            int length = BitConverter.ToInt32(data, offset + 1);
            offset += FrameHeaderSize;

            if (length < 0 || offset + length > data.Length)
            {
                // Truncated frame; treat the remainder as stdout data.
                int remainderLength = data.Length - offset;
                byte[] remainder = new byte[remainderLength];
                Buffer.BlockCopy(data, offset, remainder, 0, remainderLength);
                frames.Add(new ShellFrame(ShellFrameKind.Stdout, remainder));
                break;
            }

            byte[] payload = new byte[length];
            Buffer.BlockCopy(data, offset, payload, 0, length);
            offset += length;
            frames.Add(new ShellFrame(kind, payload));
        }

        return frames;
    }
}

/// <summary>
/// Result of a shell command execution.
/// <para>shell 命令执行结果。</para>
/// </summary>
public readonly struct ShellResult
{
    /// <summary>
    /// Gets the captured standard output bytes.
    /// <para>获取捕获的标准输出字节。</para>
    /// </summary>
    public byte[] Stdout { get; }

    /// <summary>
    /// Gets the captured standard error bytes.
    /// <para>获取捕获的标准错误字节。</para>
    /// </summary>
    public byte[] Stderr { get; }

    /// <summary>
    /// Gets the exit code, or <c>null</c> when the command did not report one.
    /// <para>获取退出码；命令未报告退出码时为 <c>null</c>。</para>
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Initializes a new shell result.
    /// <para>初始化新的 shell 结果。</para>
    /// </summary>
    public ShellResult(byte[] stdout, byte[] stderr, int? exitCode)
    {
        Stdout = stdout;
        Stderr = stderr;
        ExitCode = exitCode;
    }
}
