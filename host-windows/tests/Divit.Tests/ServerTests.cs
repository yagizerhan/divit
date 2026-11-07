// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// PenServer end to end: fake tablet (TCP client) ↔ server ↔ recording sink. Authentication: SPEC §3.2–3.3.

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using Divit.Core;
using Divit.Core.Server;
using Divit.Protocol;
using Xunit;

namespace Divit.Tests;

public sealed class ServerTests : IDisposable
{
    private sealed class Sink : IPenSink
    {
        public readonly ConcurrentQueue<PenTransition> Out = new();
        public bool Apply(in PenOutput o) { Out.Enqueue(o.Transition); return true; }
        public void Dispose() { }
        public string Trace => string.Join(" ", Out);
    }

    private sealed class FakeTablet : IDisposable
    {
        public readonly TcpClient Tcp = new() { NoDelay = true };
        public readonly byte[] DeviceId = Enumerable.Repeat((byte)7, 16).ToArray();
        private readonly NetworkStream _ns;
        private byte[] _clientNonce = [];
        public HelloAck? Ack;

        public FakeTablet(int port) { Tcp.Connect("127.0.0.1", port); _ns = Tcp.GetStream(); _ns.ReadTimeout = 3000; }

        public void Send(Message m) => _ns.Write(Codec.Encode(m));

        public HelloAck Hello()
        {
            _clientNonce = RandomNumberGenerator.GetBytes(16);
            Send(new Hello(0, TransportKind.Usb, Capabilities.Pressure | Capabilities.Hover, DeviceId, _clientNonce, "fake"));
            return Ack = Expect<HelloAck>();
        }

        /// <summary>Was the PC's proof made with this key? (The tablet sends nothing before checking this.)</summary>
        public bool HostProves(byte[] key) =>
            Auth.Equal(Ack!.HostProof, Auth.HostProof(key, DeviceId, Ack.HostId, _clientNonce, Ack.ServerNonce));

        /// <summary>Sends the tablet proof (key null: zero proof = "I have no key").</summary>
        public bool Authenticate(byte[]? key)
        {
            Send(new AuthRequest(key == null ? new byte[32] : Auth.TabletProof(key, DeviceId, Ack!.HostId, _clientNonce, Ack.ServerNonce)));
            return Expect<AuthResult>().Ok;
        }

        /// <summary>USB: HELLO + verify the PC + prove yourself.</summary>
        public void ConnectUsb(byte[] usbKey)
        {
            Assert.Equal(HelloResult.Authenticate, Hello().Result);
            Assert.True(HostProves(usbKey), "PC proof");
            Assert.True(Authenticate(usbKey), "tablet proof");
        }

        /// <summary>Wi-Fi pairing (ECDH). The derived key on success, otherwise null.</summary>
        public byte[]? Pair(uint code)
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            Send(new PairRequest(code, "tab", ecdh.ExportSubjectPublicKeyInfo()));
            var r = Expect<PairResult>();
            if (!r.Ok) return null;
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(r.HostKey, out _);
            return Auth.PairKey(ecdh.DeriveRawSecretAgreement(peer.PublicKey), DeviceId, Ack!.HostId);
        }

        /// <summary>Waits for the first message of the given type, answering the server's PINGs (heartbeat).</summary>
        public T Expect<T>() where T : Message
        {
            while (true)
            {
                var m = Read();
                if (m is Ping p) { Send(new Pong(p.Id, p.T0, 0, 0)); continue; }
                if (m is T t) return t;
                if (m is HostStatus or PairResult or HelloAck or AuthResult && typeof(T) != m.GetType()) continue;
                throw new Xunit.Sdk.XunitException($"expected {typeof(T).Name}, got {m}");
            }
        }

        public Message Read()
        {
            var h = new byte[Wire.HeaderSize];
            _ns.ReadExactly(h);
            var hdr = FrameHeader.Read(h);
            var p = new byte[hdr.Length];
            _ns.ReadExactly(p);
            return Codec.Decode(hdr.Type, p);
        }

        /// <summary>Reads until the connection closes; true if it closed.</summary>
        public bool WaitClosed(int ms)
        {
            _ns.ReadTimeout = ms;
            try { while (true) Read(); }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) { return false; }
            catch (Exception) { return true; }
        }

        public void Batch(params PenFlags[] flags)
        {
            var samples = flags.Select((f, i) => new PenSample(i, 0.5f, 0.5f, 0.3f, 0, 0, f, (ushort)i)).ToArray();
            Send(new PenBatchMessage(samples));
        }

        public void Dispose() => Tcp.Dispose();
    }

    private const PenFlags R = PenFlags.InRange, C = PenFlags.InRange | PenFlags.InContact;
    private readonly Sink _sink = new();
    private readonly string _pairPath = Path.Combine(Path.GetTempPath(), $"divit-test-{Guid.NewGuid():N}.json");
    private readonly HostIdentity _id = HostIdentity.Ephemeral();
    private PenServer? _server;

    static ServerTests() => Log.Enabled = false;

    private PenServer Server(bool wifi = false, string? code = null, int maxUnauthenticated = 4,
                             int handshakeMs = 10_000, int cooldownMs = 30_000)
    {
        _server = new PenServer(_sink, new PairingStore(_pairPath),
            aspect => new PipelineSettings(new AreaMapping(new ScreenRect(0, 0, 1920, 1080), NormRect.Full, aspect, true), new PressureCurve()), _id, code)
        {
            TreatLoopbackAsWifi = wifi,
            MaxUnauthenticated = maxUnauthenticated,
            HandshakeTimeout = TimeSpan.FromMilliseconds(handshakeMs),
            PairingCooldown = TimeSpan.FromMilliseconds(cooldownMs),
        };
        _server.Start(0, wifi);
        return _server;
    }

    private static void Eventually(Func<bool> cond, string what, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return; Thread.Sleep(10); }
        Assert.Fail($"timeout: {what}");
    }

    [Fact]
    public void UsbSessionInjectsAndReleasesOnDisconnect()
    {
        var srv = Server();
        using (var t = new FakeTablet(srv.Port))
        {
            t.ConnectUsb(_id.UsbKey);
            t.Expect<HostStatus>();
            t.Batch(R, C, C);
            Eventually(() => _sink.Out.Count >= 3, "enjeksiyon");
            Assert.Equal("Hover Down Move", _sink.Trace);
        } // cable pulled: the connection dropped during contact
        Eventually(() => _sink.Out.Count >= 5, "ReleaseAll");
        Assert.Equal("Hover Down Move Up Leave", _sink.Trace);
        Eventually(() => srv.Active == null, "session closed");
    }

    [Fact]
    public void UsbWithoutKeyIsRejectedAndKeyIsRequested()
    {
        // Another tablet app connecting through the adb reverse tunnel: it does not know the USB key.
        var srv = Server();
        int requested = 0;
        srv.UsbKeyRejected += () => Interlocked.Increment(ref requested);
        using var t = new FakeTablet(srv.Port);
        Assert.Equal(HelloResult.Authenticate, t.Hello().Result);
        Assert.False(t.Authenticate(null));
        Eventually(() => Volatile.Read(ref requested) == 1, "key resend request");   // the PC should resend the key over adb
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        Assert.False(t.HostProves(wrongKey));                           // the PC cannot be verified with a fake key
        t.Batch(R, C);                                                  // pen data before acceptance → disconnected
        Assert.True(t.WaitClosed(2000));
        Assert.Empty(_sink.Out);
    }

    [Fact]
    public void UsbRefusesPairing()
    {
        var srv = Server();
        using var t = new FakeTablet(srv.Port);
        t.Hello();
        t.Send(new PairRequest(uint.Parse(srv.PairingCode), "x", [1, 2, 3]));
        Assert.True(t.WaitClosed(2000));
        Assert.Null(srv.Active);
    }

    [Fact]
    public void SilentTabletIsDroppedAfterOneSecond()
    {
        var srv = Server();
        using var t = new FakeTablet(srv.Port);
        t.ConnectUsb(_id.UsbKey);
        t.Batch(R, C);
        // From now on we answer nothing (PINGs unanswered, no data) → must be considered dead after 1 s.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Eventually(() => _sink.Out.Count >= 4, "heartbeat ReleaseAll", 3000);
        Assert.InRange(sw.ElapsedMilliseconds, 700, 2500);
        Assert.Equal("Hover Down Up Leave", _sink.Trace);
    }

    [Fact]
    public void LiveTabletStaysConnected()
    {
        var srv = Server();
        using var t = new FakeTablet(srv.Port);
        t.ConnectUsb(_id.UsbKey);
        t.Send(new SurfaceInfo(2560, 1600, 266, 166));
        var end = DateTime.UtcNow.AddSeconds(2.5);
        uint id = 0;
        var reader = Task.Run(() => { try { while (true) t.Expect<Bye>(); } catch { } });
        while (DateTime.UtcNow < end)
        {
            t.Send(new Ping(++id, 0));
            Thread.Sleep(250);
        }
        Assert.NotNull(srv.Active);
    }

    [Fact]
    public void PenDataBeforeHelloIsRejected()
    {
        var srv = Server();
        using var t = new FakeTablet(srv.Port);
        t.Batch(C);
        Assert.True(t.WaitClosed(2000));
        Assert.Empty(_sink.Out);
    }

    [Fact]
    public void WifiPairingFlow()
    {
        var srv = Server(wifi: true);
        byte[] key;
        using (var t = new FakeTablet(srv.Port))
        {
            Assert.Equal(HelloResult.PairingRequired, t.Hello().Result);
            Assert.True(t.Ack!.HostProof.All(b => b == 0));            // no key, so no proof either
            t.Batch(R);                                                   // pen data without pairing → must disconnect
            Assert.True(t.WaitClosed(2000));
        }
        Assert.Empty(_sink.Out);

        using (var t = new FakeTablet(srv.Port))
        {
            t.Hello();
            uint wrong = (uint.Parse(srv.PairingCode) + 1) % 1_000_000;
            Assert.Null(t.Pair(wrong));
            string code = srv.PairingCode;
            key = t.Pair(uint.Parse(code)) ?? throw new Xunit.Sdk.XunitException("pairing failed");
            t.Expect<HostStatus>();
            Assert.Equal(code, srv.PairingCode);                          // the code is fixed: it does not change after use
        }

        // Reconnect: the PC proves itself with the same key derived through ECDH; the key never went on the wire.
        using (var t = new FakeTablet(srv.Port))
        {
            Assert.Equal(HelloResult.Authenticate, t.Hello().Result);
            Assert.True(t.HostProves(key));
            Assert.True(t.Authenticate(key));
        }
        // Wrong key: rejected.
        using (var t = new FakeTablet(srv.Port))
        {
            t.Hello();
            var bad = (byte[])key.Clone(); bad[0] ^= 1;
            Assert.False(t.Authenticate(bad));
            Assert.Null(srv.Active);
        }
    }

    [Fact]
    public void PendingSessionCannotPairAfterWifiIsDisabled()
    {
        var srv = Server(wifi: true);
        using var t = new FakeTablet(srv.Port);
        Assert.Equal(HelloResult.PairingRequired, t.Hello().Result);
        string code = srv.PairingCode;
        srv.Start(0, wifi: false);                                        // the user turned Wi-Fi off
        Assert.Null(t.Pair(uint.Parse(code)));                            // a connection left open cannot pair
        Assert.Null(srv.Active);
    }

    [Fact]
    public void DiscoveryAnswersOnlyWhenWifiEnabled()
    {
        using var udp = new UdpClient(0);
        udp.Client.ReceiveTimeout = 1000;
        byte[] req = System.Text.Encoding.ASCII.GetBytes(DiscoveryResponder.Request);

        _server = new PenServer(_sink, new PairingStore(_pairPath),
            a => new PipelineSettings(new AreaMapping(new ScreenRect(0, 0, 1920, 1080), NormRect.Full, a, true), new PressureCurve()), _id)
        { DiscoveryPort = 0 };
        _server.Start(0, wifi: true);
        int dport = _server.ActiveDiscoveryPort;
        Assert.NotEqual(0, dport);

        udp.Send(System.Text.Encoding.ASCII.GetBytes("baska bir sey"), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, dport));
        udp.Send(req, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, dport));
        var from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        string reply = System.Text.Encoding.UTF8.GetString(udp.Receive(ref from));
        Assert.Equal($"DIVIT-HERE 2 {_server.Port} {Convert.ToHexString(_id.HostId).ToLowerInvariant()} {Environment.MachineName}", reply);

        _server.Start(0, wifi: false);   // Wi-Fi off: the responder stops
        Assert.Equal(0, _server.ActiveDiscoveryPort);
    }

    [Fact]
    public void ThreeWrongCodesCloseConnection()
    {
        var srv = Server(wifi: true);
        using var t = new FakeTablet(srv.Port);
        t.Hello();
        uint wrong = (uint.Parse(srv.PairingCode) + 1) % 1_000_000;
        for (int i = 0; i < 3; i++) Assert.Null(t.Pair(wrong));
        Assert.True(t.WaitClosed(2000));
    }

    [Fact]
    public void SecondTabletReplacesFirst()
    {
        var srv = Server();
        using var a = new FakeTablet(srv.Port);
        a.ConnectUsb(_id.UsbKey); a.Batch(R, C);
        Eventually(() => _sink.Out.Count >= 2, "a enjeksiyon");

        using var b = new FakeTablet(srv.Port);
        b.ConnectUsb(_id.UsbKey);
        Assert.Equal(ByeReason.Replaced, a.Expect<Bye>().Reason);
        // a's pen must have been released: Hover Down Up Leave
        Eventually(() => _sink.Out.Count >= 4, "a ReleaseAll");
        Assert.Equal("Hover Down Up Leave", _sink.Trace);
    }

    [Fact]
    public void SavedCodeIsUsedAndOnlyValidCodesCanBeSet()
    {
        var srv = Server(wifi: true, code: "042137");
        Assert.Equal("042137", srv.PairingCode);
        string? saved = null;
        srv.PairingCodeChanged += c => saved = c;
        Assert.False(srv.SetPairingCode("12345"));
        Assert.False(srv.SetPairingCode("12a456"));
        Assert.Equal("042137", srv.PairingCode);
        Assert.True(srv.SetPairingCode("777000"));
        Assert.Equal("777000", saved);
        srv.NewPairingCode();
        Assert.True(PenServer.IsValidCode(srv.PairingCode));
        Assert.Equal(srv.PairingCode, saved);
    }

    [Fact]
    public void TenWrongCodesPausePairingThenItOpensAgain()
    {
        var srv = Server(wifi: true, code: "123456", cooldownMs: 600);
        int wrongLeft = 10;
        FakeTablet? t = null;
        while (wrongLeft > 0)
        {
            t?.Dispose();
            t = new FakeTablet(srv.Port);
            t.Hello();
            for (int i = 0; i < 3 && wrongLeft > 0; i++, wrongLeft--) Assert.Null(t.Pair(654321));
            if (wrongLeft > 0) Assert.True(t.WaitClosed(2000));   // 3 wrong codes per connection
        }
        Assert.True(srv.PairingLocked);
        Assert.Null(t!.Pair(123456));                             // even the right code waits during the pause
        Thread.Sleep(800);
        Assert.False(srv.PairingLocked);
        Assert.NotNull(t.Pair(123456));                           // same code, no new code needed
        Assert.Equal("123456", srv.PairingCode);
        t.Dispose();
    }

    [Fact]
    public void UnauthenticatedConnectionsAreLimited()
    {
        var srv = Server(wifi: true, maxUnauthenticated: 2);
        using var a = new FakeTablet(srv.Port);
        using var b = new FakeTablet(srv.Port);
        a.Hello(); b.Hello();
        using var c = new FakeTablet(srv.Port);
        Assert.True(c.WaitClosed(2000));                          // closed at once, no thread for it

        // A connection that authenticates frees its slot.
        a.Dispose();
        Thread.Sleep(200);
        using var d = new FakeTablet(srv.Port);
        Assert.Equal(HelloResult.PairingRequired, d.Hello().Result);
    }

    [Fact]
    public void HandshakeHasADeadlineEvenIfPingsKeepComing()
    {
        var srv = Server(handshakeMs: 500);
        using var t = new FakeTablet(srv.Port);
        Assert.Equal(HelloResult.Authenticate, t.Hello().Result);
        // Never authenticate, but keep the heartbeat alive.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool closed = false;
        uint id = 0;
        while (!closed && sw.ElapsedMilliseconds < 3000)
        {
            try { t.Send(new Ping(++id, 0)); } catch { closed = true; break; }
            closed = t.WaitClosed(200);
        }
        Assert.True(closed, "still open after 3 s");
        Assert.InRange(sw.ElapsedMilliseconds, 300, 2500);
        Assert.Null(srv.Active);
    }

    [Fact]
    public void ForgetPairingsDisconnectsTheWifiTablet()
    {
        var srv = Server(wifi: true);
        using var t = new FakeTablet(srv.Port);
        t.Hello();
        Assert.NotNull(t.Pair(uint.Parse(srv.PairingCode)));
        t.Expect<HostStatus>();
        Eventually(() => srv.Active != null, "accepted");
        srv.ForgetPairings();
        Assert.Equal(ByeReason.Normal, t.Expect<Bye>().Reason);
        Eventually(() => srv.Active == null, "session closed");
    }

    [Fact]
    public void TurningWifiOnOrOffKeepsTheUsbSession()
    {
        var srv = Server();
        using var t = new FakeTablet(srv.Port);
        t.ConnectUsb(_id.UsbKey);
        Eventually(() => srv.Active != null, "accepted");
        var usb = srv.Active;
        int gotBye = 0;
        _ = Task.Run(() => { try { t.Expect<Bye>(); Volatile.Write(ref gotBye, 1); } catch { } });
        srv.Start(0, wifi: false);           // restart the listener (the Wi-Fi setting was toggled)
        for (int i = 0; i < 6; i++) { t.Send(new Ping((uint)i, 0)); Thread.Sleep(150); }
        Assert.Same(usb, srv.Active);
        Assert.Equal(0, Volatile.Read(ref gotBye));   // the USB tablet got no BYE
    }

    [Fact]
    public void SurfaceInfoIsReported()
    {
        var srv = Server();
        float w = 0, h = 0;
        srv.SurfaceChanged += (a, b) => { w = a; h = b; };
        using var t = new FakeTablet(srv.Port);
        t.ConnectUsb(_id.UsbKey);
        t.Send(new SurfaceInfo(2560, 1600, 266, 166));
        Eventually(() => w == 266 && h == 166, "surface event");
    }

    public void Dispose()
    {
        _server?.Dispose();
        try { File.Delete(_pairPath); } catch { }
    }
}
