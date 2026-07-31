// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     The kind-specific facts — docs/plan/25 § 2.6. Knowing a type is a component is a label;
///     these are what make it documentation.
/// </summary>
public class FacetTests {
    static DocFacets Read(string source, string metadataName) {
        var compilation = Fixture.Compile(source);
        var type = compilation.Type(metadataName);
        var facets = Facets.For(type, Taxonomy.Of(type));

        Assert.NotNull(facets);

        return facets;
    }

    // ── Components ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The headline of doc 25: a component is not "a struct with two floats", it is eight bytes,
    ///     and eight bytes is what decides whether iterating it is cheap.
    /// </summary>
    [Fact]
    public void AComponentCarriesItsSizeAndWhatAChunkHolds() {
        var facets = Read(
            """
            [Vixen.Core.Component]
            public struct Velocity { public float X; public float Y; }
            """, "Velocity");

        Assert.Equal(8, facets.SizeBytes);
        // 16 KB / (12-byte entity + 8-byte component)
        Assert.Equal(819, facets.EntitiesPerChunk);
    }

    [Fact]
    public void PaddingIsCountedTheWayTheRuntimeCountsIt() {
        var facets = Read(
            """
            [Vixen.Core.Component]
            public struct Padded { public byte Flag; public float Value; }
            """, "Padded");

        // byte, three bytes of padding, float — and the whole thing aligned to four.
        Assert.Equal(8, facets.SizeBytes);
    }

    [Fact]
    public void ANestedStructIsMeasuredThrough() {
        var facets = Read(
            """
            public struct Position3 { public float X; public float Y; public float Z; }

            [Vixen.Core.Component]
            public struct Transform { public Position3 Translation; public float Scale; }
            """, "Transform");

        Assert.Equal(16, facets.SizeBytes);
    }

    [Fact]
    public void AnEmptyTagStillOccupiesARow() {
        var facets = Read(
            """
            [Vixen.Core.Component]
            public struct Frozen;
            """, "Frozen");

        Assert.Equal(1, facets.SizeBytes);
    }

    /// <summary>
    ///     ⚠ A size is null rather than guessed when the layout is the runtime's business. Somebody
    ///     reads this number to decide whether to split a component; a wrong one is worse than none.
    /// </summary>
    [Fact]
    public void AComponentHoldingAReferenceHasNoKnowableSize() {
        var compilation = Fixture.Compile(
            """
            [Vixen.Core.Component]
            public struct Managed { public string Name; }
            """);

        var facets = Facets.For(compilation.Type("Managed"), DocKind.Component);

        Assert.Null(facets?.SizeBytes);
    }

    // ── Systems ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASystemCarriesItsPhaseItsOrderAndWhatItTouches() {
        var facets = Read(
            """
            [Vixen.Core.Component] public struct Position { public float X; }
            [Vixen.Core.Component] public struct Velocity { public float X; }

            public sealed class InputSystem : Vixen.Ecs.Systems.ISystem { public void Dispose() { } }

            [Vixen.Ecs.Systems.UpdateInGroup(Vixen.Ecs.Systems.SystemPhase.FixedUpdate)]
            [Vixen.Ecs.Systems.UpdateAfter(typeof(InputSystem))]
            [Vixen.Ecs.Systems.Reads(typeof(Velocity))]
            [Vixen.Ecs.Systems.Writes(typeof(Position))]
            public sealed class MovementSystem : Vixen.Ecs.Systems.ISystem { public void Dispose() { } }
            """, "MovementSystem");

        Assert.Equal("FixedUpdate", facets.Phase);
        Assert.Equal(["T:Velocity"], facets.Reads!);
        Assert.Equal(["T:Position"], facets.Writes!);
        Assert.Equal(["T:InputSystem"], facets.RunsAfter!);
        Assert.Null(facets.RunsBefore);
    }

    /// <summary>
    ///     A system without `[UpdateInGroup]` lands in `Update`. That is a fact about where it runs,
    ///     not a blank, and the page should say so.
    /// </summary>
    [Fact]
    public void ASystemWithoutAPhaseRunsInUpdate() {
        var facets = Read(
            """
            public sealed class QuietSystem : Vixen.Ecs.Systems.ISystem { public void Dispose() { } }
            """, "QuietSystem");

        Assert.Equal("Update", facets.Phase);
    }

    // ── Replication ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AReplicatedComponentCarriesHowItIsSentAndWhatItCosts() {
        var facets = Read(
            """
            [Vixen.Net.Replication.Replicated(SendRate = 20, Priority = 3)]
            public struct NetworkTransform {
                [Vixen.Net.Replication.Quantize(-1024f, 1024f, 16)]
                public float X;
                public float Y;
            }
            """, "NetworkTransform");

        Assert.Equal("Unreliable", facets.Channel);
        Assert.Equal(20, facets.SendRate);
        Assert.Equal(3, facets.Priority);

        var quantized = Assert.Single(facets.Quantized!);

        Assert.Equal("X", quantized.Field);
        Assert.Equal(16, quantized.Bits);
        Assert.Equal(-1024f, quantized.Min);
    }

    /// <summary>
    ///     The defaults are a claim about behaviour — unreliable, every tick — so they are read out
    ///     rather than left blank when the author did not override them.
    /// </summary>
    [Fact]
    public void TheReplicationDefaultsAreStated() {
        var facets = Read(
            """
            [Vixen.Net.Replication.Replicated]
            public struct Health { public int Value; }
            """, "Health");

        Assert.Equal("Unreliable", facets.Channel);
        Assert.Equal(0, facets.SendRate);
    }

    // ── Everything else ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnImporterCarriesTheExtensionsItClaims() {
        var facets = Read(
            """
            [Vixen.Editor.Assets.Importer(".fbx", ".obj")]
            public sealed class ModelImporter;
            """, "ModelImporter");

        Assert.Equal([".fbx", ".obj"], facets.Extensions!);
    }

    [Fact]
    public void AGraphNodeCarriesItsMenuPath() {
        var facets = Read(
            """
            [Vixen.Editor.NodeGraph.Node("Math/Add", Summary = "Adds two numbers.")]
            public partial class AddNode;
            """, "AddNode");

        Assert.Equal("Math/Add", facets.MenuPath);
        Assert.Equal("Adds two numbers.", facets.MenuSummary);
    }

    [Fact]
    public void AnAnnotationCarriesWhatItMayBePutOn() {
        var facets = Read(
            """
            [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Property,
                AllowMultiple = true)]
            public sealed class InspectorAttribute : System.Attribute;
            """, "InspectorAttribute");

        Assert.Equal(["Field", "Property"], facets.Targets!.OrderBy(target => target, StringComparer.Ordinal));
        Assert.True(facets.AllowMultiple);
    }

    [Fact]
    public void AKindWithNothingToSayCarriesNoFacetsAtAll() {
        var compilation = Fixture.Compile("public sealed class Plain;");

        Assert.Null(Facets.For(compilation.Type("Plain"), DocKind.Class));
    }
}
