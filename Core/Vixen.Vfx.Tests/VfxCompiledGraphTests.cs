// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Vfx;
using Xunit;

namespace Vixen.Vfx.Tests;

/// <summary>
///     What compiling a graph works out, and what it refuses.
/// </summary>
/// <remarks>
///     The compiled graph is the artefact both backends read, so the things asserted here are the
///     things a GPU emitter would also depend on: that storage is derived rather than declared, that
///     salts are assigned and distinct, and that a graph which would run over uninitialised memory is
///     rejected while it is still a graph rather than after it is an effect that does nothing.
/// </remarks>
public sealed class VfxCompiledGraphTests {
    [Fact]
    public void StorageIsExactlyWhatTheOperationsTouch() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetVelocity, Vector4.Zero)
            ],
            [new(VfxOpcode.Integrate)],
            16
        );

        Assert.Equal(VfxAttribute.Position | VfxAttribute.Velocity | VfxAttribute.Identifier, graph.Attributes);

        // Nothing rotates, so nothing pays for rotation.
        Assert.False((graph.Attributes & VfxAttribute.Rotation) != 0);
        Assert.False((graph.Attributes & VfxAttribute.Colour) != 0);
    }

    [Fact]
    public void AnAttributeWithNoStorageHasNoMemory() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetPosition, Vector4.Zero)],
            [],
            16
        );

        using var buffer = new ParticleBuffer(graph.Attributes, graph.Capacity);

        Assert.False(buffer.Rotation.Length > 0, "Rotation was allocated for a graph that never mentions it.");
        Assert.True(buffer.Position.Length > 0);
    }

    [Fact]
    public void LifetimeBringsAgeWithIt() {
        // Age is the runtime's own — nothing writes it in a graph — so an updater reading it has to
        // be allowed when there is a lifetime for it to be measured against.
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetLifetime, new Vector4(1f, 1f, 0f, 0f))],
            [new(VfxOpcode.SizeOverLife, new Vector4(0f, 1f, 0f, 0f))],
            16
        );

        Assert.True((graph.Attributes & VfxAttribute.Age) != 0);
    }

    [Fact]
    public void ReadingWhatNothingWritesIsRefused() {
        // The failure this replaces is the worst kind: an effect that runs over zeroed memory, looks
        // like it does nothing, and has no symptom to search for.
        var failure = Assert.Throws<ArgumentException>(() => VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetPosition, Vector4.Zero)],
            [new(VfxOpcode.Integrate)],
            16
        ));

        Assert.Contains("Velocity", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRandomOperationGetsASaltOfItsOwn() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.SetLifetime, new Vector4(1f, 2f, 0f, 0f)),
                new(VfxOpcode.SetSize, new Vector4(1f, 2f, 0f, 0f)),
                new(VfxOpcode.SetRotation, new Vector4(0f, 1f, 0f, 0f))
            ],
            [],
            16
        );

        var salts = new HashSet<uint>();

        foreach (var operation in graph.Initializers) {
            Assert.True(operation.Salt != 0, $"{operation.Opcode} draws on randomness and was left without a salt.");
            Assert.True(salts.Add(operation.Salt), $"{operation.Opcode} shares a salt with another operation.");
        }

        Assert.Equal(3, salts.Count);
    }

    [Fact]
    public void SaltsAreFarEnoughApartToCoverAWideDraw() {
        // A position in a box draws three consecutive salts; a position in a sphere draws three too.
        // Two operations a single salt apart would have the second's first draw collide with the
        // first's second, which reads as a pattern rather than as a bug.
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-1f, -1f, -1f, 0f)) { B = new(1f, 1f, 1f, 0f) },
                new(VfxOpcode.VelocityRandomDirection, new Vector4(1f, 2f, 0f, 0f))
            ],
            [],
            16
        );

        var first = graph.Initializers[0].Salt;
        var second = graph.Initializers[1].Salt;

        Assert.True(second - first >= 3, $"Salts {first} and {second} are too close for operations that draw three values each.");
    }

    [Fact]
    public void AnAuthoredSaltIsLeftAlone() {
        // What lets a golden test pin an exact effect rather than an effect-shaped thing.
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetLifetime, 99u, new Vector4(1f, 2f, 0f, 0f), Vector4.Zero)],
            [],
            16
        );

        Assert.Equal(99u, graph.Initializers[0].Salt);
    }

    [Fact]
    public void TheCostOfAParticleIsReadable() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetVelocity, Vector4.Zero)
            ],
            [new(VfxOpcode.Integrate)],
            16
        );

        // Position and velocity at twelve bytes each, plus the identifier's four.
        Assert.Equal(28, graph.BytesPerParticle);
    }

    [Fact]
    public void ACapacityOfNothingIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => VfxCompiledGraph.Compile([], [], [], 0));
    }
}
