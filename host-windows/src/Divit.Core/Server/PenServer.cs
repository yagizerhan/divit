// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// TCP server and tablet session (protocol/SPEC.md §3–4, architecture §3.1, §5).
// Hot path: the session's receive thread reads a frame → PenBatch.Read → PenPipeline.Process → injection.
// No queue and no thread hop in between.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Divit.Protocol;

namespace Divit.Core.Server;

public sealed record SessionInfo(string Model, TransportKind Transport, string Remote, DateTime Since);

internal enum PairOutcome { Ok, WrongCode, Paused, WifiOff }

public sealed class PenServer : IDisposable
{
    private readonly IPenSink _sink;
    private readonly PairingStore _pairing;
    private readonly object _gate = new();
    private volatile Socket? _listener;
    private DiscoveryResponder? _discovery;
    private Session? _active;
    /// <summary>Every session whose receive thread is running (accepted or not); Stop() waits for them.</summary>
    private readonly HashSet<Session> _sessions = new();
    // Written by the UI thread, read by the session threads: a reference swap of immutable values.
    private volatile Func<float, PipelineSettings> _settingsFactory;
    private volatile string _monitorName = "";
    private int _pairFailures;
    /// <summary>Pauses in a row without a successful pairing: each one lasts twice as long (capped).</summary>
    private int _pairLockouts;
    private long _pairBlockedUntilMs;
    // Separate budgets: connections waiting on Wi-Fi must never use up the slots of the USB tablet (and the other way round).
    private int _unauthenticatedUsb, _unauthenticatedWifi;

    /// <param name="pairingCode">The saved pairing code (6 digits); null or invalid = create a new one (see <see cref="PairingCodeChanged"/>).</param>
    public PenServer(IPenSink sink, PairingStore pairing, Func<float, PipelineSettings> settingsFactory, HostIdentity? identity = null, string? pairingCode = null)
    {
        _sink = sink; _pairing = pairing; _settingsFactory = settingsFactory;
        Identity = identity ?? HostIdentity.Ephemeral();
        if (IsValidCode(pairingCode)) PairingCode = pairingCode!;
        else NewPairingCode();
    }

    public event Action? StateChanged;
    /// <summary>The pairing code changed (created or set by the user): the platform saves it.</summary>
    public event Action<string>? PairingCodeChanged;
    /// <summary>The tablet reported its drawing surface (mm), e.g. for the WinTab tablet resolution.</summary>
    public event Action<float, float>? SurfaceChanged;
    internal void OnSurface(float wMm, float hMm) => SurfaceChanged?.Invoke(wMm, hMm);
    /// <summary>On USB the tablet could not give a valid proof (no key / an old one): the platform should resend the key over adb.</summary>
    public event Action? UsbKeyRejected;
    internal void OnUsbKeyRejected() => UsbKeyRejected?.Invoke();

    /// <summary>PC identity and USB key (SPEC §3.2).</summary>
    public HostIdentity Identity { get; }

    public int Port { get; private set; }
    public bool WifiEnabled { get; private set; }
    /// <summary>Tests only: when true, 127.0.0.1 connections are treated like Wi-Fi and need pairing.</summary>
    public bool TreatLoopbackAsWifi { get; init; }
    /// <summary>Discovery UDP port (SPEC §7). 0 = random (tests). Runs while Wi-Fi is allowed.</summary>
    public int DiscoveryPort { get; init; } = DiscoveryResponder.DefaultPort;
    /// <summary>Side button actions (right click, shortcut); provided by the platform.</summary>
    public IButtonActions? Actions { get; init; }
    /// <summary>Actual port of the running discovery responder; 0 if Wi-Fi is off.</summary>
    public int ActiveDiscoveryPort => _discovery?.Port ?? 0;
    /// <summary>The fixed pairing code (SPEC §3.3). It stays the same until the user changes it.</summary>
    public string PairingCode { get; private set; } = "";
    /// <summary>After 10 wrong codes pairing pauses (against guessing), then opens again. The first pause lasts
    /// <see cref="PairingCooldown"/>; each further one without a successful pairing in between lasts twice as long, up to
    /// <see cref="MaxPairingCooldown"/>. A successful pairing or a new code starts over.</summary>
    public bool PairingLocked => Environment.TickCount64 < Interlocked.Read(ref _pairBlockedUntilMs);
    public TimeSpan PairingCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxPairingCooldown { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>At most this many connections per transport (USB, Wi-Fi) may be open before authentication; extra ones are closed at once.</summary>
    public int MaxUnauthenticated { get; init; } = 4;
    /// <summary>Time to authenticate after connecting. While the user types the pairing code: <see cref="PairingTimeout"/>.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>The tablet waits 2 minutes for the user to enter the code (TransportClient); a little longer here.</summary>
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromSeconds(150);
    public Session? Active { get { lock (_gate) return _active; } }
    /// <summary>The Wi-Fi session waiting for a pairing code (or null), so the UI can show "enter the code".</summary>
    public Session? Pending { get; private set; }

    internal void SetPending(Session? s, bool clearOnlyIf = false)
    {
        if (clearOnlyIf) { if (Pending == s) Pending = null; } else Pending = s;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// (Re)starts listening. A running USB session is kept (turning Wi-Fi on or off must not drop the pen);
    /// a Wi-Fi session is closed when Wi-Fi is turned off.
    /// </summary>
    public void Start(int port, bool wifi)
    {
        StopListening();
        Port = port; WifiEnabled = wifi;
        if (!wifi && Active is { IsUsb: false } w) w.Stop(ByeReason.Normal);
        var ep = new IPEndPoint(wifi ? IPAddress.Any : IPAddress.Loopback, port);
        var l = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        l.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        l.Bind(ep);
        l.Listen(4);
        Port = ((IPEndPoint)l.LocalEndPoint!).Port;
        _listener = l;
        Log.Info($"listening: {ep} ({(wifi ? "USB + Wi-Fi" : "USB only")})");
        new Thread(() => AcceptLoop(l)) { IsBackground = true, Name = "accept" }.Start();
        if (wifi)
        {
            try { _discovery = new DiscoveryResponder(DiscoveryPort, Port, Environment.MachineName, Identity.HostId); }
            catch (Exception e) { Log.Info($"could not start discovery (the address can be entered manually): {e.Message}"); }
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Stops listening and closes every session, waiting for their receive threads. After this returns no session
    /// thread touches the sink any more, so the platform may dispose it (program exit).
    /// </summary>
    public void Stop()
    {
        StopListening();
        Session[] all;
        lock (_gate) all = _sessions.ToArray();
        // In parallel: each one may wait up to 2 s for a peer that does not close.
        Task.WaitAll(all.Select(s => Task.Run(() => s.Stop(ByeReason.Normal, join: true))).ToArray());
    }

    private void StopListening()
    {
        var l = _listener;
        _listener = null;   // first, so the accept loop knows the close below is on purpose
        l?.Close();
        _discovery?.Dispose();
        _discovery = null;
    }

    private void AcceptLoop(Socket l)
    {
        int failures = 0;
        while (true)
        {
            Socket s;
            try { s = l.Accept(); failures = 0; }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (!ReferenceEquals(_listener, l)) return;   // Stop()/Start() closed this listener
                // A transient error (e.g. the client reset the connection before it was accepted) must not end the
                // loop: the server would look alive but accept nothing until Divit is restarted.
                if (++failures <= 5) Log.Info($"accept failed, retrying: {e.Message}");
                Thread.Sleep(Math.Min(50 * failures, 1000));
                continue;
            }
            try { Admit(s); }
            catch (Exception e)
            {
                // The client may already be gone (RemoteEndPoint throws on a reset socket): drop it, keep accepting.
                Log.Info($"could not start a session: {e.Message}");
                try { s.Close(); } catch { }
            }
        }
    }

    private void Admit(Socket s)
    {
        bool usb = IsUsbEndpoint(s);
        ref int counter = ref usb ? ref _unauthenticatedUsb : ref _unauthenticatedWifi;
        // No thread for connections beyond the limit (a stuck or misbehaving client cannot pile up threads).
        if (Interlocked.Increment(ref counter) > MaxUnauthenticated)
        {
            Interlocked.Decrement(ref counter);
            Log.Info($"connection refused (too many unauthenticated {(usb ? "USB" : "Wi-Fi")}): {s.RemoteEndPoint}");
            s.Close();
            return;
        }
        Session session;
        try { session = new Session(this, s, usb); }
        catch { Interlocked.Decrement(ref counter); throw; }
        lock (_gate) _sessions.Add(session);
        session.Start();
    }

    /// <summary>A loopback connection is the USB tablet (adb reverse); everything else came over the network.</summary>
    private bool IsUsbEndpoint(Socket s) => !TreatLoopbackAsWifi && s.RemoteEndPoint is IPEndPoint ep && IPAddress.IsLoopback(ep.Address);

    internal void AuthenticationDone(bool usb)
    {
        if (usb) Interlocked.Decrement(ref _unauthenticatedUsb);
        else Interlocked.Decrement(ref _unauthenticatedWifi);
    }

    public static bool IsValidCode(string? code) => code is { Length: 6 } && code.All(char.IsAsciiDigit);

    /// <summary>Creates a random new code (the user pressed "New code", or there was no saved code).</summary>
    public void NewPairingCode() => SetPairingCode(RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"));

    /// <summary>Sets the user's own code (6 digits). False if invalid.</summary>
    public bool SetPairingCode(string code)
    {
        if (!IsValidCode(code)) return false;
        lock (_gate)
        {
            PairingCode = code;
            _pairFailures = 0;
            _pairLockouts = 0;
            Interlocked.Exchange(ref _pairBlockedUntilMs, 0);
        }
        PairingCodeChanged?.Invoke(code);
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Forget all paired tablets. A connected Wi-Fi tablet is disconnected too (it has to pair again).</summary>
    public void ForgetPairings()
    {
        _pairing.Clear();
        if (Active is { IsUsb: false } w) w.Stop(ByeReason.Normal);
        Log.Info("pairings cleared");
        StateChanged?.Invoke();
    }

    /// <summary>When the monitor / area / curve changes: push the new settings and HOST_STATUS to the active session.</summary>
    public void ApplySettings(Func<float, PipelineSettings> factory, string monitorName)
    {
        _settingsFactory = factory; _monitorName = monitorName;
        Active?.OnSettingsChanged();
    }

    internal IPenSink Sink => _sink;
    internal PairingStore Pairing => _pairing;
    internal PipelineSettings MakeSettings(float surfaceAspect) => _settingsFactory(surfaceAspect);
    internal string MonitorName => _monitorName;

    /// <summary>Is the code correct? Always refused while Wi-Fi is off (including connections opened earlier). May be called by several sessions at once.</summary>
    internal PairOutcome TryPair(uint code)
    {
        TimeSpan pause = default;
        lock (_gate)
        {
            if (!WifiEnabled) return PairOutcome.WifiOff;
            if (PairingLocked) return PairOutcome.Paused;
            if (code.ToString("D6") == PairingCode) { _pairFailures = 0; _pairLockouts = 0; return PairOutcome.Ok; }   // the code stays the same
            if (++_pairFailures >= 10)
            {
                _pairFailures = 0;
                // 30 s, 60 s, 2 min, … up to MaxPairingCooldown: guessing all codes would take years instead of weeks.
                double ms = PairingCooldown.TotalMilliseconds * Math.Pow(2, Math.Min(_pairLockouts, 20));
                pause = TimeSpan.FromMilliseconds(Math.Min(ms, Math.Max(MaxPairingCooldown.TotalMilliseconds, PairingCooldown.TotalMilliseconds)));
                _pairLockouts++;
                Interlocked.Exchange(ref _pairBlockedUntilMs, Environment.TickCount64 + (long)pause.TotalMilliseconds);
            }
        }
        if (pause > TimeSpan.Zero) { Log.Info($"pairing paused for {pause.TotalSeconds:F0} s: 10 wrong codes"); StateChanged?.Invoke(); }
        return PairOutcome.WrongCode;
    }

    /// <summary>The accepted session becomes active; the previous one is stopped and its thread awaited
    /// (two threads must not write to the same synthetic pen device at once).</summary>
    internal void Activate(Session s)
    {
        Session? old;
        lock (_gate) { old = _active; _active = s; }
        if (old != null && old != s) old.Stop(ByeReason.Replaced, join: true);
        StateChanged?.Invoke();
    }

    internal void Ended(Session s)
    {
        lock (_gate)
        {
            if (_active == s) _active = null;
            _sessions.Remove(s);
        }
        StateChanged?.Invoke();
    }

    public void Dispose() => Stop();
}

public sealed class Session
{
    private const int HeartbeatMs = 250, DeadAfterMs = 1000;
    private static readonly bool Trace = Environment.GetEnvironmentVariable("DIVIT_TRACE") == "1";

    private readonly PenServer _server;
    private readonly Socket _s;
    private readonly bool _isUsb;
    private readonly object _sendLock = new();
    private readonly byte[] _buf = new byte[Wire.HeaderSize + Wire.MaxPayload];
    private readonly PenSample[] _samples = new PenSample[Wire.MaxBatch];
    private Thread? _rx;
    private Timer? _pinger;
    private uint _pingId;
    private long _lastPingT0;
    private bool _accepted;
    private int _pairAttempts, _authFailures;
    private Hello? _hello;
    private bool _countedUnauthenticated = true;   // counted by the accept loop until accepted or closed
    private long _deadlineMs;                       // must be accepted by then (Environment.TickCount64)
    private byte[] _serverNonce = [];
    /// <summary>This session's key: the PC's USB key on USB, the device's pairing key on Wi-Fi (or null).</summary>
    private byte[]? _key;
    private float _surfaceAspect = 16f / 10;  // Galaxy Tab S9 FE+ assumption until SURFACE_INFO arrives

    public ClockSync Clock { get; } = new();
    public PenPipeline? Pipeline { get; private set; }
    public SessionInfo? Info { get; private set; }
    public bool IsUsb => _isUsb;

    internal Session(PenServer server, Socket s, bool usb)
    {
        _server = server; _s = s;
        _s.NoDelay = true;
        _s.ReceiveTimeout = DeadAfterMs;  // no frame for 1 s = connection dead (SPEC §3.4)
        _s.SendTimeout = DeadAfterMs;
        _isUsb = usb;
        _deadlineMs = Environment.TickCount64 + (long)server.HandshakeTimeout.TotalMilliseconds;
    }

    internal void Start()
    {
        // Normal priority until accepted; the hot path gets Highest in Accept().
        _rx = new Thread(ReceiveLoop) { IsBackground = true, Name = "pen-rx" };
        _rx.Start();
    }

    /// <summary>Waiting for the user to type the pairing code: allow more time than the normal handshake.</summary>
    private void WaitForPairing()
    {
        _deadlineMs = Environment.TickCount64 + (long)_server.PairingTimeout.TotalMilliseconds;
        _server.SetPending(this);
    }

    private void Uncount()
    {
        if (!_countedUnauthenticated) return;
        _countedUnauthenticated = false;
        _server.AuthenticationDone(_isUsb);
    }

    /// <summary>
    /// Graceful close: BYE + shut down the send direction, wait for the peer to close. An immediate Close() makes Windows
    /// send RST and the connection drops before the tablet can read the BYE in its buffer.
    /// Called from another thread without <paramref name="join"/> (the UI: Wi-Fi off, forget pairings) it runs in the
    /// background, so a stalled network can never freeze the window for the 1 s send timeout. On the session's own
    /// receive thread it stays synchronous: nothing more may be sent or accepted after the BYE.
    /// </summary>
    internal void Stop(ByeReason reason, bool join = false)
    {
        if (!join && Thread.CurrentThread != _rx) { Task.Run(() => StopNow(reason, false)); return; }
        StopNow(reason, join);
    }

    private void StopNow(ByeReason reason, bool join)
    {
        try { Send(new Bye(reason)); } catch { }
        try { _s.Shutdown(SocketShutdown.Send); } catch { }
        if (join && _rx != null && _rx != Thread.CurrentThread)
        {
            // The tablet closes on BYE; if not, the heartbeat (1 s) drops it anyway. Still wait at most 1.5 s.
            if (!_rx.Join(1500)) { _s.Close(); _rx.Join(500); }
        }
        else _ = Task.Delay(500).ContinueWith(_ => _s.Close());
    }

    private void ReceiveLoop()
    {
        string remote = _s.RemoteEndPoint?.ToString() ?? "?";
        Log.Info($"connection: {remote}");
        try
        {
            while (true)
            {
                ReadExactly(_buf.AsSpan(0, Wire.HeaderSize));
                var h = FrameHeader.Read(_buf);
                var payload = _buf.AsSpan(Wire.HeaderSize, h.Length);
                ReadExactly(payload);
                if (Trace) Log.Info($"<- {h.Type} {h.Length} B");

                if (h.Type == MessageType.PenBatch)
                {
                    if (!_accepted) throw new ProtocolException(ProtocolError.Range, "pen data before acceptance");
                    int n = PenBatch.Read(payload, _samples);
                    long? off = Clock.OffsetUs;
                    var p = Pipeline!;
                    for (int i = 0; i < n; i++) p.Process(_samples[i], MonoClock.NowUs(), off);
                }
                else HandleControl(Codec.Decode(h.Type, payload), MonoClock.NowUs());
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException or ProtocolException)
        {
            string why = e switch
            {
                SocketException { SocketErrorCode: SocketError.TimedOut } => $"no response for {DeadAfterMs} ms",
                ProtocolException pe => $"protocol error ({pe.Error}): {pe.Message}",
                _ => e.Message,
            };
            Log.Info($"connection closed: {remote} — {why}");
            if (e is ProtocolException) try { Send(new Bye(ByeReason.ProtocolError)); } catch { }
        }
        catch (Exception e)
        {
            // A bug or an input nobody thought of must cost this connection only. Unhandled, it would end the whole
            // process (the tray app disappears) with no chance to release the pen.
            Log.Info($"connection closed: {remote} — unexpected error: {e}");
            try { Send(new Bye(ByeReason.ProtocolError)); } catch { }
        }
        finally
        {
            _pinger?.Dispose();
            try { Pipeline?.ReleaseAll(); }   // CN-03: the pen never stays pressed
            catch (Exception e) { Log.Info($"releasing the pen failed: {e.Message}"); }
            if (Pipeline != null)
            {
                var pl = Pipeline;
                static string Pct(long a, long b) => b == 0 ? "—" : $"{100.0 * a / b:F0}%";
                Log.Info($"batching: hover {pl.HoverSamples} samples, batched {Pct(pl.HoverHistorical, pl.HoverSamples)} · contact {pl.ContactSamples} samples, batched {Pct(pl.ContactHistorical, pl.ContactSamples)}");
            }
            if (Pipeline != null)
                Log.Info($"session summary: {Pipeline.Samples} samples, {Pipeline.SeqGaps} sequence gaps, {Pipeline.InjectFailures} injection failures, " +
                         $"latency p50/p95/p99 = {Pipeline.Latency.Percentile(50) / 1000.0:F1}/{Pipeline.Latency.Percentile(95) / 1000.0:F1}/{Pipeline.Latency.Percentile(99) / 1000.0:F1} ms");
            _s.Close();
            Uncount();
            _server.SetPending(this, clearOnlyIf: true);
            _server.Ended(this);
        }
    }

    private void HandleControl(Message m, long nowUs)
    {
        switch (m)
        {
            case Hello h when _hello == null:
                _hello = h;
                Info = new SessionInfo(h.Model, _isUsb ? TransportKind.Usb : TransportKind.Wifi, _s.RemoteEndPoint?.ToString() ?? "?", DateTime.Now);
                StartHeartbeat();
                if (!_isUsb && !_server.WifiEnabled) throw new ProtocolException(ProtocolError.Range, "Wi-Fi is off");
                _serverNonce = Auth.Random(Auth.NonceSize);
                _key = _isUsb ? _server.Identity.UsbKey : _server.Pairing.KeyFor(h.DeviceId);
                if (_key != null)
                {
                    // The PC proves itself first, so a fake PC learns nothing from the tablet (SPEC §3.2).
                    Send(new HelloAck(HelloResult.Authenticate, Wire.Minor, Environment.MachineName, _server.Identity.HostId, _serverNonce,
                        Auth.HostProof(_key, h.DeviceId, _server.Identity.HostId, h.ClientNonce, _serverNonce)));
                }
                else
                {
                    Send(new HelloAck(HelloResult.PairingRequired, Wire.Minor, Environment.MachineName, _server.Identity.HostId, _serverNonce, new byte[Auth.ProofSize]));
                    WaitForPairing();
                    Log.Info($"waiting for pairing: {h.Model}");
                }
                break;

            case Hello:
                throw new ProtocolException(ProtocolError.Range, "second HELLO");

            case var _ when _hello == null:
                throw new ProtocolException(ProtocolError.Range, "first message is not HELLO");

            case AuthRequest a when !_accepted:
                {
                    if (!_isUsb && !_server.WifiEnabled) throw new ProtocolException(ProtocolError.Range, "Wi-Fi is off");
                    bool ok = _key != null && Auth.Equal(a.Proof,
                        Auth.TabletProof(_key, _hello.DeviceId, _server.Identity.HostId, _hello.ClientNonce, _serverNonce));
                    Send(new AuthResult(ok));
                    if (ok) { Accept(); break; }
                    if (++_authFailures >= 3) { Stop(ByeReason.PairingFailed); break; }
                    if (_isUsb)
                    {
                        // The tablet has no USB key or an old one (e.g. the app was reinstalled): resend it over adb.
                        Log.Info($"USB authentication failed: {_hello.Model} — resending the key");
                        _server.OnUsbKeyRejected();
                    }
                    else
                    {
                        Log.Info($"authentication failed, waiting for pairing: {_hello.Model}");
                        WaitForPairing();
                    }
                    break;
                }

            case PairRequest pr when !_accepted:
                if (_isUsb) throw new ProtocolException(ProtocolError.Range, "no pairing over USB (the key comes via adb)");
                {
                    // Check the key before the code: a malformed key must not count as a guess or reach ECDH.
                    Auth.CheckTabletKey(pr.TabletKey);
                    var outcome = _server.TryPair(pr.Code);
                    if (outcome == PairOutcome.Ok)
                    {
                        var (hostKey, pairKey) = Auth.PairAsHost(pr.TabletKey, _hello.DeviceId, _server.Identity.HostId);
                        _server.Pairing.Add(_hello.DeviceId, pr.DeviceName, pairKey);
                        Send(new PairResult(true, hostKey));
                        Log.Info($"paired: {pr.DeviceName}");
                        Accept();
                    }
                    else
                    {
                        // The reason lets the tablet say "paused, wait" instead of "wrong code" when the code was right.
                        Send(new PairResult(false, [], outcome switch
                        {
                            PairOutcome.Paused => PairFailure.Paused,
                            PairOutcome.WifiOff => PairFailure.WifiOff,
                            _ => PairFailure.WrongCode,
                        }));
                        if (++_pairAttempts >= 3) { Stop(ByeReason.PairingFailed); }
                    }
                    break;
                }

            case Ping p:
                Send(new Pong(p.Id, p.T0, nowUs, MonoClock.NowUs()));
                break;

            case Pong p when p.Id == _pingId && p.T0 == _lastPingT0:
                Clock.Add(p.T0, p.T1, p.T2, nowUs);
                break;

            case SurfaceInfo si when _accepted:
                _surfaceAspect = si.WidthMm / si.HeightMm;
                Log.Info($"surface: {si.WidthPx}×{si.HeightPx} px, {si.WidthMm:F0}×{si.HeightMm:F0} mm");
                _server.OnSurface(si.WidthMm, si.HeightMm);
                OnSettingsChanged();
                break;

            case Bye b:
                throw new IOException($"tablet closed ({b.Reason})");

            default:
                break; // unknown messages, or ones meaningless in this state, are ignored (SPEC §1)
        }
    }

    private void Accept()
    {
        Uncount();
        Thread.CurrentThread.Priority = ThreadPriority.Highest;   // the hot path (Accept runs on the receive thread)
        _server.SetPending(this, clearOnlyIf: true);
        _server.Activate(this);   // the previous session is closed here (BYE 3) and awaited
        Pipeline = new PenPipeline(_server.Sink, _server.MakeSettings(_surfaceAspect), _server.Actions);
        _accepted = true;
        SendHostStatus();
        Log.Info($"accepted: {_hello!.Model} ({(_isUsb ? "USB" : "Wi-Fi")})");
    }

    /// <summary>
    /// New settings (monitor, area, curve; or a new surface size). The pipeline switches at once; HOST_STATUS is sent by
    /// the heartbeat timer thread, woken immediately, because the caller may be the UI thread and a blocking socket write
    /// there could freeze the window for up to the 1 s send timeout.
    /// </summary>
    internal void OnSettingsChanged()
    {
        var p = Pipeline;
        if (p == null) return;
        p.UpdateSettings(_server.MakeSettings(_surfaceAspect));
        Volatile.Write(ref _statusDirty, 1);
        try { _pinger?.Change(0, HeartbeatMs); } catch (ObjectDisposedException) { }
    }

    private int _statusDirty;

    private void SendHostStatus()
    {
        try { lock (_sendLock) SendLocked(HostStatusFrame(Pipeline!)); } catch { }
    }

    private byte[] HostStatusFrame(PenPipeline p)
    {
        var a = p.Settings.Mapping.EffectiveArea;
        return Codec.Encode(new HostStatus(_server.MonitorName, a.Left, a.Top, a.Right, a.Bottom));
    }

    private void StartHeartbeat()
    {
        _pinger = new Timer(_ =>
        {
            try
            {
                lock (_sendLock)
                {
                    _pingId++;
                    _lastPingT0 = MonoClock.NowUs();
                    SendLocked(Codec.Encode(new Ping(_pingId, _lastPingT0)));
                    // Settings are read under the send lock, so of two quick changes the later one is always sent last.
                    if (Interlocked.Exchange(ref _statusDirty, 0) == 1 && Pipeline is { } p) SendLocked(HostStatusFrame(p));
                }
            }
            catch { /* the receive thread will notice the disconnect anyway */ }
        }, null, 0, HeartbeatMs);
    }

    private void Send(Message m)
    {
        byte[] b = Codec.Encode(m);
        lock (_sendLock) SendLocked(b);
    }

    private void SendLocked(byte[] b)
    {
        int off = 0;
        while (off < b.Length) off += _s.Send(b, off, b.Length - off, SocketFlags.None);
    }

    private void ReadExactly(Span<byte> dst)
    {
        int off = 0;
        while (off < dst.Length)
        {
            // Not yet accepted: the handshake has a deadline (PINGs alone do not keep a connection open forever).
            if (!_accepted && Environment.TickCount64 > _deadlineMs)
                throw new ProtocolException(ProtocolError.Range, "authentication timed out");
            int n = _s.Receive(dst[off..]);
            if (n == 0) throw new IOException("peer closed");
            off += n;
        }
    }
}
