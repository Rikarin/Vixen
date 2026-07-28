// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>
///     A transport was asked to do something it cannot do — listen on an endpoint already taken,
///     connect a client half that is already connected.
/// </summary>
/// <remarks>
///     Deliberately narrow. Losing a packet, being refused a connection and being disconnected are
///     not exceptions: they are what a network does, they are reported through
///     <see cref="ITransportEvents" />, and a transport that threw for them would make ordinary
///     operation cost a stack trace.
/// </remarks>
public sealed class TransportException : InvalidOperationException {
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What was asked for, and why it cannot be done.</param>
    public TransportException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">What was asked for, and why it cannot be done.</param>
    /// <param name="innerException">The cause.</param>
    public TransportException(string message, Exception innerException) : base(message, innerException) {
    }

    /// <summary>Creates the exception with no message.</summary>
    public TransportException() {
    }
}
