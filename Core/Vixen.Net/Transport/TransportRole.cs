// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>Which half of a transport an event or a state belongs to.</summary>
/// <remarks>
///     This is the local half, not the remote peer: <see cref="Server" /> on an event means "our
///     server half saw this", whoever it was that did it.
/// </remarks>
public enum TransportRole : byte {
    /// <summary>The half that listens and numbers connections.</summary>
    Server = 0,

    /// <summary>The half that connects to a server.</summary>
    Client = 1
}

/// <summary>Whether a half of a transport is running, and if not, which way it is going.</summary>
public enum TransportState : byte {
    /// <summary>Not running. What a transport is before it is started and after it is stopped.</summary>
    Stopped = 0,

    /// <summary>Asked to start, not there yet — connecting, or binding.</summary>
    Starting = 1,

    /// <summary>Listening, or connected.</summary>
    Running = 2,

    /// <summary>Asked to stop, still shutting down.</summary>
    Stopping = 3
}
