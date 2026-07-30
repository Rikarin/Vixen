// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Vixen.Editor.Debugger;

/// <summary>What a remote-inspector message is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The values are written down and must not be renumbered.</b> The two ends of this
///         protocol are different processes and, on a device, different builds — an editor talking
///         to a player from last week's install is the ordinary case, not the exceptional one. An
///         enum whose members moved when somebody inserted one in the middle would make that a
///         silent misinterpretation rather than an error.
///     </para>
/// </remarks>
public enum InspectorMessage : byte {
    /// <summary>Editor → build: who are you, and what version of this protocol do you speak?</summary>
    Hello = 1,

    /// <summary>Build → editor: the answer, carrying the build's name and its protocol version.</summary>
    Welcome = 2,

    /// <summary>Editor → build: send me the entity tree.</summary>
    RequestTree = 3,

    /// <summary>Build → editor: one entity, with its parent and its components.</summary>
    Entity = 4,

    /// <summary>Build → editor: that is the whole tree.</summary>
    TreeComplete = 5,

    /// <summary>Editor → build: set this component member to this value.</summary>
    SetValue = 6,

    /// <summary>Build → editor: a counter's current reading.</summary>
    Counter = 7,

    /// <summary>Editor → build: do the named thing — capture a frame, collect, reload.</summary>
    Command = 8,

    /// <summary>Build → editor: what happened to the last request.</summary>
    Result = 9
}

/// <summary>One entity as the far end describes it.</summary>
/// <param name="Id">The build's own identifier for it, opaque here.</param>
/// <param name="Parent">Its parent's id, or zero for a root.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Components">The component type names it carries.</param>
public sealed record RemoteEntity(ulong Id, ulong Parent, string Name, IReadOnlyList<string> Components);

/// <summary>One live number the far end is reporting.</summary>
/// <param name="Name">What it counts.</param>
/// <param name="Value">The reading.</param>
public readonly record struct RemoteCounter(string Name, double Value);

/// <summary>
///     The wire format between the editor and a running build: a one-byte kind, then fields.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 13 owns the protocol and says what it must carry — browse the live hierarchy, read
///         and <i>write</i> component values, live counters, and trigger a capture. This is that
///         list and no more.</b> Discovery and pairing are deliberately not here: which transport
///         finds which device is <c>Vixen.Net</c>'s question, and a protocol that opened its own
///         socket would be a second answer to it.
///     </para>
///     <para>
///         ⚠ <b>Hand-written rather than JSON, and the reason is the far end.</b> A phone streaming
///         its entity tree over a phone's uplink is the case this exists for; the same tree as JSON
///         is several times the bytes and needs a parser in the player. Every field below is a
///         length-prefixed string or a fixed-width number, which is a reader in forty lines on both
///         sides.
///     </para>
///     <para>
///         ⚠ <b>Little-endian, stated rather than assumed.</b> Every platform this engine targets is
///         little-endian today and one of them will not be; <c>BinaryPrimitives</c> makes the choice
///         explicit and costs nothing where it already agrees.
///     </para>
///     <para>
///         ⚠ <b>A truncated message is refused rather than read past.</b> A transport may hand over a
///         datagram that was cut short, and a reader that trusted a length prefix would index off the
///         end of a buffer on the editor's frame thread — which is a crash in the tool somebody
///         attached <i>because</i> something was already going wrong.
///     </para>
/// </remarks>
public static class InspectorProtocol {
    /// <summary>What this build of the editor speaks.</summary>
    /// <remarks>
    ///     Bumped when a field is added to an existing message. A far end reporting a different
    ///     version is refused with a sentence rather than half-understood, because a protocol
    ///     mismatch that shows an empty entity tree looks exactly like a build with no entities.
    /// </remarks>
    public const ushort Version = 1;

    /// <summary>The longest string any field may carry.</summary>
    /// <remarks>
    ///     A ceiling on what a malformed or hostile length prefix can make the editor allocate. Names
    ///     and type names are tens of bytes; four kilobytes is far past anything real and far short
    ///     of anything that matters.
    /// </remarks>
    public const int MaximumStringBytes = 4096;

    /// <summary>Writes a <see cref="InspectorMessage.Hello" />.</summary>
    /// <param name="writer">Where the bytes go.</param>
    /// <param name="editor">What the editor calls itself.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static void WriteHello(IBufferWriter<byte> writer, string editor) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(editor);

        Kind(writer, InspectorMessage.Hello);
        UInt16(writer, Version);
        String(writer, editor);
    }

    /// <summary>Writes a <see cref="InspectorMessage.Welcome" />.</summary>
    /// <param name="writer">Where the bytes go.</param>
    /// <param name="build">What the build calls itself.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static void WriteWelcome(IBufferWriter<byte> writer, string build) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(build);

        Kind(writer, InspectorMessage.Welcome);
        UInt16(writer, Version);
        String(writer, build);
    }

    /// <summary>Writes a message with no fields.</summary>
    /// <param name="writer">Where the bytes go.</param>
    /// <param name="message">Which message.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer" /> is null.</exception>
    public static void WriteBare(IBufferWriter<byte> writer, InspectorMessage message) {
        ArgumentNullException.ThrowIfNull(writer);
        Kind(writer, message);
    }

    /// <summary>Writes an <see cref="InspectorMessage.Entity" />.</summary>
    /// <param name="writer">Where the bytes go.</param>
    /// <param name="entity">The entity.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static void WriteEntity(IBufferWriter<byte> writer, RemoteEntity entity) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(entity);

        Kind(writer, InspectorMessage.Entity);
        UInt64(writer, entity.Id);
        UInt64(writer, entity.Parent);
        String(writer, entity.Name);
        UInt16(writer, (ushort)Math.Min(entity.Components.Count, ushort.MaxValue));

        foreach (var component in entity.Components) {
            String(writer, component);
        }
    }

    /// <summary>Writes a <see cref="InspectorMessage.SetValue" />.</summary>
    /// <param name="writer">Where the bytes go.</param>
    /// <param name="entity">Which entity.</param>
    /// <param name="member">Which member, as <c>Component.Member</c>.</param>
    /// <param name="value">The new value, as text.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The value crosses as text, and that is a decision rather than a shortcut.</b> The two
    ///     ends do not share a serializer — a player built last week has last week's schema — and a
    ///     binary value would be interpreted against whichever layout the receiver happens to hold.
    ///     Text is what the inspector already edits and what the far end already knows how to parse
    ///     for its own scene files.
    /// </remarks>
    public static void WriteSetValue(IBufferWriter<byte> writer, ulong entity, string member, string value) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(value);

        Kind(writer, InspectorMessage.SetValue);
        UInt64(writer, entity);
        String(writer, member);
        String(writer, value);
    }

    /// <summary>Writes a <see cref="InspectorMessage.Counter" />.</summary>
    /// <param name="writer">Where the bytes go.</param>
    /// <param name="counter">The counter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer" /> is null.</exception>
    public static void WriteCounter(IBufferWriter<byte> writer, RemoteCounter counter) {
        ArgumentNullException.ThrowIfNull(writer);

        Kind(writer, InspectorMessage.Counter);
        String(writer, counter.Name);

        var span = writer.GetSpan(sizeof(double));
        BinaryPrimitives.WriteDoubleLittleEndian(span, counter.Value);
        writer.Advance(sizeof(double));
    }

    /// <summary>Writes a <see cref="InspectorMessage.Command" /> or a <see cref="InspectorMessage.Result" />.</summary>
    /// <param name="writer">Where the bytes go.</param>
    /// <param name="message">Which of the two.</param>
    /// <param name="text">The verb, or what happened.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static void WriteText(IBufferWriter<byte> writer, InspectorMessage message, string text) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(text);

        Kind(writer, message);
        String(writer, text);
    }

    /// <summary>Reads whichever message a payload holds.</summary>
    /// <param name="payload">The bytes.</param>
    /// <param name="message">What it was.</param>
    /// <returns>
    ///     A cursor positioned after the kind byte, for the <c>TryRead…</c> methods. An empty payload
    ///     reports <see langword="false" />.
    /// </returns>
    public static bool TryReadKind(ReadOnlySpan<byte> payload, out InspectorMessage message) {
        if (payload.Length < 1 || !Enum.IsDefined((InspectorMessage)payload[0])) {
            message = default;
            return false;
        }

        message = (InspectorMessage)payload[0];
        return true;
    }

    /// <summary>Reads a <see cref="InspectorMessage.Hello" /> or <see cref="InspectorMessage.Welcome" />.</summary>
    /// <param name="payload">The whole payload, kind byte included.</param>
    /// <param name="version">What the far end speaks.</param>
    /// <param name="name">What it calls itself.</param>
    /// <returns>Whether the payload was well-formed.</returns>
    public static bool TryReadGreeting(ReadOnlySpan<byte> payload, out ushort version, out string name) {
        version = 0;
        name = string.Empty;

        var cursor = 1;

        return payload.Length >= 1
            && TryUInt16(payload, ref cursor, out version)
            && TryString(payload, ref cursor, out name);
    }

    /// <summary>Reads an <see cref="InspectorMessage.Entity" />.</summary>
    /// <param name="payload">The whole payload, kind byte included.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether the payload was well-formed.</returns>
    public static bool TryReadEntity(ReadOnlySpan<byte> payload, out RemoteEntity? entity) {
        entity = null;

        var cursor = 1;

        if (!TryUInt64(payload, ref cursor, out var id)
            || !TryUInt64(payload, ref cursor, out var parent)
            || !TryString(payload, ref cursor, out var name)
            || !TryUInt16(payload, ref cursor, out var count)) {
            return false;
        }

        var components = new string[count];

        for (var index = 0; index < count; index++) {
            if (!TryString(payload, ref cursor, out components[index])) {
                return false;
            }
        }

        entity = new(id, parent, name, components);
        return true;
    }

    /// <summary>Reads a <see cref="InspectorMessage.SetValue" />.</summary>
    /// <param name="payload">The whole payload, kind byte included.</param>
    /// <param name="entity">Which entity.</param>
    /// <param name="member">Which member.</param>
    /// <param name="value">The new value.</param>
    /// <returns>Whether the payload was well-formed.</returns>
    public static bool TryReadSetValue(
        ReadOnlySpan<byte> payload,
        out ulong entity,
        out string member,
        out string value
    ) {
        entity = 0;
        member = string.Empty;
        value = string.Empty;

        var cursor = 1;

        return TryUInt64(payload, ref cursor, out entity)
            && TryString(payload, ref cursor, out member)
            && TryString(payload, ref cursor, out value);
    }

    /// <summary>Reads a <see cref="InspectorMessage.Counter" />.</summary>
    /// <param name="payload">The whole payload, kind byte included.</param>
    /// <param name="counter">The counter.</param>
    /// <returns>Whether the payload was well-formed.</returns>
    public static bool TryReadCounter(ReadOnlySpan<byte> payload, out RemoteCounter counter) {
        counter = default;

        var cursor = 1;

        if (!TryString(payload, ref cursor, out var name) || cursor + sizeof(double) > payload.Length) {
            return false;
        }

        counter = new(name, BinaryPrimitives.ReadDoubleLittleEndian(payload[cursor..]));
        return true;
    }

    /// <summary>Reads a <see cref="InspectorMessage.Command" /> or <see cref="InspectorMessage.Result" />.</summary>
    /// <param name="payload">The whole payload, kind byte included.</param>
    /// <param name="text">The verb, or what happened.</param>
    /// <returns>Whether the payload was well-formed.</returns>
    public static bool TryReadText(ReadOnlySpan<byte> payload, out string text) {
        var cursor = 1;
        return TryString(payload, ref cursor, out text);
    }

    static void Kind(IBufferWriter<byte> writer, InspectorMessage message) {
        var span = writer.GetSpan(1);
        span[0] = (byte)message;
        writer.Advance(1);
    }

    static void UInt16(IBufferWriter<byte> writer, ushort value) {
        var span = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        writer.Advance(sizeof(ushort));
    }

    static void UInt64(IBufferWriter<byte> writer, ulong value) {
        var span = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        writer.Advance(sizeof(ulong));
    }

    static void String(IBufferWriter<byte> writer, string value) {
        var bytes = Encoding.UTF8.GetByteCount(value);

        if (bytes > MaximumStringBytes) {
            // ⚠ Truncated at a *text element* boundary rather than a byte one or a UTF-16 one. A
            // quarter of the ceiling is a safe char count because no scalar is more than four bytes;
            // what a plain slice would still get wrong is a surrogate pair, whose halves are two
            // chars — and a lone surrogate is not encodable, so the far end would read a replacement
            // character where a name had one emoji in it.
            var length = Math.Min(value.Length, MaximumStringBytes / 4);

            if (length > 0 && char.IsHighSurrogate(value[length - 1])) {
                length--;
            }

            value = value[..length];
            bytes = Encoding.UTF8.GetByteCount(value);
        }

        UInt16(writer, (ushort)bytes);

        var span = writer.GetSpan(bytes);
        Encoding.UTF8.GetBytes(value, span);
        writer.Advance(bytes);
    }

    static bool TryUInt16(ReadOnlySpan<byte> payload, ref int cursor, out ushort value) {
        if (cursor + sizeof(ushort) > payload.Length) {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(payload[cursor..]);
        cursor += sizeof(ushort);

        return true;
    }

    static bool TryUInt64(ReadOnlySpan<byte> payload, ref int cursor, out ulong value) {
        if (cursor + sizeof(ulong) > payload.Length) {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(payload[cursor..]);
        cursor += sizeof(ulong);

        return true;
    }

    static bool TryString(ReadOnlySpan<byte> payload, ref int cursor, out string value) {
        value = string.Empty;

        if (!TryUInt16(payload, ref cursor, out var length)) {
            return false;
        }

        if (length > MaximumStringBytes || cursor + length > payload.Length) {
            return false;
        }

        value = Encoding.UTF8.GetString(payload.Slice(cursor, length));
        cursor += length;

        return true;
    }
}
