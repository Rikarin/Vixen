// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Xunit;

namespace Vixen.Editor.Debugger.Tests;

/// <summary>The wire format, written and read back.</summary>
/// <remarks>
///     ⚠ <b>The truncation cases are the ones worth having.</b> A transport may hand over a datagram
///     that was cut short, and a reader that trusted a length prefix would index off the end of a
///     buffer on the editor's frame thread — a crash in the tool somebody attached <i>because</i>
///     something was already going wrong.
/// </remarks>
public sealed class InspectorProtocolTests {
    static byte[] Written(Action<ArrayBufferWriter<byte>> write) {
        ArrayBufferWriter<byte> writer = new(256);
        write(writer);

        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public void AGreetingRoundTrips() {
        var payload = Written(writer => InspectorProtocol.WriteHello(writer, "Vixen Editor"));

        Assert.True(InspectorProtocol.TryReadKind(payload, out var kind));
        Assert.Equal(InspectorMessage.Hello, kind);

        Assert.True(InspectorProtocol.TryReadGreeting(payload, out var version, out var name));
        Assert.Equal(InspectorProtocol.Version, version);
        Assert.Equal("Vixen Editor", name);
    }

    [Fact]
    public void AnEntityRoundTripsWithItsComponents() {
        var payload = Written(
            writer => InspectorProtocol.WriteEntity(writer, new(9, 4, "Camera", ["Transform", "Camera"]))
        );

        Assert.True(InspectorProtocol.TryReadEntity(payload, out var entity));
        Assert.NotNull(entity);

        Assert.Equal(9ul, entity.Id);
        Assert.Equal(4ul, entity.Parent);
        Assert.Equal("Camera", entity.Name);
        Assert.Equal(["Transform", "Camera"], entity.Components);
    }

    [Fact]
    public void AnEntityWithNoComponentsRoundTrips() {
        var payload = Written(writer => InspectorProtocol.WriteEntity(writer, new(1, 0, "Empty", [])));

        Assert.True(InspectorProtocol.TryReadEntity(payload, out var entity));
        Assert.Empty(entity!.Components);
    }

    [Fact]
    public void AWriteRoundTrips() {
        var payload = Written(writer => InspectorProtocol.WriteSetValue(writer, 3, "Transform.Position", "1 2 3"));

        Assert.True(InspectorProtocol.TryReadSetValue(payload, out var entity, out var member, out var value));
        Assert.Equal(3ul, entity);
        Assert.Equal("Transform.Position", member);
        Assert.Equal("1 2 3", value);
    }

    [Fact]
    public void ACounterRoundTripsWithItsFraction() {
        var payload = Written(writer => InspectorProtocol.WriteCounter(writer, new("frame.ms", 16.6667)));

        Assert.True(InspectorProtocol.TryReadCounter(payload, out var counter));
        Assert.Equal("frame.ms", counter.Name);
        Assert.Equal(16.6667, counter.Value, 6);
    }

    [Fact]
    public void NonAsciiSurvivesTheRoundTrip() {
        var payload = Written(writer => InspectorProtocol.WriteEntity(writer, new(1, 0, "Kamera – 光", ["Ubersetzung"])));

        Assert.True(InspectorProtocol.TryReadEntity(payload, out var entity));
        Assert.Equal("Kamera – 光", entity!.Name);
    }

    [Fact]
    public void AnEmptyPayloadIsRefusedRatherThanRead() {
        Assert.False(InspectorProtocol.TryReadKind([], out _));
        Assert.False(InspectorProtocol.TryReadEntity([], out _));
    }

    [Fact]
    public void AKindNobodyDefinedIsRefused() => Assert.False(InspectorProtocol.TryReadKind([200], out _));

    [Fact]
    public void ATruncatedEntityIsRefusedRatherThanReadPast() {
        var payload = Written(writer => InspectorProtocol.WriteEntity(writer, new(9, 4, "Camera", ["Transform"])));

        for (var length = 1; length < payload.Length; length++) {
            Assert.False(InspectorProtocol.TryReadEntity(payload.AsSpan(0, length), out _));
        }
    }

    [Fact]
    public void ATruncatedCounterIsRefused() {
        var payload = Written(writer => InspectorProtocol.WriteCounter(writer, new("fps", 60)));

        Assert.False(InspectorProtocol.TryReadCounter(payload.AsSpan(0, payload.Length - 1), out _));
    }

    /// <summary>
    ///     ⚠ A length prefix larger than the ceiling is refused before anything is allocated against
    ///     it, which is what stops a malformed or hostile message from being an out-of-memory in the
    ///     editor.
    /// </summary>
    [Fact]
    public void AStringLongerThanTheCeilingIsTruncatedOnTheWayOut() {
        var payload = Written(
            writer => InspectorProtocol.WriteText(
                writer,
                InspectorMessage.Command,
                new string('x', InspectorProtocol.MaximumStringBytes * 2)
            )
        );

        Assert.True(InspectorProtocol.TryReadText(payload, out var text));
        Assert.True(text.Length <= InspectorProtocol.MaximumStringBytes);
    }

    /// <summary>
    ///     ⚠ Truncating an over-long string in the middle of a surrogate pair leaves a lone
    ///     surrogate, which is not encodable — so the far end would read a replacement character
    ///     where somebody's entity name had one emoji in it.
    /// </summary>
    [Fact]
    public void TruncationDoesNotSplitASurrogatePair() {
        // Every character is a surrogate pair, so a cut at any even index is safe and a cut at any
        // odd index is not — which means the guard is exercised whichever way the arithmetic lands.
        var payload = Written(
            writer => InspectorProtocol.WriteText(
                writer,
                InspectorMessage.Command,
                string.Concat(Enumerable.Repeat("😀", InspectorProtocol.MaximumStringBytes))
            )
        );

        Assert.True(InspectorProtocol.TryReadText(payload, out var text));
        Assert.DoesNotContain('�', text);
        Assert.All(text.EnumerateRunes(), rune => Assert.Equal(0x1F600, rune.Value));
    }
}
