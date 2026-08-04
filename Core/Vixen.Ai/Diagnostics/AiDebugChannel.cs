// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Ai.Diagnostics;

/// <summary>What an AI debug message is.</summary>
/// <remarks>
///     ⚠ <b>The values are written down and must not be renumbered</b>, for the reason
///     <c>InspectorProtocol</c> gives at length: the two ends are different processes and often
///     different builds, so a member that moved when somebody inserted one would be a silent
///     misinterpretation rather than an error.
/// </remarks>
public enum AiDebugMessage : byte {
    /// <summary>Editor → build: send me this agent's state.</summary>
    RequestAgent = 1,

    /// <summary>Build → editor: one agent, with its rows.</summary>
    Agent = 2,

    /// <summary>Build → editor: there is no such agent, or the channel is off.</summary>
    NoAgent = 3
}

/// <summary>
///     One agent's debug state, over a wire.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 37 § D17's one exception, built as an exception.</b> Nothing in <c>Vixen.Ai</c> is
///         replicated and nothing ever will be — a client that planned would plan from an
///         interpolated view and reach different conclusions — but the editor's AI debugger has to
///         work against a running dedicated server, and that means a request and a response for one
///         agent's state.
///     </para>
///     <para>
///         ⚠ <b><see cref="Enabled" /> is off, and a build that never turns it on cannot answer.</b>
///         <see cref="TryHandle" /> refuses before it looks at the entity, so the switch is one branch
///         rather than a policy every caller has to remember. A host turns it on from the same
///         condition doc 13's remote inspector uses — <c>BuildVariants.Current.HasDiagnostics()</c> —
///         which is false in a shipping build. It is a property rather than a compile-time flag for
///         the reason <c>DebugDraw.Enabled</c> is one: the build where somebody needs this most is a
///         production server, and doc 13 asks for exactly that.
///     </para>
///     <para>
///         ⚠ <b>The payload is an <see cref="AiAgentSnapshot" /> and nothing else</b> — no tree, no
///         template, no blackboard layout. The far end gets rows of formatted strings, so the editor
///         needs no copy of the server's content to read them and a protocol version does not have to
///         move when somebody adds a node type.
///     </para>
///     <para>
///         Hand-written, little-endian, length-prefixed, and a truncated message is refused rather
///         than read past — the four rules <c>InspectorProtocol</c> lays down, followed here rather
///         than re-argued.
///     </para>
/// </remarks>
public sealed class AiDebugChannel {
    /// <summary>What this build speaks. Bumped when a field is added to an existing message.</summary>
    public const ushort Version = 1;

    /// <summary>The longest string any field may carry.</summary>
    /// <remarks>A ceiling on what a malformed length prefix can make the far end allocate.</remarks>
    public const int MaximumStringBytes = 1024;

    /// <summary>How many rows one response may carry.</summary>
    /// <remarks>
    ///     A capture is already bounded by <see cref="AiSnapshots.MaximumRowsPerSection" />; this is
    ///     the reader's own ceiling, because a reader must not trust a count a sender wrote.
    /// </remarks>
    public const int MaximumRows = 256;

    readonly AiAgentSnapshot scratch = new();

    /// <summary>Whether this build answers at all. ⚠ Off, and a shipping build leaves it off.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many requests have been answered.</summary>
    public int Answered { get; private set; }

    /// <summary>How many have been refused, because the switch is off or the agent is gone.</summary>
    public int Refused { get; private set; }

    /// <summary>Writes a request for one agent.</summary>
    /// <param name="writer">Where the bytes go.</param>
    /// <param name="entity">Which agent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer" /> is null.</exception>
    public static void WriteRequest(IBufferWriter<byte> writer, Entity entity) {
        ArgumentNullException.ThrowIfNull(writer);

        var span = writer.GetSpan(13);

        span[0] = (byte)AiDebugMessage.RequestAgent;
        BinaryPrimitives.WriteUInt16LittleEndian(span[1..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[3..], entity.Id);
        BinaryPrimitives.WriteInt32LittleEndian(span[7..], entity.Version);

        // ⚠ The world id travels too. A handle without it names whatever shares the slot in whichever
        // world the far end happens to look in — and a dedicated server has more than one.
        BinaryPrimitives.WriteInt16LittleEndian(span[11..], entity.WorldId);
        writer.Advance(13);
    }

    /// <summary>Reads a request.</summary>
    /// <param name="payload">The message.</param>
    /// <param name="entity">Which agent it asked about.</param>
    /// <param name="version">What the far end speaks.</param>
    /// <returns>Whether it was a whole request.</returns>
    public static bool TryReadRequest(ReadOnlySpan<byte> payload, out Entity entity, out ushort version) {
        entity = Entity.Null;
        version = 0;

        if (payload.Length < 13 || payload[0] != (byte)AiDebugMessage.RequestAgent) {
            return false;
        }

        version = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        entity = new(
            BinaryPrimitives.ReadInt32LittleEndian(payload[3..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[7..]),
            BinaryPrimitives.ReadInt16LittleEndian(payload[11..])
        );

        return true;
    }

    /// <summary>Answers a request, if this build answers requests.</summary>
    /// <param name="request">What arrived.</param>
    /// <param name="system">The system holding the agents.</param>
    /// <param name="world">Their world.</param>
    /// <param name="reply">Where the answer goes.</param>
    /// <returns>Whether an agent was described. A refusal is still written.</returns>
    /// <exception cref="ArgumentNullException">Any argument but <paramref name="request" /> is null.</exception>
    public bool TryHandle(ReadOnlySpan<byte> request, Ecs.AiSystem system, World world, IBufferWriter<byte> reply) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(reply);

        // ⚠ The switch is tested before the request is even parsed. A build that does not carry this
        // feature must not be distinguishable from one that does by how it fails.
        if (!Enabled
            || !TryReadRequest(request, out var entity, out var version)
            || version != Version
            || !AiSnapshots.Take(system, world, entity, scratch)) {
            Refused++;
            reply.GetSpan(1)[0] = (byte)AiDebugMessage.NoAgent;
            reply.Advance(1);

            return false;
        }

        WriteAgent(reply, scratch);
        Answered++;

        return true;
    }

    /// <summary>Writes an agent's state.</summary>
    /// <param name="writer">Where the bytes go.</param>
    /// <param name="snapshot">The state.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static void WriteAgent(IBufferWriter<byte> writer, AiAgentSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(snapshot);

        Byte(writer, (byte)AiDebugMessage.Agent);
        Int(writer, snapshot.Entity.Id);
        Int(writer, snapshot.Entity.Version);
        Short(writer, snapshot.Entity.WorldId);
        Long(writer, snapshot.Tick);
        Byte(writer, (byte)snapshot.Planner);
        Byte(writer, (byte)snapshot.Status);
        Byte(writer, snapshot.Located ? (byte)1 : (byte)0);
        Float(writer, snapshot.Position.X);
        Float(writer, snapshot.Position.Y);
        Float(writer, snapshot.Position.Z);
        Text(writer, snapshot.Asset.ToString());
        Text(writer, snapshot.Action.ToString());
        Text(writer, snapshot.Reason);

        var rows = Math.Min(snapshot.Count, MaximumRows);

        Int(writer, rows);

        for (var index = 0; index < rows; index++) {
            var row = snapshot.Rows[index];

            Byte(writer, (byte)row.Section);
            Byte(writer, row.Active ? (byte)1 : (byte)0);
            Float(writer, row.Number);
            Text(writer, row.Name);
            Text(writer, row.Value);
        }
    }

    /// <summary>Reads an agent's state.</summary>
    /// <param name="payload">The message.</param>
    /// <param name="into">Where to put it. Cleared first.</param>
    /// <returns>Whether it was a whole, well-formed agent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="into" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Every length is checked against what is left before it is used.</b> A transport may
    ///     hand over a datagram that was cut short, and a reader that trusted a prefix would index off
    ///     the end of a buffer on the editor's frame thread — a crash in the tool somebody attached
    ///     <i>because</i> something was already going wrong.
    /// </remarks>
    public static bool TryReadAgent(ReadOnlySpan<byte> payload, AiAgentSnapshot into) {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        var cursor = 0;

        if (!TryByte(payload, ref cursor, out var kind) || kind != (byte)AiDebugMessage.Agent) {
            return false;
        }

        if (!TryInt(payload, ref cursor, out var id)
            || !TryInt(payload, ref cursor, out var generation)
            || !TryShort(payload, ref cursor, out var worldId)
            || !TryLong(payload, ref cursor, out var tick)
            || !TryByte(payload, ref cursor, out var planner)
            || !TryByte(payload, ref cursor, out var status)
            || !TryByte(payload, ref cursor, out var located)
            || !TryFloat(payload, ref cursor, out var x)
            || !TryFloat(payload, ref cursor, out var y)
            || !TryFloat(payload, ref cursor, out var z)
            || !TryText(payload, ref cursor, out var asset)
            || !TryText(payload, ref cursor, out var action)
            || !TryText(payload, ref cursor, out var reason)
            || !TryInt(payload, ref cursor, out var rows)
            || (uint)rows > MaximumRows) {
            into.Clear();

            return false;
        }

        into.Entity = new(id, generation, worldId);
        into.Tick = tick;
        into.Planner = Enum.IsDefined((AiPlanner)planner) ? (AiPlanner)planner : AiPlanner.None;
        into.Status = Enum.IsDefined((ActionStatus)status) ? (ActionStatus)status : ActionStatus.Running;
        into.Located = located != 0;
        into.Position = new(x, y, z);
        into.Asset = Symbol.Intern(asset);
        into.Action = Symbol.Intern(action);
        into.Reason = reason;

        for (var index = 0; index < rows; index++) {
            if (!TryByte(payload, ref cursor, out var section)
                || !TryByte(payload, ref cursor, out var active)
                || !TryFloat(payload, ref cursor, out var number)
                || !TryText(payload, ref cursor, out var name)
                || !TryText(payload, ref cursor, out var value)) {
                into.Clear();

                return false;
            }

            into.Add(
                new(
                    Enum.IsDefined((AiDebugSection)section) ? (AiDebugSection)section : AiDebugSection.Doing,
                    name,
                    value,
                    number,
                    active != 0
                )
            );
        }

        return true;
    }

    static void Byte(IBufferWriter<byte> writer, byte value) {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    static void Int(IBufferWriter<byte> writer, int value) {
        BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value);
        writer.Advance(4);
    }

    static void Short(IBufferWriter<byte> writer, short value) {
        BinaryPrimitives.WriteInt16LittleEndian(writer.GetSpan(2), value);
        writer.Advance(2);
    }

    static void Long(IBufferWriter<byte> writer, long value) {
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), value);
        writer.Advance(8);
    }

    static void Float(IBufferWriter<byte> writer, float value) {
        BinaryPrimitives.WriteSingleLittleEndian(writer.GetSpan(4), value);
        writer.Advance(4);
    }

    static void Text(IBufferWriter<byte> writer, string? value) {
        var text = value ?? string.Empty;
        var bytes = Encoding.UTF8.GetByteCount(text);

        // Truncated at the writer rather than refused, because the alternative is a debugger that
        // cannot show an agent whose action somebody gave a long name.
        if (bytes > MaximumStringBytes) {
            text = text[..Math.Min(text.Length, MaximumStringBytes / 4)];
            bytes = Encoding.UTF8.GetByteCount(text);
        }

        Int(writer, bytes);

        if (bytes == 0) {
            return;
        }

        Encoding.UTF8.GetBytes(text, writer.GetSpan(bytes));
        writer.Advance(bytes);
    }

    static bool TryByte(ReadOnlySpan<byte> payload, ref int cursor, out byte value) {
        value = 0;

        if (cursor + 1 > payload.Length) {
            return false;
        }

        value = payload[cursor];
        cursor += 1;

        return true;
    }

    static bool TryShort(ReadOnlySpan<byte> payload, ref int cursor, out short value) {
        value = 0;

        if (cursor + 2 > payload.Length) {
            return false;
        }

        value = BinaryPrimitives.ReadInt16LittleEndian(payload[cursor..]);
        cursor += 2;

        return true;
    }

    static bool TryInt(ReadOnlySpan<byte> payload, ref int cursor, out int value) {
        value = 0;

        if (cursor + 4 > payload.Length) {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
        cursor += 4;

        return true;
    }

    static bool TryLong(ReadOnlySpan<byte> payload, ref int cursor, out long value) {
        value = 0;

        if (cursor + 8 > payload.Length) {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(payload[cursor..]);
        cursor += 8;

        return true;
    }

    static bool TryFloat(ReadOnlySpan<byte> payload, ref int cursor, out float value) {
        value = 0f;

        if (cursor + 4 > payload.Length) {
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(payload[cursor..]);
        cursor += 4;

        return true;
    }

    static bool TryText(ReadOnlySpan<byte> payload, ref int cursor, out string value) {
        value = string.Empty;

        if (!TryInt(payload, ref cursor, out var length)
            || (uint)length > MaximumStringBytes
            || cursor + length > payload.Length) {
            return false;
        }

        value = length == 0 ? string.Empty : Encoding.UTF8.GetString(payload.Slice(cursor, length));
        cursor += length;

        return true;
    }
}
