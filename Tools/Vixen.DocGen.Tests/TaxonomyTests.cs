// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     One test per rule in docs/plan/25 § 2.3, because the taxonomy is the claim the whole document
///     rests on: that what a type <em>is</em> can be derived rather than maintained.
/// </summary>
public class TaxonomyTests {
    [Fact]
    public void ComponentAttributeMakesAComponent() {
        var compilation = Fixture.Compile(
            """
            [Vixen.Core.Component]
            public struct Velocity { public float X; }
            """);

        Assert.Equal(DocKind.Component, Taxonomy.Of(compilation.Type("Velocity")));
    }

    /// <summary>
    ///     The pair is its own kind: `[Component]` says the ECS may attach it, `[DataContract]` says
    ///     it can be described and serialised, and only both together put a type in the Add Component
    ///     menu and into a `.vxscene`.
    /// </summary>
    [Fact]
    public void ComponentWithDataContractIsASceneComponent() {
        var compilation = Fixture.Compile(
            """
            [Vixen.Core.Component]
            [Vixen.Core.DataContract]
            public struct MeshRenderer { public int Mesh; }
            """);

        Assert.Equal(DocKind.SceneComponent, Taxonomy.Of(compilation.Type("MeshRenderer")));
    }

    [Fact]
    public void DataContractAloneIsNotAComponent() {
        var compilation = Fixture.Compile(
            """
            [Vixen.Core.DataContract]
            public sealed class Settings { public int Value; }
            """);

        Assert.Equal(DocKind.Class, Taxonomy.Of(compilation.Type("Settings")));
    }

    [Fact]
    public void ImplementingISystemMakesASystem() {
        var compilation = Fixture.Compile(
            """
            public sealed class MovementSystem : Vixen.Ecs.Systems.ISystem {
                public void Dispose() { }
            }
            """);

        Assert.Equal(DocKind.System, Taxonomy.Of(compilation.Type("MovementSystem")));
    }

    /// <summary>Deriving `SystemBase` is the usual way, and it reaches the same rule transitively.</summary>
    [Fact]
    public void DerivingSystemBaseMakesASystem() {
        var compilation = Fixture.Compile(
            """
            public sealed class RenderSystem : Vixen.Ecs.Systems.SystemBase;
            """);

        Assert.Equal(DocKind.System, Taxonomy.Of(compilation.Type("RenderSystem")));
    }

    [Fact]
    public void DerivingBehaviorMakesABehavior() {
        var compilation = Fixture.Compile(
            """
            public sealed class PlayerController : Vixen.Engine.Behaviors.Behavior;
            """);

        Assert.Equal(DocKind.Behavior, Taxonomy.Of(compilation.Type("PlayerController")));
    }

    [Fact]
    public void ReplicatedAttributeMakesAReplicatedComponent() {
        var compilation = Fixture.Compile(
            """
            [Vixen.Net.Replication.Replicated]
            public struct NetworkTransform { public float X; }
            """);

        Assert.Equal(DocKind.ReplicatedComponent, Taxonomy.Of(compilation.Type("NetworkTransform")));
    }

    [Fact]
    public void NodeAttributeMakesAGraphNode() {
        var compilation = Fixture.Compile(
            """
            [Vixen.Editor.NodeGraph.Node("Math/Add")]
            public partial class AddNode;
            """);

        Assert.Equal(DocKind.GraphNode, Taxonomy.Of(compilation.Type("AddNode")));
    }

    [Fact]
    public void ImporterAttributeMakesAnImporter() {
        var compilation = Fixture.Compile(
            """
            [Vixen.Editor.Assets.Importer(".fbx", ".obj")]
            public sealed class ModelImporter;
            """);

        Assert.Equal(DocKind.Importer, Taxonomy.Of(compilation.Type("ModelImporter")));
    }

    [Fact]
    public void DerivingAttributeMakesAnAnnotation() {
        var compilation = Fixture.Compile(
            """
            public sealed class TooltipAttribute(string text) : System.Attribute {
                public string Text { get; } = text;
            }
            """);

        Assert.Equal(DocKind.Annotation, Taxonomy.Of(compilation.Type("TooltipAttribute")));
    }

    [Fact]
    public void ControlsNamespaceMakesAUiControl() {
        var compilation = Fixture.Compile(
            """
            namespace Vixen.Ui.Controls {
                public sealed class Button;
            }
            """);

        Assert.Equal(DocKind.UiControl, Taxonomy.Of(compilation.Type("Vixen.Ui.Controls.Button")));
    }

    /// <summary>
    ///     ⚠ The most-specific-first ordering is a property of the rules, not an accident of how they
    ///     were written: a scene component in the controls namespace is a scene component.
    /// </summary>
    [Fact]
    public void SpecificRulesWinOverPositionalOnes() {
        var compilation = Fixture.Compile(
            """
            namespace Vixen.Ui.Controls {
                [Vixen.Core.Component]
                [Vixen.Core.DataContract]
                public struct CanvasTag { public int Value; }
            }
            """);

        Assert.Equal(DocKind.SceneComponent, Taxonomy.Of(compilation.Type("Vixen.Ui.Controls.CanvasTag")));
    }

    // The kinds travel as their slugs rather than as DocKind values: the enum is internal to the
    // tool, and a public test signature cannot name it.
    [Theory]
    [InlineData("public sealed class Plain;", "Plain", "class")]
    [InlineData("public struct Point { public int X; }", "Point", "struct")]
    [InlineData("public interface IThing { }", "IThing", "interface")]
    [InlineData("public enum Mode { A }", "Mode", "enum")]
    [InlineData("public delegate void Handler();", "Handler", "delegate")]
    public void EverythingElseFallsBackToItsTypeKind(string source, string name, string expected) =>
        Assert.Equal(expected, Taxonomy.Slug(Taxonomy.Of(Fixture.Compile(source).Type(name))));

    [Theory]
    [InlineData(nameof(DocKind.SceneComponent), "scene-component")]
    [InlineData(nameof(DocKind.ReplicatedComponent), "replicated-component")]
    [InlineData(nameof(DocKind.UiControl), "ui-control")]
    [InlineData(nameof(DocKind.GraphNode), "graph-node")]
    [InlineData(nameof(DocKind.Component), "component")]
    [InlineData(nameof(DocKind.Class), "class")]
    public void SlugsAreTheKebabCasedFormTheSiteFiltersOn(string kind, string expected) =>
        Assert.Equal(expected, Taxonomy.Slug(Enum.Parse<DocKind>(kind)));
}
