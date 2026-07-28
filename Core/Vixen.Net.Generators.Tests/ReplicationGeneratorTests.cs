// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Generated;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Xunit;

namespace Vixen.Net.Generators.Tests;

/// <summary>The generator: what it emits, what it refuses, and what it does not re-run.</summary>
public sealed class ReplicationGeneratorTests {
    const string Preamble = """
        using Vixen.Net;
        using Vixen.Net.Replication;

        namespace Subject;
        """;

    [Fact]
    public void EveryReplicatedComponentInThisAssemblyIsRegistered() {
        var registry = new ReplicationRegistry();

        // The generated registration, compiled into this test assembly by the generator running as
        // part of building it.
        ReplicatedComponents.RegisterAll(registry);

        Assert.Equal(3, registry.Count);
        Assert.NotEqual(0u, registry.ManifestHash);
        Assert.NotEqual(-1, registry.IndexOf(ReplicationRegistry.HashTypeName(typeof(GeneratedTransform).FullName!)));
    }

    [Fact]
    public void TheGeneratedReplicatorWritesExactlyWhatAHandWrittenOneWrites() {
        using var world = new World();

        var entity = world.Create(
            new GeneratedTransform {
                X = 12.5f,
                Y = -400.25f,
                Yaw = 1.5707f,
                Frame = -913,
                Team = 3,
                Grounded = true
            }
        );

        var generated = Find(typeof(GeneratedTransform));
        var handWritten = new HandWrittenTransformReplicator();

        Assert.Equal(handWritten.TypeId, generated.TypeId);
        Assert.Equal(handWritten.TypeName, generated.TypeName);
        Assert.Equal(handWritten.Channel, generated.Channel);
        Assert.Equal(handWritten.Priority, generated.Priority);
        Assert.Equal(handWritten.ComponentType, generated.ComponentType);

        // Bit for bit, not merely equivalent: the two are alternative implementations of one wire
        // format, and a wire format that differs by a bit is not a wire format.
        Assert.Equal(Encode(handWritten, world, entity), Encode(generated, world, entity));
    }

    [Fact]
    public void WhatTheGeneratedReplicatorWrites_ItCanReadBack() {
        using var server = new World();
        using var client = new World();

        var sent = new GeneratedTransform {
            X = -12.5f,
            Y = 400.25f,
            Yaw = 3.25f,
            Frame = 77,
            Team = 200,
            Grounded = false
        };

        var entity = server.Create(sent);
        var replicator = Find(typeof(GeneratedTransform));
        var bits = Encode(replicator, server, entity);

        var mirrored = client.Create();
        var reader = new BitReader(bits);

        Assert.True(replicator.Apply(client, mirrored, ref reader));

        ref readonly var got = ref client.Read<GeneratedTransform>(mirrored);
        var range = new QuantizeRange(-1000f, 1000f, 16);

        Assert.InRange(got.X, sent.X - range.MaxError, sent.X + range.MaxError);
        Assert.InRange(got.Y, sent.Y - range.MaxError, sent.Y + range.MaxError);
        Assert.Equal(sent.Yaw, got.Yaw); // not quantized, so exact
        Assert.Equal(sent.Frame, got.Frame);
        Assert.Equal(sent.Team, got.Team);
        Assert.Equal(sent.Grounded, got.Grounded);
    }

    [Fact]
    public void QuantizationIsWhatMakesTheRecordSmall() {
        using var world = new World();
        var entity = world.Create(new GeneratedTransform { X = 1, Y = 2, Yaw = 3, Frame = 4, Team = 5 });

        var bits = Encode(Find(typeof(GeneratedTransform)), world, entity);

        // 16 + 16 + 32 + 32 + 8 + 1 = 105 bits, which is fourteen bytes. Two unquantized floats
        // instead of the two quantized ones would be seventeen.
        Assert.Equal(14, bits.Length);
    }

    [Fact]
    public void AVectorAndARotationAreSentTheWayTheDocsSayTheyAre() {
        using var world = new World();

        var sent = new GeneratedPose {
            Position = new(12.5f, -400f, 3f),
            Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, 1.1f)
        };

        var entity = world.Create(sent);
        var replicator = Find(typeof(GeneratedPose));
        var bits = Encode(replicator, world, entity);

        // 48 bits of quantized position and 32 of smallest-three rotation, against the 224 the two
        // occupy in memory.
        Assert.Equal(10, bits.Length);

        using var client = new World();
        var mirrored = client.Create();
        var reader = new BitReader(bits);

        Assert.True(replicator.Apply(client, mirrored, ref reader));

        ref readonly var got = ref client.Read<GeneratedPose>(mirrored);
        var range = new QuantizeRange(-1000f, 1000f, 16);

        Assert.InRange(got.Position.X, sent.Position.X - range.MaxError, sent.Position.X + range.MaxError);
        Assert.InRange(got.Position.Y, sent.Position.Y - range.MaxError, sent.Position.Y + range.MaxError);
        Assert.Equal(sent.Rotation.Y, got.Rotation.Y, 2);
        Assert.Equal(sent.Rotation.W, got.Rotation.W, 2);
    }

    [Fact]
    public void AQuantizeOnARotation_IsAnError() {
        var (diagnostics, _) = GeneratorHarness.Run(
            $$"""
            {{Preamble}}
            using Vixen.Core.Mathematics;

            [Replicated]
            public struct Wrong {
                [Quantize(0f, 1f, 8)]
                public Quaternion Rotation;
            }
            """
        );

        // A unit quaternion's sent components are in [-1/√2, 1/√2] because they have to be, so there
        // is no range to declare and only the width would be a choice.
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET1002");
    }

    [Fact]
    public void AComponentThatSaysNothingGetsTheDefaults() {
        var score = Find(typeof(GeneratedScore));

        Assert.Equal(Channel.Unreliable, score.Channel);
        Assert.Equal(0, score.Priority);
    }

    [Fact]
    public void TheGeneratorAndTheRuntimeAgreeAboutWireIds() {
        // Two implementations of one hash, because the generator cannot reference the runtime. This
        // is the test that keeps them the same function.
        var name = typeof(GeneratedTransform).FullName!;

        Assert.Equal(ReplicationRegistry.HashTypeName(name), ReplicationGenerator.HashTypeName(name));
        Assert.Equal(ReplicationRegistry.HashTypeName(name), Find(typeof(GeneratedTransform)).TypeId);
    }

    [Fact]
    public void AFieldOfATypeThatCannotBeSent_IsAnError() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            $$"""
            {{Preamble}}

            [Replicated]
            public struct Broken {
                public string Name;
            }
            """
        );

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET1001");

        // And nothing is emitted for it: a page of errors inside code the author cannot see buries
        // the one line that is actually wrong.
        Assert.Empty(sources);
    }

    [Fact]
    public void QuantizeOnSomethingThatIsNotAFloat_IsAnError() {
        var (diagnostics, _) = GeneratorHarness.Run(
            $$"""
            {{Preamble}}

            [Replicated]
            public struct Wrong {
                [Quantize(0f, 1f, 8)]
                public int Count;
            }
            """
        );

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET1002");
    }

    [Theory]
    [InlineData("0f, 1f, 0")]
    [InlineData("0f, 1f, 33")]
    [InlineData("1f, 0f, 8")]
    public void AQuantizeRangeThatCannotBeEncodedWith_IsAnError(string arguments) {
        var (diagnostics, _) = GeneratorHarness.Run(
            $$"""
            {{Preamble}}

            [Replicated]
            public struct Wrong {
                [Quantize({{arguments}})]
                public float Value;
            }
            """
        );

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "VXNET1003");
    }

    [Fact]
    public void AComponentWithNothingInIt_IsAWarningAndNotAnError() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            $$"""
            {{Preamble}}

            [Replicated]
            public struct Empty {
            }
            """
        );

        var warning = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXNET1004");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);

        // Still emitted, because the declaration is legal and the author may be part-way through
        // writing it. An error here would stop a build over an empty struct.
        Assert.NotEmpty(sources);
    }

    [Fact]
    public void PrivateFieldsAreNotReplicated() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            $$"""
            {{Preamble}}

            [Replicated]
            public struct Partly {
                public int Sent;
                int notSent;
            }
            """
        );

        Assert.Empty(diagnostics);
        Assert.Contains(sources, source => source.Contains("value.Sent", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("notSent", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGeneratedCodeCompiles() {
        var diagnostics = GeneratorHarness.CompileWithGeneratedCode(
            $$"""
            {{Preamble}}

            [Replicated(Channel = Channel.Reliable, Priority = 3)]
            public struct Everything {
                [Quantize(-1f, 1f, 12)]
                public float Quantized;

                public float Exact;
                public int Signed;
                public uint Unsigned;
                public short Small;
                public ushort SmallUnsigned;
                public byte Tiny;
                public sbyte TinySigned;
                public bool Flag;
            }
            """
        );

        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void EditingSomethingElseReRunsNothing() {
        var reasons = GeneratorHarness.ReasonsOnSecondRun(
            $$"""
            {{Preamble}}

            [Replicated]
            public struct Watched {
                public int Value;
            }
            """
        );

        // The claim an incremental generator makes is about the second run, and this is the only way
        // to check it: adding an unrelated file must leave the per-component step's output alone.
        Assert.NotEmpty(reasons);
        Assert.All(
            reasons,
            reason => Assert.True(
                reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                $"An unrelated edit re-ran the step: {reason}."
            )
        );
    }

    static IComponentReplicator Find(Type component) {
        var registry = new ReplicationRegistry();
        ReplicatedComponents.RegisterAll(registry);

        var id = ReplicationRegistry.HashTypeName(component.FullName!);

        Assert.True(registry.TryGet(id, out var replicator));
        Assert.NotNull(replicator);

        return replicator;
    }

    static byte[] Encode(IComponentReplicator replicator, World world, Core.Entity entity) {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new BitWriter(buffer);
        replicator.Write(world, entity, ref writer);

        Assert.True(writer.TryFinish(out var bits));

        return bits.ToArray();
    }
}
