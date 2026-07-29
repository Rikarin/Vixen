// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Sessions;

/// <summary>What a payload above the session is.</summary>
/// <remarks>
///     The session carries opaque bytes and does not care what is in them. Three things want to put
///     bytes there — replication, remote calls, and whatever the game itself sends — and without a
///     byte saying which, a receiver would have to guess. One byte, at the front, and each of the
///     three keeps its own decoder.
/// </remarks>
public enum PayloadKind : byte {
    /// <summary>The game's own. Nothing in the engine reads it.</summary>
    Game = 0,

    /// <summary>A replication snapshot.</summary>
    Replication = 1,

    /// <summary>A remote call.</summary>
    Rpc = 2,

    /// <summary>A typed message about nothing in particular.</summary>
    /// <remarks>
    ///     The one kind that is not about a networked object. See <c>BroadcastRouter</c> for why
    ///     that distinction is worth a payload kind rather than being folded into
    ///     <see cref="Rpc" />.
    /// </remarks>
    Broadcast = 3,

    /// <summary>A client's inputs for a run of ticks, which is what prediction rests on.</summary>
    Input = 4,

    /// <summary>The largest kind there is. Anything above it did not come from this engine.</summary>
    /// <remarks>
    ///     <b>Kept beside the members rather than written into <see cref="NetworkPayload.TryUnwrap" />,
    ///     because it was once written into it and went stale.</b> The check read "greater than
    ///     <see cref="Rpc" />" and stayed that way when <see cref="Broadcast" /> was added — so every
    ///     broadcast that went through the session layer was refused as malformed, silently, while the
    ///     router's own tests passed because they never went through it. There is now a test that
    ///     enumerates this enum and round-trips every member, so the next kind cannot repeat it.
    /// </remarks>
    Last = Input
}

/// <summary>Puts the kind byte on, and takes it off.</summary>
/// <remarks>
///     Deliberately a byte rather than bits: a snapshot is bit-packed and a call is bit-packed, and
///     both would have to be shifted by a bit to make room for a smaller marker. One byte a packet,
///     against a snapshot's hundreds, is the cheaper trade — and it keeps the two decoders reading
///     from a byte boundary, which is where their first field expects to be.
/// </remarks>
public static class NetworkPayload {
    /// <summary>Writes a payload with its kind in front of it.</summary>
    /// <param name="kind">What it is.</param>
    /// <param name="payload">The bytes.</param>
    /// <param name="buffer">Where to put the result.</param>
    /// <param name="wrapped">The result, if it fits.</param>
    /// <returns>Whether it fit.</returns>
    public static bool TryWrap(
        PayloadKind kind,
        ReadOnlySpan<byte> payload,
        Span<byte> buffer,
        out ReadOnlySpan<byte> wrapped
    ) {
        if (buffer.Length < payload.Length + 1) {
            wrapped = default;

            return false;
        }

        buffer[0] = (byte)kind;
        payload.CopyTo(buffer[1..]);
        wrapped = buffer[..(payload.Length + 1)];

        return true;
    }

    /// <summary>Reads the kind off the front of a payload.</summary>
    /// <param name="wrapped">The bytes as they arrived.</param>
    /// <param name="kind">What it is.</param>
    /// <param name="payload">The rest of it.</param>
    /// <returns>Whether there was a kind byte and it names one of ours.</returns>
    public static bool TryUnwrap(ReadOnlySpan<byte> wrapped, out PayloadKind kind, out ReadOnlySpan<byte> payload) {
        kind = PayloadKind.Game;
        payload = default;

        if (wrapped.IsEmpty || wrapped[0] > (byte)PayloadKind.Last) {
            return false;
        }

        kind = (PayloadKind)wrapped[0];
        payload = wrapped[1..];

        return true;
    }
}
