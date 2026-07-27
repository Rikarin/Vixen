// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace Vixen.Ecs.Tests;

public sealed class ComponentTypeTests {
    [Fact]
    public void APlainStructIsStoredInline() {
        var type = ComponentType<Position>.Info;

        Assert.False(type.IsManaged);
        Assert.False(type.IsTag);
        Assert.Equal(Unsafe.SizeOf<Position>(), type.Size);
    }

    [Fact]
    public void AClassComponentIsStoredAsAHandle() {
        var type = ComponentType<Label>.Info;

        Assert.True(type.IsManaged);
        Assert.Equal(sizeof(int), type.Size);
    }

    /// <summary>
    ///     The case that is easy to get wrong: it is a struct, it is not a class, and putting it in
    ///     a chunk would hide a reference from the garbage collector.
    /// </summary>
    [Fact]
    public void AStructContainingAReferenceIsAlsoManaged() {
        Assert.True(ComponentType<Named>.Info.IsManaged);
        Assert.Equal(sizeof(int), ComponentType<Named>.Info.Size);
    }

    [Fact]
    public void ATagCostsNoChunkMemory() {
        var type = ComponentType<Frozen>.Info;

        Assert.True(type.IsTag);
        Assert.Equal(0, type.Size);
    }

    /// <summary>
    ///     An empty struct measures one byte, so "no fields" cannot be read from the size and a tag
    ///     with a field in it would silently lose the field. This is where that stops.
    /// </summary>
    [Fact]
    public void ATagWithAFieldIsRefusedAtRegistration() {
        var failure = Assert.ThrowsAny<Exception>(() => _ = ComponentType<BrokenTag>.Id);

        // The registration runs in a static constructor, so the first touch surfaces it wrapped and
        // every later touch reports the type as uninitialised. Either way the message is reachable.
        Assert.Contains("BrokenTag", Unwrap(failure).Message, StringComparison.Ordinal);
        Assert.Contains("fields", Unwrap(failure).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdsAreDenseStableAndNeverZero() {
        var first = ComponentType<Position>.Id;
        var again = ComponentType<Position>.Id;

        Assert.Equal(first, again);
        Assert.True(first.IsValid);
        Assert.Equal(ComponentType<Position>.Info, ComponentRegistry.Get(first));
    }

    [Fact]
    public void AnUnknownIdIsRefusedRatherThanGuessed() =>
        Assert.Throws<ArgumentException>(() => ComponentRegistry.Get(new(int.MaxValue)));

    static Exception Unwrap(Exception exception) => exception is TypeInitializationException { InnerException: { } inner }
        ? inner
        : exception;

    struct BrokenTag : ITagComponent {
#pragma warning disable CS0649 // Never assigned: the point is that it exists at all.
        public int Oops;
#pragma warning restore CS0649
    }
}
