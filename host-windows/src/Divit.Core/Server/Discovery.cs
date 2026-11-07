// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Automatic PC discovery on Wi-Fi (protocol/SPEC.md §7). Runs only while Wi-Fi connections are allowed.
// Request (tablet → broadcast): "DIVIT-DISCOVER 1"   Reply (PC → tablet, direct): "DIVIT-HERE 2 <tcpPort> <hostId hex> <pcName>"
// The reply carries the PC name, port and PC ID. The ID is not secret: on connect the tablet asks the PC for a proof,
// which an impostor claiming the same ID cannot produce. The pairing code or keys are never sent.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Divit.Core.Server;

public sealed class DiscoveryResponder : IDisposable
{
    public const int DefaultPort = 47471;
    public const string Request = "DIVIT-DISCOVER 1";

    private readonly UdpClient _udp;
    private readonly byte[] _reply;
    private volatile bool _running = true;

    public int Port { get; }

    public DiscoveryResponder(int udpPort, int tcpPort, string hostName, byte[] hostId)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        // The name may contain spaces: the rest of the line is the name.
        _reply = Encoding.UTF8.GetBytes($"DIVIT-HERE 2 {tcpPort} {Convert.ToHexString(hostId).ToLowerInvariant()} {hostName}");
        new Thread(Loop) { IsBackground = true, Name = "discovery" }.Start();
        Log.Info($"discovery responder: UDP {Port}");
    }

    private void Loop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref any);
                if (data.Length > 64) continue;   // the expected request is short; large packets are ignored
                if (!Encoding.ASCII.GetString(data).StartsWith(Request, StringComparison.Ordinal)) continue;
                _udp.Send(_reply, _reply.Length, any);
            }
            catch (SocketException) when (_running) { /* ICMP "unreachable" etc.: keep going */ }
            catch { return; }
        }
    }

    public void Dispose()
    {
        _running = false;
        _udp.Dispose();
    }
}
