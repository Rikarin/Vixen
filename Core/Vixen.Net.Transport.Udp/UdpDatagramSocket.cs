// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace Vixen.Net.Transport.Udp;

/// <summary>A real UDP socket.</summary>
/// <remarks>
///     Thin on purpose: everything that could be got wrong twice lives above the seam, where it is
///     tested against an in-memory bus. What is left here is the two or three things a datagram socket
///     needs to be told before it behaves — and each of them is a bug that only shows up in
///     production, which is why they are here with a reason attached rather than in a settings file.
/// </remarks>
public sealed class UdpDatagramSocket : IDatagramSocket {
    readonly Socket socket;

    /// <inheritdoc />
    public EndPoint? LocalEndPoint => socket.LocalEndPoint;

    /// <summary>Binds a socket.</summary>
    /// <param name="endPoint">Where. A port of zero lets the operating system choose.</param>
    public UdpDatagramSocket(IPEndPoint endPoint) {
        ArgumentNullException.ThrowIfNull(endPoint);

        socket = new(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };

        if (endPoint.AddressFamily == AddressFamily.InterNetworkV6) {
            socket.DualMode = true;
        }

        // Without this, one ICMP "port unreachable" from a peer that has gone away makes the *next*
        // receive on this socket throw ConnectionReset — on a connectionless socket, about a
        // connection it does not have. Every UDP stack turns it off; almost none of them say why.
        //
        // Windows only, and asked as a question rather than attempted and caught: the control code
        // is a Windows one, and the other platforms throw PlatformNotSupportedException rather than
        // a SocketException for it. The first real-socket test found that by failing on macOS.
        if (OperatingSystem.IsWindows()) {
            const int DisableConnectionReset = unchecked((int)0x9800000C);

            try {
                socket.IOControl(DisableConnectionReset, [0, 0, 0, 0], null);
            } catch (SocketException) {
                // An old Windows that does not know the code. It behaves like the platforms that
                // never needed it.
            }
        }

        socket.Bind(endPoint);
    }

    /// <inheritdoc />
    public void SendTo(ReadOnlySpan<byte> payload, EndPoint destination) {
        try {
            socket.SendTo(payload, SocketFlags.None, destination);
        } catch (SocketException) {
            // A datagram that could not be handed to the operating system is a datagram that was
            // lost, which is a thing the layer above already has to handle. Throwing here would make
            // an ordinary event an exceptional one.
        }
    }

    /// <inheritdoc />
    public bool TryReceiveFrom(Span<byte> buffer, out EndPoint from, out int length) {
        from = AnyEndPoint();

        length = 0;

        try {
            if (socket.Available <= 0) {
                return false;
            }

            length = socket.ReceiveFrom(buffer, SocketFlags.None, ref from);

            return true;
        } catch (SocketException) {
            return false;
        }
    }

    /// <summary>Closes the socket.</summary>
    public void Dispose() => socket.Dispose();

    IPEndPoint AnyEndPoint() =>
        socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
}

/// <summary>Makes real UDP sockets.</summary>
public sealed class UdpDatagramSocketFactory : IDatagramSocketFactory {
    /// <inheritdoc />
    public IDatagramSocket Bind(IPEndPoint endPoint) => new UdpDatagramSocket(endPoint);
}
