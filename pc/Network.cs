using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LeanMic;

/// <summary>
/// Accepts one phone at a time over TCP and answers LAN discovery broadcasts.
/// Stream: "LMIC" | u32 rate | u16 channels | u8 nameLen | name | s16le PCM...
/// </summary>
sealed class StreamServer : IDisposable
{
    public const int TcpPort = 51235, UdpPort = 51234;

    readonly CancellationTokenSource cts = new();
    TcpListener? tcp;
    UdpClient? udp;
    TcpClient? current;

    /// <summary>(phone name, sample rate, Wi-Fi address or null for USB). Raised on a worker thread.</summary>
    public event Action<string, int, string?>? Connected;
    public event Action? Disconnected;
    public Action<byte[], int, int>? Audio;

    public void Start()
    {
        tcp = new TcpListener(IPAddress.Any, TcpPort);
        tcp.Start();
        _ = AcceptLoop();

        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpPort));
            _ = DiscoveryLoop();
        }
        catch (SocketException e) { Log.Write("Discovery disabled: " + e.Message); }
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await tcp!.AcceptTcpClientAsync(cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Handle(c));
        }
    }

    void Handle(TcpClient c)
    {
        bool active = false;
        try
        {
            c.NoDelay = true;
            var s = c.GetStream();
            s.ReadTimeout = 3000;

            var h = ReadExact(s, 11);
            if (Encoding.ASCII.GetString(h, 0, 4) != "LMIC") return;
            int rate = BitConverter.ToInt32(h, 4);
            int channels = BitConverter.ToUInt16(h, 8);
            string name = Encoding.UTF8.GetString(ReadExact(s, h[10]));
            if (channels != 1 || rate < 8000 || rate > 192000) return;

            // Valid phone: it replaces whichever phone was connected before (stray connections never do).
            Interlocked.Exchange(ref current, c)?.Dispose();
            active = true;

            var remote = ((IPEndPoint)c.Client.RemoteEndPoint!).Address;
            // USB traffic arrives through the local adb server, so it looks like loopback.
            string? wifiAddr = IPAddress.IsLoopback(remote) ? null : remote.MapToIPv4().ToString();
            Log.Write($"Phone connected: {name} {rate} Hz via {wifiAddr ?? "USB"}");
            Connected?.Invoke(name, rate, wifiAddr);

            // 'K' acknowledges the handshake; then one byte per second lets the phone notice a dead link fast.
            s.WriteByte((byte)'K');
            _ = Heartbeat(c, s);

            s.ReadTimeout = 5000;   // phone vanished (Wi-Fi drop) -> disconnect instead of hanging
            var buf = new byte[4096];
            int carry = 0;
            while (true)
            {
                int n = s.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                n += carry;
                int even = n & ~1;
                Audio?.Invoke(buf, 0, even);
                carry = n - even;
                if (carry == 1) buf[0] = buf[even];
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            c.Dispose();
            if (active && Interlocked.CompareExchange(ref current, null, c) == c)
            {
                Log.Write("Phone disconnected");
                Disconnected?.Invoke();
            }
        }
    }

    async Task Heartbeat(TcpClient c, NetworkStream s)
    {
        var beat = new[] { (byte)'H' };
        try
        {
            while (!cts.IsCancellationRequested && c.Connected)
            {
                await Task.Delay(1000, cts.Token);
                await s.WriteAsync(beat, cts.Token);
            }
        }
        catch { /* connection closed; the read loop handles cleanup */ }
    }

    static byte[] ReadExact(Stream s, int n)
    {
        var b = new byte[n];
        s.ReadExactly(b, 0, n);
        return b;
    }

    async Task DiscoveryLoop()
    {
        var reply = Encoding.UTF8.GetBytes("LEANMIC! " + Environment.MachineName);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var r = await udp!.ReceiveAsync(cts.Token);
                if (Encoding.ASCII.GetString(r.Buffer) == "LEANMIC?")
                    await udp.SendAsync(reply, r.RemoteEndPoint, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { }
        }
    }

    /// <summary>IPv4 addresses of real LAN adapters (ones with a gateway; skips Hyper-V/WSL/VPN virtual ones).</summary>
    public static List<string> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();

    public void Dispose()
    {
        cts.Cancel();
        tcp?.Stop();
        udp?.Dispose();
        current?.Dispose();
    }
}

/// <summary>
/// USB support: asks the *already running* adb server (over its socket on 5037) to add
/// "adb reverse tcp:51235 tcp:51235" for each connected phone.
///
/// It never runs an adb.exe of its own while a server is up, so it can't restart another
/// app's adb server (e.g. WO Mic's) and break its connection. Only when no server is running
/// at all does it start one from the Android SDK, if installed.
/// </summary>
sealed class AdbBridge : IDisposable
{
    readonly int port;
    System.Threading.Timer? timer;
    int busy;
    DateTime lastStartAttempt = DateTime.MinValue;

    public volatile string Status = "USB: checking…";

    public AdbBridge(int port) => this.port = port;

    public void Start() => timer = new System.Threading.Timer(_ => Tick(), null, 0, 3000);

    void Tick()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            List<string> serials;
            try { serials = Devices(); }
            // Only "connection refused" means no server. A slow/busy server must never trigger a start:
            // a different adb version would kill it (and another app's USB link with it).
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
            {
                var exe = FindAdb();
                if (exe == null) { Status = "USB: needs Android platform-tools (adb). Wi-Fi works without it."; return; }
                if (DateTime.Now - lastStartAttempt > TimeSpan.FromSeconds(30))
                {
                    lastStartAttempt = DateTime.Now;
                    Log.Write("No adb server running; starting " + exe);
                    Process.Start(new ProcessStartInfo(exe, "start-server") { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
                }
                Status = "USB: starting adb…";
                return;
            }

            if (serials.Count == 0) { Status = "USB: no phone with USB debugging connected"; return; }
            var ready = new List<string>();
            foreach (var s in serials)
            {
                try { EnsureReverse(s); ready.Add(s); }
                catch (Exception e) when (e is IOException or SocketException) { Log.Write($"adb reverse failed for {s}: {e.Message}"); }
            }
            Status = ready.Count > 0 ? "USB: ready (" + string.Join(", ", ready) + ")" : "USB: phone found but couldn't set up the link";
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { Status = "USB: adb is busy, retrying…"; }
        catch (Exception e) { Status = "USB: " + e.Message; }
        finally { Volatile.Write(ref busy, 0); }
    }

    void EnsureReverse(string serial)
    {
        string spec = $"tcp:{port}";
        using (var c = Open())
        {
            var s = c.GetStream();
            Send(s, "host:transport:" + serial); ExpectOkay(s);
            Send(s, "reverse:list-forward"); ExpectOkay(s);
            if (ReadLenString(s).Contains($"{spec} {spec}")) return;
        }
        using (var c = Open())
        {
            var s = c.GetStream();
            Send(s, "host:transport:" + serial); ExpectOkay(s);
            Send(s, $"reverse:forward:{spec};{spec}"); ExpectOkay(s);
        }
        Log.Write($"adb reverse {spec} added for {serial}");
    }

    static List<string> Devices()
    {
        using var c = Open();
        var s = c.GetStream();
        Send(s, "host:devices"); ExpectOkay(s);
        return ReadLenString(s)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length == 2 && p[1].Trim() == "device")
            .Select(p => p[0])
            .ToList();
    }

    static TcpClient Open()
    {
        var c = new TcpClient();
        try
        {
            if (!c.ConnectAsync(IPAddress.Loopback, 5037).Wait(2000)) throw new SocketException((int)SocketError.TimedOut);
        }
        catch (AggregateException e) when (e.InnerException is SocketException se) { c.Dispose(); throw se; }
        catch { c.Dispose(); throw; }
        c.ReceiveTimeout = c.SendTimeout = 3000;
        return c;
    }

    static void Send(NetworkStream s, string cmd) => s.Write(Encoding.ASCII.GetBytes(cmd.Length.ToString("x4") + cmd));

    static void ExpectOkay(NetworkStream s)
    {
        var st = ReadString(s, 4);
        if (st == "OKAY") return;
        throw new IOException(st == "FAIL" ? ReadLenString(s) : "unexpected adb reply " + st);
    }

    static string ReadLenString(NetworkStream s) => ReadString(s, Convert.ToInt32(ReadString(s, 4), 16));

    static string ReadString(NetworkStream s, int n)
    {
        var b = new byte[n];
        s.ReadExactly(b, 0, n);
        return Encoding.UTF8.GetString(b);
    }

    static string? FindAdb()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose() => timer?.Dispose();
}
