// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Net.Messaging;
namespace Vixen.Samples.Mmo.Contracts;

/// <summary>A line of chat, on whichever channel the sender named.</summary>
/// <remarks>
///     <para>
///         <b>A broadcast rather than an RPC, and the distinction is doc 16's.</b> A remote call is
///         <em>about an object</em> — it names one, ownership is checked against it, and it is
///         refused if the receiver does not have it. Chat is about nobody's object: the sender may be
///         a hundred metres away and out of interest, or on another shard entirely.
///     </para>
///     <para>
///         ⚠ <b>The channel is a <c>DefId</c> and never a string.</b> Doc 28 makes a chat channel
///         content, so a game adds one by writing a <c>.vxdef</c> — putting the name on the wire
///         would make the wire format depend on a designer's spelling.
///     </para>
///     <para>
///         ⚠ <b>The text is capped by the reader and never by the packet.</b> A length read out of
///         the payload and then trusted is the oldest remote-allocation bug there is.
///     </para>
/// </remarks>
public struct ChatLine : IBroadcast<ChatLine> {
    /// <summary>Which channel, as a <c>DefId</c> value.</summary>
    public uint Channel { get; init; }

    /// <summary>The session's number for whoever said it.</summary>
    public uint Speaker { get; init; }

    /// <summary>What they said, already filtered by the realm.</summary>
    public string? Text { get; init; }

    /// <inheritdoc />
    public static string BroadcastName => "Mmo.ChatLine";

    /// <summary>The most a line may be, in UTF-8 bytes. The channel's own cap is smaller and authored.</summary>
    public const int MaximumBytes = 512;

    /// <inheritdoc />
    public readonly void Write(ref BitWriter writer) {
        var bytes = Encoding.UTF8.GetBytes(Text ?? string.Empty);

        writer.WriteVariable(Channel);
        writer.WriteVariable(Speaker);
        writer.WriteVariable((uint)Math.Min(bytes.Length, MaximumBytes));
        writer.WriteBytes(bytes.AsSpan(0, Math.Min(bytes.Length, MaximumBytes)));
    }

    /// <inheritdoc />
    public static bool TryRead(ref BitReader reader, out ChatLine value) {
        value = default;

        if (!reader.TryReadVariable(out var channel)
            || !reader.TryReadVariable(out var speaker)
            || !reader.TryReadVariable(out var length)
            || length > MaximumBytes
            || !reader.TryReadBytes((int)length, out var bytes)) {
            return false;
        }

        value = new() {
            Channel = channel,
            Speaker = speaker,
            Text = bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes)
        };

        return true;
    }
}

/// <summary>Where a dynamic event has got to. Sent to everybody on the map, not to a party.</summary>
/// <remarks>
///     The Rootbound Colossus is the case: it is nobody's object, everybody on the map can see the
///     bar, and a latecomer needs the current state rather than the history.
/// </remarks>
public struct EventProgress : IBroadcast<EventProgress> {
    /// <summary>Which event, as a <c>DefId</c> value.</summary>
    public uint Event { get; init; }

    /// <summary>How far along, in tenths of a percent, so it fits in a variable-length integer.</summary>
    public ushort PerMille { get; init; }

    /// <summary>How long is left, in whole seconds.</summary>
    public ushort Remaining { get; init; }

    /// <inheritdoc />
    public static string BroadcastName => "Mmo.EventProgress";

    /// <inheritdoc />
    public readonly void Write(ref BitWriter writer) {
        writer.WriteVariable(Event);
        writer.WriteVariable(PerMille);
        writer.WriteVariable(Remaining);
    }

    /// <inheritdoc />
    public static bool TryRead(ref BitReader reader, out EventProgress value) {
        value = default;

        if (!reader.TryReadVariable(out var identifier)
            || !reader.TryReadVariable(out var perMille)
            || !reader.TryReadVariable(out var remaining)) {
            return false;
        }

        value = new() {
            Event = identifier,
            PerMille = (ushort)Math.Min(perMille, 1000u),
            Remaining = (ushort)Math.Min(remaining, ushort.MaxValue)
        };

        return true;
    }
}

/// <summary>The battleground's score. One message rather than a component, because it is not an entity.</summary>
public struct MatchScore : IBroadcast<MatchScore> {
    /// <summary>What the first team has.</summary>
    public ushort Team0 { get; init; }

    /// <summary>What the second team has.</summary>
    public ushort Team1 { get; init; }

    /// <summary>How long is left, in whole seconds.</summary>
    public ushort Remaining { get; init; }

    /// <inheritdoc />
    public static string BroadcastName => "Mmo.MatchScore";

    /// <inheritdoc />
    public readonly void Write(ref BitWriter writer) {
        writer.WriteVariable(Team0);
        writer.WriteVariable(Team1);
        writer.WriteVariable(Remaining);
    }

    /// <inheritdoc />
    public static bool TryRead(ref BitReader reader, out MatchScore value) {
        value = default;

        if (!reader.TryReadVariable(out var team0)
            || !reader.TryReadVariable(out var team1)
            || !reader.TryReadVariable(out var remaining)) {
            return false;
        }

        value = new() {
            Team0 = (ushort)Math.Min(team0, ushort.MaxValue),
            Team1 = (ushort)Math.Min(team1, ushort.MaxValue),
            Remaining = (ushort)Math.Min(remaining, ushort.MaxValue)
        };

        return true;
    }
}
