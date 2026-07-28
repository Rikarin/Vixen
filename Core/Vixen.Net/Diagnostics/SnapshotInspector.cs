// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Diagnostics;

/// <summary>One record inside a snapshot, as the inspector found it.</summary>
/// <param name="Object">Which networked object it is about.</param>
/// <param name="TypeName">Which component, or the empty string if the manifest does not know.</param>
/// <param name="IsDelta">Whether it was sent as a difference from an earlier capture.</param>
/// <param name="CapturedAt">The tick the value it carries was read from the world at.</param>
/// <param name="BaselineAt">
///     The capture the difference was measured from, or <see cref="Tick" /> zero for a whole record.
/// </param>
/// <param name="Bits">What the whole record cost, its header included.</param>
public readonly record struct SnapshotRecord(
    NetworkId Object,
    string TypeName,
    bool IsDelta,
    Tick CapturedAt,
    Tick BaselineAt,
    int Bits
) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Object} {TypeName} {(IsDelta ? $"Δ from {BaselineAt.Value}" : "whole")} — {Bits} bits"
        );
}

/// <summary>What a snapshot turned out to contain.</summary>
/// <param name="Tick">The tick it describes.</param>
/// <param name="Bits">How big the whole thing was.</param>
/// <param name="Removals">Objects it told the receiver to drop.</param>
/// <param name="Records">The values in it.</param>
/// <param name="Complete">
///     Whether it decoded to the end. False means the bytes were truncated or do not match this
///     manifest, and what was found before that point is still returned.
/// </param>
public readonly record struct SnapshotContents(
    Tick Tick,
    int Bits,
    IReadOnlyList<NetworkId> Removals,
    IReadOnlyList<SnapshotRecord> Records,
    bool Complete
);

/// <summary>Takes a snapshot apart, without applying any of it.</summary>
/// <remarks>
///     <para>
///         The half of a packet inspector that has to live next to the encoder, because it is the only
///         thing that knows the record layout. What is done with the answer — a console dump, a tick
///         timeline, an editor panel — is somebody else's problem and deliberately not this type's.
///     </para>
///     <para>
///         <b>It reads the same bytes the client does and applies none of them.</b> That is what makes
///         it usable on a recorded capture, on a snapshot the client rejected, and on a peer's traffic
///         while it is running. It needs the registry to name the component types, and it degrades to
///         reporting sizes without names rather than failing when a type is not in it.
///     </para>
///     <para>
///         A difference cannot be decoded to a value without the capture it was measured from, which
///         the inspector does not have. It reports the <i>shape</i> — which object, which component,
///         which baseline, how many bits — and that is the question a bandwidth investigation is
///         asking anyway.
///     </para>
/// </remarks>
public static class SnapshotInspector {
    /// <summary>Reads a snapshot.</summary>
    /// <param name="registry">The component types, for naming them.</param>
    /// <param name="snapshot">The bytes, exactly as they came off the wire.</param>
    /// <returns>What was in it.</returns>
    public static SnapshotContents Inspect(ReplicationRegistry registry, ReadOnlySpan<byte> snapshot) {
        ArgumentNullException.ThrowIfNull(registry);

        var total = snapshot.Length * 8;
        var reader = new BitReader(snapshot);
        var removals = new List<NetworkId>();
        var records = new List<SnapshotRecord>();

        if (!reader.TryReadUInt32(out var rawTick)) {
            return new(default, total, removals, records, Complete: false);
        }

        var tick = new Tick(rawTick);

        while (true) {
            if (!reader.TryReadBool(out var more)) {
                return Partial();
            }

            if (!more) {
                break;
            }

            if (!reader.TryReadVariable(out var removed)) {
                return Partial();
            }

            removals.Add(new(removed));
        }

        while (true) {
            if (!reader.TryReadBool(out var more)) {
                return Partial();
            }

            if (!more) {
                break;
            }

            var start = reader.BitsRead - 1;

            if (!TryRecord(registry, tick, start, ref reader, out var record)) {
                return Partial();
            }

            records.Add(record);
        }

        return new(tick, reader.BitsRead, removals, records, Complete: true);

        SnapshotContents Partial() => new(tick, total, removals, records, Complete: false);
    }

    static bool TryRecord(
        ReplicationRegistry registry,
        Tick tick,
        int start,
        ref BitReader reader,
        out SnapshotRecord record
    ) {
        record = default;

        if (!reader.TryReadVariable(out var id)
            || !reader.TryReadVariable(out var typeIndex)
            || !reader.TryReadBool(out var aged)) {
            return false;
        }

        var age = 0u;

        if (aged && !reader.TryReadVariable(out age)) {
            return false;
        }

        if (!reader.TryReadBool(out var isDelta)) {
            return false;
        }

        var baselineAge = 0u;

        if (isDelta && !reader.TryRead(ReplicationServer.BaselineAgeBits, out baselineAge)) {
            return false;
        }

        // The payload's own length is only knowable through the replicator, which is the one thing
        // an inspector cannot fake: a whole record is the declared layout, a difference has to be
        // walked. Both need the type, so an unknown type ends the read rather than guessing a length
        // and desynchronising everything after it.
        if (!registry.TryGetByIndex(typeIndex, out var replicator) || replicator is null) {
            return false;
        }

        var capturedAt = tick.Subtract((int)age);
        var lanes = replicator.Lanes;

        if (lanes.IsEmpty || !Skip(lanes, isDelta, ref reader)) {
            return false;
        }

        record = new(
            new(id),
            replicator.TypeName,
            isDelta,
            capturedAt,
            isDelta ? capturedAt.Subtract((int)baselineAge + 1) : default,
            reader.BitsRead - start
        );

        return true;
    }

    static bool Skip(ReadOnlySpan<WireLane> lanes, bool isDelta, ref BitReader reader) {
        if (!isDelta) {
            return reader.TryReadBitsOver(DeltaCodec.TotalBits(lanes));
        }

        foreach (var lane in lanes) {
            if (!reader.TryReadBool(out var changed)) {
                return false;
            }

            if (!changed) {
                continue;
            }

            if (!lane.Offset || lane.Bits < DeltaCodec.MinimumOffsetBits) {
                if (!reader.TryReadBitsOver(lane.Bits)) {
                    return false;
                }

                continue;
            }

            if (!reader.TryRead(DeltaCodec.SelectorBits, out var selector)
                || !reader.TryReadBitsOver(DeltaCodec.OffsetWidth(selector, lane.Bits))) {
                return false;
            }
        }

        return true;
    }
}
