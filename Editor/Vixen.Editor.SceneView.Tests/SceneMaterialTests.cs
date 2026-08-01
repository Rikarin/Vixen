// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Materials;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>An entity's material reaches the instance the viewport draws it with.</summary>
/// <remarks>
///     <para>
///         <b>The viewport shaded every surface in one grey with one directional term, and the material
///         an entity named was invisible.</b> Assigning one and seeing nothing happen is the shape of
///         defect that sends an author to the game to find out whether the assignment took — so what is
///         asserted here is the join: a reference on the entity, a surface out of the source, and the
///         lanes on the instance the shader reads.
///     </para>
///     <para>
///         ⚠ <b>The other half is that entities <em>without</em> a material did not change.</b> Every
///         block-out level in existence would have shifted appearance the day this landed if the
///         neutral surface were anything but a fully rough dielectric, so that case has a test of its
///         own and it is the one that matters most.
///     </para>
/// </remarks>
public class SceneMaterialTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-material-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    static readonly AssetReference Brick = new(new AssetId(new("33333333-3333-3333-3333-333333333333")));
    static readonly AssetReference Chrome = new(new AssetId(new("44444444-4444-4444-4444-444444444444")));
    static readonly AssetReference Missing = new(new AssetId(new("55555555-5555-5555-5555-555555555555")));

    public SceneMaterialTests() {
        Directory.CreateDirectory(root);
        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    /// <summary>A shape's material decides the colour it is drawn in and how it is shaded.</summary>
    [Fact]
    public void A_shapes_material_is_its_colour_and_its_shading() {
        Shape(PrimitiveKind.Cube, Brick, Vector3.Zero);

        var meshes = new SceneMeshes { Surfaces = new Surfaces() };

        Assert.Equal(1, meshes.Build(scene));

        var instance = meshes.Instances[0];

        Assert.Equal(0.6f, instance.Colour.R, 4);
        Assert.Equal(0.2f, instance.Colour.G, 4);
        Assert.Equal(0f, instance.Surface.X, 4);
        Assert.Equal(0.8f, instance.Surface.Y, 4);
    }

    /// <summary>A mesh entity's material reaches it the same way a shape's does.</summary>
    /// <remarks>
    ///     Both, because they are two components with a <c>Material</c> field each and the collector
    ///     reads them on two different branches — a change to one that missed the other would be a
    ///     viewport where block-out walls take their material and imported meshes do not.
    /// </remarks>
    [Fact]
    public void A_mesh_entitys_material_reaches_it_too() {
        var entity = scene.CreateShape(PrimitiveKind.Cube, LocalTransform.At(Vector3.Zero));

        world.Remove<PrimitiveShape>(entity);

        MeshRenderables.Attach(
            world,
            entity,
            MeshRenderables.Default(new(new AssetId(new("66666666-6666-6666-6666-666666666666")))) with {
                Material = Chrome
            }
        );

        var meshes = new SceneMeshes { Meshes = new Meshes(), Surfaces = new Surfaces() };

        meshes.Build(scene);

        Assert.Equal(1f, meshes.Instances[0].Surface.X, 4);
        Assert.Equal(0.05f, meshes.Instances[0].Surface.Y, 4);
    }

    /// <summary>
    ///     ⚠ <b>Without a material, an entity is drawn exactly as it was before materials existed.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The shape colour and a fully rough dielectric, which is one directional term. Both halves
    ///         are asserted because either alone would let the picture move: the colour without the
    ///         surface would be a grey mirror, and the surface without the colour would be a white matte
    ///         cube.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Plus the block-out checker, which is doc 24's P5 and is what an entity with no
    ///         material now draws.</b> That is a deliberate change to "the picture it always was" and it
    ///         is the whole point of the phase: grey on grey is what makes a block-out unreadable, and
    ///         squares of a fixed size in metres are what make proportion something you count. An entity
    ///         that <i>has</i> a material still draws it — see the tests above.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Without_a_material_the_picture_is_the_one_it_always_was() {
        Shape(PrimitiveKind.Cube, AssetReference.Null, Vector3.Zero);

        var meshes = new SceneMeshes { Surfaces = new Surfaces() };

        meshes.Build(scene);

        var instance = meshes.Instances[0];

        Assert.Equal(meshes.ShapeColour, instance.Colour);
        Assert.Equal(MeshInstance.Packed(MaterialSurface.Default, meshes.Checker, meshes.AxisTint), instance.Surface);
        Assert.Equal(0f, instance.Emissive.R, 4);
    }

    /// <summary>And with no source at all, which is a host that has wired none.</summary>
    [Fact]
    public void Without_a_source_every_entity_is_the_neutral_surface() {
        Shape(PrimitiveKind.Sphere, Brick, Vector3.Zero);

        var meshes = new SceneMeshes();

        meshes.Build(scene);

        Assert.Equal(meshes.ShapeColour, meshes.Instances[0].Colour);
        Assert.Equal(MeshInstance.Packed(MaterialSurface.Default, meshes.Checker, meshes.AxisTint), meshes.Instances[0].Surface);
    }

    /// <summary>A material the source has never heard of is neutral rather than absent.</summary>
    /// <remarks>
    ///     ⚠ <b>The asymmetry with a missing <em>mesh</em>, which draws nothing.</b> A mesh that has not
    ///     arrived would otherwise make an entity's shape a function of disk speed; a material that has
    ///     not arrived costs only its colour, and an entity that vanished while its material was read
    ///     would make opening a project look like opening an empty level.
    /// </remarks>
    [Fact]
    public void An_unresolvable_material_is_drawn_neutrally_rather_than_skipped() {
        Shape(PrimitiveKind.Cube, Missing, Vector3.Zero);

        var meshes = new SceneMeshes { Surfaces = new Surfaces() };

        Assert.Equal(1, meshes.Build(scene));
        Assert.Equal(meshes.ShapeColour, meshes.Instances[0].Colour);
        Assert.Equal(MeshInstance.Packed(MaterialSurface.Default, meshes.Checker, meshes.AxisTint), meshes.Instances[0].Surface);
    }

    /// <summary>Emission arrives on the instance with the material's intensity already in it.</summary>
    [Fact]
    public void An_emissive_material_lights_its_own_instance() {
        Shape(PrimitiveKind.Sphere, Brick, Vector3.Zero);

        var meshes = new SceneMeshes { Surfaces = new Surfaces() };

        meshes.Build(scene);

        // Brick is authored with a dim glow at an intensity of two — see `Surfaces` below.
        Assert.Equal(0.5f, meshes.Instances[0].Emissive.R, 4);
        Assert.Equal(0f, meshes.Instances[0].Emissive.B, 4);
    }

    /// <summary>Selecting something takes its colour and leaves the rest of its material alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The rule worth arguing with, so it is written down as a test.</b> A selected object
    ///     drawn in its own colours would be identified only by its rim, which some show-flag
    ///     combinations turn off — so selection keeps the amber. What it does <i>not</i> take is the
    ///     shading: an amber object that is still visibly rough or polished says which one is selected
    ///     without also lying about what it is made of.
    /// </remarks>
    [Fact]
    public void Selection_takes_the_colour_and_not_the_shading() {
        var entity = Shape(PrimitiveKind.Cube, Chrome, Vector3.Zero);

        scene.Selection.Set(entity);

        var meshes = new SceneMeshes { Surfaces = new Surfaces() };

        meshes.Build(scene);

        var surface = meshes.Instances[0];

        Assert.Equal(meshes.SelectedColour, surface.Colour);
        Assert.Equal(1f, surface.Surface.X, 4);
    }

    /// <summary>
    ///     ⚠ <b>The outline and the wires are drawn with the neutral surface, whatever the entity is.</b>
    /// </summary>
    /// <remarks>
    ///     Both are lit flat, which the shader does by handing them a normal that faces the key — a
    ///     trick that survives a BRDF only because a fully rough dielectric's specular lobe is worth
    ///     about two per cent of its colour. A rim that inherited a chrome material would be a selection
    ///     outline with a highlight sliding along it as the camera moved, which is the one failure mode
    ///     an outline must not have.
    /// </remarks>
    [Fact]
    public void The_outline_and_the_wires_ignore_the_material() {
        var entity = Shape(PrimitiveKind.Cube, Chrome, Vector3.Zero);

        scene.Selection.Set(entity);

        using var pane = new Pane();

        pane.Viewport.Modes.Current = ViewMode.ShadedWireframe;

        var meshes = new SceneMeshes { Surfaces = new Surfaces() };

        meshes.Build(scene, pane.Viewport);

        // The surface, its outline and its wires: three instances of one entity.
        Assert.Equal(3, meshes.Instances.Length);

        var neutral = MeshInstance.Packed(MaterialSurface.Default);

        foreach (var instance in meshes.Instances) {
            var flat = instance.Style.Z > 0.5f;

            Assert.Equal(flat ? neutral : new Vector4(1f, 0.05f, 0f, 0f), instance.Surface);

            if (flat) {
                Assert.Equal(0f, instance.Emissive.R, 4);
            }
        }
    }

    /// <summary>The roughness view is a greyscale of the roughness, shaded by nothing.</summary>
    /// <remarks>
    ///     ✅ <b>A mode that used to be declared-and-disabled.</b> <c>ViewShading.IsSupported</c> refused
    ///     it because "roughness needs a material to read one off, and there are none" — which stopped
    ///     being true. It costs a colour at collect time and no shader at all, because a roughness is
    ///     one number per entity where a normal is one per vertex.
    /// </remarks>
    [Fact]
    public void The_roughness_view_draws_the_roughness_and_is_not_shaded_by_it() {
        Shape(PrimitiveKind.Cube, Brick, Vector3.Zero);

        Assert.True(ViewShading.IsSupported(ViewMode.Roughness));

        using var pane = new Pane();

        pane.Viewport.Modes.Current = ViewMode.Roughness;

        var meshes = new SceneMeshes { Surfaces = new Surfaces() };

        meshes.Build(scene, pane.Viewport);

        var instance = meshes.Instances[0];

        Assert.Equal(0.8f, instance.Colour.R, 4);
        Assert.Equal(0.8f, instance.Colour.G, 4);
        Assert.Equal(0.8f, instance.Colour.B, 4);

        // ⚠ And the surface is neutral, which is the half that would be easy to leave out: shading a
        // roughness view by the roughness is the number multiplied by a picture of itself.
        Assert.Equal(MeshInstance.Packed(MaterialSurface.Default), instance.Surface);
        Assert.Equal(1f, ViewShading.AmbientFor(ViewMode.Roughness, 0.35f));
    }

    /// <summary>A normal view is not shaded by the material either.</summary>
    [Fact]
    public void The_normal_view_is_not_shaded_by_the_material() {
        Shape(PrimitiveKind.Sphere, Chrome, Vector3.Zero);

        using var pane = new Pane();

        pane.Viewport.Modes.Current = ViewMode.Normal;

        var meshes = new SceneMeshes { Surfaces = new Surfaces() };

        meshes.Build(scene, pane.Viewport);

        Assert.Equal(MeshInstance.Packed(MaterialSurface.Default), meshes.Instances[0].Surface);
    }

    /// <summary>One material asked for once, however many entities name it.</summary>
    /// <remarks>
    ///     ⚠ <b>And the cache survives the frame</b>, which is what keeps a scene of a thousand
    ///     unmaterialled crates from walking an import cache a thousand times a frame. What makes that
    ///     safe is <c>Invalidate</c>, which an import finishing already calls — asserted below, because
    ///     a cache nothing clears is a viewport that draws yesterday's material for ever.
    /// </remarks>
    [Fact]
    public void A_material_is_read_once_and_forgotten_on_invalidate() {
        Shape(PrimitiveKind.Cube, Brick, Vector3.Zero);
        Shape(PrimitiveKind.Sphere, Brick, new(3f, 0f, 0f));
        Shape(PrimitiveKind.Cone, Brick, new(6f, 0f, 0f));

        var source = new Surfaces();
        var meshes = new SceneMeshes { Surfaces = source };

        meshes.Build(scene);
        meshes.Build(scene);

        Assert.Equal(1, source.Asked);

        meshes.Invalidate();
        meshes.Build(scene);

        Assert.Equal(2, source.Asked);
    }

    Entity Shape(PrimitiveKind kind, AssetReference material, Vector3 at) {
        var entity = scene.CreateShape(kind, LocalTransform.At(at));

        world.Get<PrimitiveShape>(entity).Material = material;

        return entity;
    }

    /// <summary>Two materials, one of each extreme, and nothing else resolves.</summary>
    sealed class Surfaces : ISurfaceSource {
        public int Asked { get; private set; }

        public bool TryGet(AssetReference reference, out MaterialSurface surface) {
            Asked++;
            surface = MaterialSurface.Default;

            if (reference == Brick) {
                surface = new(new Color4(0.6f, 0.2f, 0.1f, 1f), Metalness: 0f, Roughness: 0.8f, new(0.5f, 0.2f, 0f));
                return true;
            }

            if (reference == Chrome) {
                surface = new(new Color4(0.9f, 0.9f, 0.9f, 1f), Metalness: 1f, Roughness: 0.05f, Color3.Black);
                return true;
            }

            return false;
        }
    }

    /// <summary>Any reference is a triangle, which is enough for a mesh entity to be drawn at all.</summary>
    sealed class Meshes : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = new() {
                Name = "mesh",
                Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
                Normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)],
                TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)],
                Indices = [0, 1, 2]
            };

            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
