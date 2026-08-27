// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Prefabs;
using Vixen.Editor.AssetEditors.Scenes;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>What the inspector marks as overridden, and what its Revert item puts back.</summary>
/// <remarks>
///     <para>
///         <b>Doc 47 § 7's row 6.</b> The presentation — the bolded row, the enabled Revert item —
///         existed from the day the inspector was written and had never been shown a pairing, because
///         nothing assigned <c>InspectorView.Prefab</c>. These are the two decisions that had to be
///         made before it could be, and each has a test that fails if it is made the other way.
///     </para>
///     <para>
///         ⚠⚠ <b><see cref="AnOverrideToTheTemplatesOwnValueIsStillAnOverride" /> and
///         <see cref="AnOverrideToZeroRevertsFromTheInspector" /> are the pair that tells the right
///         model from the wrong one.</b> Both hold a value equal to what a comparison would call
///         "unchanged" — one equal to the template's, one equal to the type's default — so a source
///         that answered <c>IsOverridden</c> by comparing values would report them as not overridden,
///         and the revert would have nothing to do. That is model (A) of doc 47 § 3, and it is what
///         the format's list of <i>names</i> exists to avoid.
///     </para>
///     <para>
///         ⚠ <b><see cref="AChildOfAMovedInstanceIsNotOverridden" /> is the other one.</b>
///         <c>SceneEntity</c>'s position is world space and <c>SceneEntityData</c>'s is relative to
///         the parent, so a pairing that compared the two would call every child of a moved instance
///         overridden — and a revert would write a local value into a world-space setter.
///     </para>
///     <para>
///         ⚠ <b><c>SceneScalars.Register()</c> first</b>, for the reason
///         <see cref="PrefabPlacementTests" /> gives: a <c>Vector3</c> in one of these files reads
///         back as <c>(0, 0, 0)</c> unless the converter is registered before anything is written.
///     </para>
/// </remarks>
public class PrefabInspectorTests : IDisposable {
    static PrefabInspectorTests() => SceneScalars.Register();

    static readonly EntityId Source = EntityId.New();
    static readonly EntityId ChildSource = EntityId.New();

    readonly TransformSystem transforms = new();

    /// <inheritdoc />
    public void Dispose() {
        transforms.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The prefab as its file holds it: a root with a lamp, and a child one unit above it.</summary>
    static SceneFile Template(Vector3 position, float intensity) =>
        new() {
            Name = "Turret",
            Roots = [
                new() {
                    Id = Source,
                    Name = "Turret",
                    Position = position,
                    Components = [Lamp(intensity)],
                    Children = [new() { Id = ChildSource, Name = "Barrel", Position = new(0f, 1f, 0f) }]
                }
            ]
        };

    static Light Lamp(float intensity) {
        var light = Lights.Default(LightKind.Point);
        light.Intensity = intensity;

        return light;
    }

    static AssetId Publish(EditorFixture fixture, SceneFile prefab, string name = "turret") {
        var asset = AssetId.New();

        fixture.WriteAsset(
            $"Assets/{name}{SceneFile.PrefabExtension}",
            prefab.ToYaml(),
            $"guid: {asset}\nmetaVersion: 1\n"
        );

        fixture.Project.Open();

        return asset;
    }

    // ── The entity's own members ────────────────────────────────────────────────────────────────

    /// <summary>A member nothing claims is not marked, and one the instance claims is.</summary>
    [Fact]
    public void TheMarkComesFromTheClaimList() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var asset = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out var root, out _));

        var source = new PrefabSource(scene, fixture.Project.Assets);
        var target = new Placed(scene, root);

        source.Link(target, root);

        Assert.False(Field(source, target, Name).IsOverridden);

        scene.Prefabs.Mark(root, nameof(Placed.Name));

        Assert.True(Field(source, target, Name).IsOverridden);
    }

    /// <summary>⚠⚠ An override to the value the template already has is still an override, and reverts.</summary>
    /// <remarks>
    ///     <b>The test that tells the two models apart.</b> The instance's name is the template's, so
    ///     nothing about the <i>value</i> says anything at all; what says it is the claim. A source
    ///     backed by a value comparison reports "not overridden" here, the revert finds nothing to
    ///     write, and the claim stays in the file for ever — blocking every later change the template
    ///     makes to that member, silently.
    /// </remarks>
    [Fact]
    public void AnOverrideToTheTemplatesOwnValueIsStillAnOverride() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var asset = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out var root, out _));

        var source = new PrefabSource(scene, fixture.Project.Assets);
        var target = new Placed(scene, root);

        source.Link(target, root);

        // The author typed the template's own name back in, which the format says is an override and a
        // comparison cannot.
        scene.Prefabs.Mark(root, nameof(Placed.Name));
        Assert.Equal("Turret", target.Name);

        var field = Field(source, target, Name);

        Assert.True(field.IsOverridden);

        // And the revert does something, even though there is no value to write.
        Assert.True(field.RevertToPrefab());
        Assert.False(field.IsOverridden);
        Assert.Empty(scene.Prefabs.OverridesOf(root));
    }

    /// <summary>⚠ A child of a moved instance is not overridden merely because its parent moved.</summary>
    /// <remarks>
    ///     The child's world position is the sum of its own and its parent's; the template's is
    ///     neither. A pairing that called "differs from the template" an override would mark every
    ///     child of every instance anybody had ever dragged.
    /// </remarks>
    [Fact]
    public void AChildOfAMovedInstanceIsNotOverridden() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var asset = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out var root, out _));

        var child = OnlyChildOf(world, root);

        // The designer drags the whole instance twenty units along X. Nothing about the child changed.
        new Transform(world, root).LocalPosition = new(25f, 0f, 0f);
        Settle(world);

        var source = new PrefabSource(scene, fixture.Project.Assets);
        var placed = new Placed(scene, child);

        source.Link(placed, child);

        // Its world position is now (25, 1, 0) and the template says (0, 1, 0) — two different things
        // by construction, and neither is an override.
        Assert.Equal(new Vector3(25f, 1f, 0f), placed.Position);
        Assert.False(Field(source, placed, Position).IsOverridden);
    }

    /// <summary>⚠ The value a revert writes is in the space the property reads, not the file's.</summary>
    [Fact]
    public void RevertingAChildWritesAWorldSpaceValue() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var asset = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out var root, out _));

        var child = OnlyChildOf(world, root);

        new Transform(world, root).LocalPosition = new(25f, 0f, 0f);
        new Transform(world, child).LocalPosition = new(3f, 4f, 5f);
        Settle(world);

        scene.Prefabs.Mark(child, nameof(Placed.Position));

        var source = new PrefabSource(scene, fixture.Project.Assets);
        var placed = new Placed(scene, child);

        source.Link(placed, child);

        var field = Field(source, placed, Position);

        Assert.True(field.IsOverridden);
        Assert.True(field.RevertToPrefab());

        // The template's (0, 1, 0) is relative to the parent, and the parent is at (25, 0, 0). A
        // world-space value written into the setter unconverted would have landed at (0, 1, 0) and
        // put the barrel twenty-five units from the turret it belongs to.
        Assert.Equal(new Vector3(0f, 1f, 0f), new Transform(world, child).LocalPosition);

        // ⚠ Settled before the world-space read, because `WorldTransform` is a pass's output: the
        // editor's own panel reads a stale one for the rest of the frame it wrote in, which is why
        // the check that matters above is the local one.
        Settle(world);
        Assert.Equal(new Vector3(25f, 1f, 0f), placed.Position);
        Assert.False(field.IsOverridden);
    }

    /// <summary>An edit through the inspector is what records the claim.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the display is fed by nothing in a live session.</b> A freshly placed
    ///     instance claims no member, so a panel that only ever <i>read</i> the list would be correct
    ///     and permanently empty — the same "built but never fed" the pairing itself was.
    /// </remarks>
    [Fact]
    public void EditingAMemberClaimsIt() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var asset = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out var root, out _));
        Settle(world);

        var source = new PrefabSource(scene, fixture.Project.Assets);
        var target = new Placed(scene, root);

        source.Link(target, root);

        Assert.True(Field(source, target, Name).Write("Turret (west gate)"));
        Assert.True(scene.Prefabs.IsOverridden(root, nameof(Placed.Name)));

        // And it is one undo away, because a claim the author can no longer see the cause of is a
        // level saying something nobody said.
        scene.Stack.Undo();
        Assert.False(scene.Prefabs.IsOverridden(root, nameof(Placed.Name)));
    }

    /// <summary>An entity that never came from a prefab claims nothing and is never marked.</summary>
    [Fact]
    public void AnEntityFromNowhereClaimsNothing() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var loose = scene.Add("Crate", LocalTransform.Identity);

        var source = new PrefabSource(scene, fixture.Project.Assets);
        var target = new Placed(scene, loose);

        source.Link(target, loose);

        var field = Field(source, target, Name);

        Assert.True(field.Write("Crate (big)"));
        Assert.False(field.IsOverridden);
        Assert.Empty(scene.Prefabs.OverridesOf(loose));
        Assert.False(field.RevertToPrefab());
    }

    // ── A component's members ───────────────────────────────────────────────────────────────────

    /// <summary>⚠⚠ A lamp turned down to zero shows as overridden and reverts to the template's value.</summary>
    /// <remarks>
    ///     Doc 47 § 4's zero-value trap, at the layer that displays it. <c>0</c> is both the type's
    ///     default and "off", so nothing about the number distinguishes "the author turned this lamp
    ///     off" from "nobody touched it" — which is why the file records the name and why this reads
    ///     it rather than the value.
    /// </remarks>
    [Fact]
    public void AnOverrideToZeroRevertsFromTheInspector() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var asset = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out var root, out _));

        var descriptor = ReflectedDescriptor.For(typeof(Light));

        Assert.NotNull(descriptor);
        Assert.True(descriptor.TryGetMember(nameof(Light.Intensity), out var intensity));

        var source = new PrefabSource(scene, fixture.Project.Assets);

        // The box a component foldout edits, paired by the alias the format spells the path with.
        List<object> box = [world.Read<Light>(root)];

        source.Link(box[0], root, "Light");

        var field = new InspectorField(descriptor, intensity, box, null, source);

        Assert.True(field.Write(0f));
        Assert.Equal(0f, ((Light) box[0]).Intensity);

        // The value is the type's default and the row is still marked, which is the whole point.
        Assert.True(field.IsOverridden);
        Assert.True(scene.Prefabs.IsOverridden(root, "Light.Intensity"));

        Assert.True(field.RevertToPrefab());
        Assert.Equal(7f, ((Light) box[0]).Intensity);
        Assert.False(field.IsOverridden);
    }

    // ── Nesting ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>⚠ A nested node is read against the outer template, overrides included.</summary>
    /// <remarks>
    ///     Doc 47 § 7b's rule, at the inspector. The outer prefab holds its own copy of the inner one
    ///     and the outer author's overrides over it; reaching past that to the inner prefab's file
    ///     would show — and revert to — a value the level was never reconciled against.
    /// </remarks>
    [Fact]
    public void ANestedNodeIsReadAgainstTheOuterTemplate() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var inner = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        // The outer prefab holds an instance of the inner one, and its author renamed it.
        var outerFile = new SceneFile {
            Name = "Emplacement",
            Roots = [
                new() {
                    Id = EntityId.New(),
                    Name = "Emplacement",
                    Children = [
                        new() {
                            Id = EntityId.New(),
                            Name = "Turret (outer)",
                            Position = new(5f, 0f, 0f),
                            Prefab = new AssetReference(inner).ToString(),
                            Source = Source,
                            Overrides = ["Name"],
                            Components = [Lamp(7f)]
                        }
                    ]
                }
            ]
        };

        var outer = Publish(fixture, outerFile, "emplacement");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, outer, Entity.Null, out var root, out _));

        var nested = OnlyChildOf(world, root);

        Assert.True(scene.Prefabs.TryGet(nested, out var link));
        Assert.Equal(inner, link.Prefab);

        scene.Prefabs.Mark(nested, nameof(Placed.Name));

        var source = new PrefabSource(scene, fixture.Project.Assets);
        var placed = new Placed(scene, nested);

        source.Link(placed, nested);

        var field = Field(source, placed, Name);

        Assert.True(field.IsOverridden);
        Assert.True(source.TryGetPrefabValue(placed, Name, out var value));

        // The outer template's name for that node, not the inner prefab's "Turret".
        Assert.Equal("Turret (outer)", value);
    }

    /// <summary>Runs the transform pass the way a frame does.</summary>
    /// <remarks>
    ///     ⚠ <b>Resolve, then advance, and the order is the whole of it.</b> A write stamps the chunk
    ///     with the world's <i>current</i> version and <c>Resolve</c> asks for chunks newer than the one
    ///     it last saw, which it then sets to the current one — so advancing first puts the write it was
    ///     called to see one version behind the cut, and the read comes back with the value from before
    ///     the write. The frame loop advances at the end of a frame for exactly this reason.
    /// </remarks>
    void Settle(World world) {
        transforms.Resolve(world);
        world.AdvanceVersion();
    }

    /// <summary>The one child of an entity, which every instance placed here has.</summary>
    static Entity OnlyChildOf(World world, Entity entity) {
        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            return child;
        }

        throw new InvalidOperationException("The instance was placed with a child and has none.");
    }

    static InspectorField Field(PrefabSource source, Placed target, InspectorMember member) =>
        new(Described, member, [target], null, source);

    static readonly InspectorMember<Placed, string> Name = new(
        nameof(Placed.Name),
        static placed => placed.Name,
        static (placed, value) => placed.Name = value
    );

    static readonly InspectorMember<Placed, Vector3> Position = new(
        nameof(Placed.Position),
        static placed => placed.Position,
        static (placed, value) => placed.Position = value
    );

    static readonly InspectorDescriptor Described = new(typeof(Placed), [Name, Position]);

    /// <summary>An entity as a row of editors, which is what <c>SceneEntity</c> is in the shell.</summary>
    /// <remarks>
    ///     ⚠ <b>World space, exactly as <c>SceneEntity</c> reads it</b> — that is the half of this the
    ///     test is about. <c>Vixen.Editor.App</c> is not on this assembly's reference list, so the
    ///     shell's own class cannot be used here; what matters is that the property means what the
    ///     shell's means, which is the thing a pairing has to convert for.
    /// </remarks>
    sealed class Placed(SceneDocument document, Entity entity) {
        public string Name {
            get => document.NameOf(entity);
            set => document.SetName(entity, value);
        }

        public Vector3 Position {
            get => new Transform(document.World, entity).Position;
            set => new Transform(document.World, entity).Position = value;
        }
    }
}
