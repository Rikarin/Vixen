// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Materials;
using Xunit;

namespace Tests;

/// <summary>Reducing a material's feature tree to the four numbers a preview can shade with.</summary>
/// <remarks>
///     <para>
///         <b>What is asserted is the reduction, not the picture.</b> Whether a metal looks like metal
///         needs a device and an eye; that the metalness a material authored is the metalness that
///         reaches the instance does not, and it is the half that breaks silently — a fold that dropped
///         a feature draws a plausible surface of the wrong material, which looks like an artist's
///         choice rather than like a bug.
///     </para>
///     <para>
///         ⚠ <b>The default is the load-bearing case.</b> A viewport that could read a material had to
///         keep drawing the entities that name none exactly as it did before, or every block-out level
///         in existence would change appearance the day this landed — so
///         <see cref="MaterialSurface.Default" /> is a fully rough dielectric, which is one directional
///         term and nothing else.
///     </para>
/// </remarks>
public class MaterialSurfaceTests {
    /// <summary>Nothing to read is the neutral surface, which is the old picture exactly.</summary>
    [Fact]
    public void No_features_is_a_fully_rough_dielectric() {
        Assert.Equal(MaterialSurface.Default, MaterialSurface.Of([]));
        Assert.Equal(MaterialSurface.Default, MaterialSurface.Of((IReadOnlyList<IMaterialFeature>?) null));
        Assert.Equal(MaterialSurface.Default, MaterialSurface.Of((MaterialContent?) null));

        // ⚠ Rough and not half-rough, which is `MetalRoughnessFeature`'s own default and a different
        // question: that one is what a material with the feature and no roughness set means. This one
        // is what *no material* means, and a fully rough dielectric is the one directional term the
        // viewport drew before there was anything to read.
        Assert.Equal(1f, MaterialSurface.Default.Roughness);
        Assert.Equal(0f, MaterialSurface.Default.Metalness);
        Assert.False(MaterialSurface.Default.IsEmissive);
    }

    /// <summary>A metal-roughness feature is read straight through.</summary>
    [Fact]
    public void A_metal_roughness_feature_is_the_surface() {
        var surface = MaterialSurface.Of([
            new MetalRoughnessFeature { BaseColor = new(0.8f, 0.1f, 0.1f), Metalness = 1f, Roughness = 0.2f }
        ]);

        Assert.Equal(0.8f, surface.BaseColour.R, 4);
        Assert.Equal(0.1f, surface.BaseColour.G, 4);
        Assert.Equal(1f, surface.BaseColour.A, 4);
        Assert.Equal(1f, surface.Metalness, 4);
        Assert.Equal(0.2f, surface.Roughness, 4);
    }

    /// <summary>A textured one contributes its tint, which is what a preview can show of it.</summary>
    /// <remarks>
    ///     ⚠ <b>The tint and not the map, and that is the reduction's largest single loss.</b> A base
    ///     colour map multiplies the tint, and a material whose tint is white and whose map is a brick
    ///     comes out white here. It is still the right answer for the viewport to give — the metalness
    ///     and roughness are exact, and a white wall is closer to a brick one than the grey everything
    ///     was before.
    /// </remarks>
    [Fact]
    public void A_textured_metal_roughness_contributes_its_tint() {
        var surface = MaterialSurface.Of([
            new TexturedMetalRoughnessFeature {
                BaseColor = new(0.2f, 0.4f, 0.6f),
                Metalness = 0.5f,
                Roughness = 0.3f,
                BaseColorMap = "baseColorMap"
            }
        ]);

        Assert.Equal(0.4f, surface.BaseColour.G, 4);
        Assert.Equal(0.5f, surface.Metalness, 4);
        Assert.Equal(0.3f, surface.Roughness, 4);
    }

    /// <summary>Specular-glossiness converts, and lands on the right end at both ends.</summary>
    [Fact]
    public void Specular_glossiness_converts_to_metal_roughness() {
        var plastic = MaterialSurface.Of([
            new SpecularGlossinessFeature {
                DiffuseColor = new(0.9f, 0.9f, 0.9f),
                SpecularColor = new(0.04f, 0.04f, 0.04f),
                Glossiness = 0.25f
            }
        ]);

        // A specular colour of exactly a dielectric's is a dielectric, and glossiness is roughness
        // upside down.
        Assert.Equal(0f, plastic.Metalness, 3);
        Assert.Equal(0.75f, plastic.Roughness, 4);

        var gold = MaterialSurface.Of([
            new SpecularGlossinessFeature {
                DiffuseColor = new(0f, 0f, 0f),
                SpecularColor = new(1f, 1f, 1f),
                Glossiness = 1f
            }
        ]);

        Assert.Equal(1f, gold.Metalness, 3);
        Assert.Equal(0f, gold.Roughness, 4);
    }

    /// <summary>Emissive is added to a base workflow rather than replacing it.</summary>
    /// <remarks>
    ///     ⚠ <b>The one feature here that is not a base workflow, and the ordering trap.</b> Every other
    ///     fold overwrites, because a later base workflow is the one on top; emissive after a
    ///     metal-roughness must not take the base colour with it — a lava material is a rock that glows,
    ///     and dropping the rock would make it a white surface with a glow on it.
    /// </remarks>
    [Fact]
    public void Emissive_adds_to_the_workflow_under_it() {
        var surface = MaterialSurface.Of([
            new MetalRoughnessFeature { BaseColor = new(0.3f, 0.15f, 0.05f), Metalness = 0f, Roughness = 0.9f },
            new EmissiveFeature { EmissiveColor = new(1f, 0.4f, 0.1f), Intensity = 4f }
        ]);

        Assert.Equal(0.3f, surface.BaseColour.R, 4);
        Assert.Equal(0.9f, surface.Roughness, 4);

        // Intensity folded into the components, so nothing downstream carries a second number that has
        // to be multiplied by the first at the right moment.
        Assert.Equal(4f, surface.Emissive.R, 4);
        Assert.Equal(1.6f, surface.Emissive.G, 4);
        Assert.True(surface.IsEmissive);
    }

    /// <summary>A blend is interpolated rather than resolved to one of its sides.</summary>
    /// <remarks>
    ///     ⚠ <b>Taking either side would make a half-and-half blend of chrome and rubber look like one
    ///     of them</b>, which is worse than looking like neither: the material would appear to have a
    ///     property it does not have, at exactly the weight an author is dragging through.
    /// </remarks>
    [Fact]
    public void A_blend_interpolates_both_sides() {
        var surface = MaterialSurface.Of([
            new BlendFeature {
                Under = new MetalRoughnessFeature { BaseColor = Vector3.Zero, Metalness = 0f, Roughness = 1f },
                Over = new SpecularGlossinessFeature {
                    DiffuseColor = new(1f, 1f, 1f),
                    SpecularColor = new(1f, 1f, 1f),
                    Glossiness = 1f
                },
                Weight = 0.5f
            }
        ]);

        Assert.Equal(0.5f, surface.BaseColour.R, 3);
        Assert.Equal(0.5f, surface.Metalness, 3);
        Assert.Equal(0.5f, surface.Roughness, 3);
    }

    /// <summary>Layers are the weighted mean, and weights that sum to nothing leave the surface alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The zero case is a material an author has on screen, not a malformed one.</b> Every
    ///     layer carries a weight and a freshly added list of them is a list of zeros, which is what is
    ///     in front of somebody for as long as it takes to type the first number — a black surface
    ///     there reads as the editor having broken.
    /// </remarks>
    [Fact]
    public void Layers_are_the_weighted_mean_and_no_weight_changes_nothing() {
        var surface = MaterialSurface.Of([
            new MaterialLayersFeature {
                Layers = [
                    new(new(1f, 0f, 0f), Metalness: 1f, Roughness: 0f, Weight: 3f),
                    new(new(0f, 0f, 0f), Metalness: 0f, Roughness: 1f, Weight: 1f)
                ]
            }
        ]);

        Assert.Equal(0.75f, surface.BaseColour.R, 4);
        Assert.Equal(0.75f, surface.Metalness, 4);
        Assert.Equal(0.25f, surface.Roughness, 4);

        var unfinished = MaterialSurface.Of([
            new MaterialLayersFeature { Layers = [new(Vector3.One, 1f, 0f, Weight: 0f)] }
        ]);

        Assert.Equal(MaterialSurface.Default, unfinished);
    }

    /// <summary>A feature the reduction cannot show leaves the surface it wraps alone.</summary>
    /// <remarks>
    ///     ⚠ <b>Silently, and that is the right silence.</b> A material with a clear coat is still a
    ///     material whose base colour the viewport should draw; refusing it, or falling back to the
    ///     neutral surface, would make "this material has a feature the preview does not implement"
    ///     look identical to "this material is not assigned".
    /// </remarks>
    [Fact]
    public void A_feature_the_preview_cannot_show_is_passed_over() {
        var surface = MaterialSurface.Of([
            new MetalRoughnessFeature { BaseColor = new(0.2f, 0.6f, 0.2f), Metalness = 0f, Roughness = 0.4f },
            new NormalMapFeature { Strength = 2f },
            new ClearCoatFeature { ClearCoat = 1f },
            new SheenFeature()
        ]);

        Assert.Equal(0.6f, surface.BaseColour.G, 4);
        Assert.Equal(0.4f, surface.Roughness, 4);
    }

    /// <summary>Values outside zero-to-one are clamped rather than carried through.</summary>
    /// <remarks>
    ///     A roughness above one is a distribution the BRDF is not defined for and a negative metalness
    ///     is a negative diffuse colour — both are one bad number in a file, and both would come out as
    ///     a surface that is wrong everywhere rather than at the value that was wrong.
    /// </remarks>
    [Fact]
    public void Metalness_and_roughness_are_clamped() {
        var surface = MaterialSurface.Of([
            new MetalRoughnessFeature { BaseColor = Vector3.One, Metalness = 4f, Roughness = -1f }
        ]);

        Assert.Equal(1f, surface.Metalness, 4);
        Assert.Equal(0f, surface.Roughness, 4);
    }

    /// <summary>A compiled material is read through its feature list.</summary>
    [Fact]
    public void A_material_content_is_read_through_its_features() {
        var content = new MaterialContent {
            Features = [new MetalRoughnessFeature { BaseColor = new(0f, 0f, 1f), Metalness = 1f, Roughness = 0.1f }]
        };

        var surface = MaterialSurface.Of(content);

        Assert.Equal(1f, surface.BaseColour.B, 4);
        Assert.Equal(1f, surface.Metalness, 4);
    }

    /// <summary>The packing the instance carries puts metalness and roughness where the shader looks.</summary>
    /// <remarks>
    ///     ⚠ <b>The two reserved lanes are asserted at zero on purpose.</b> They are what a reflectance
    ///     or an occlusion strength would go in when something authors one, and a fold that quietly
    ///     started writing to <c>z</c> would be a scene shaded by a value the shader does not yet read —
    ///     until the day it does.
    /// </remarks>
    [Fact]
    public void The_packed_lanes_are_metalness_then_roughness() {
        var packed = MeshInstance.Packed(
            MaterialSurface.Default with { Metalness = 0.25f, Roughness = 0.75f }
        );

        Assert.Equal(new Vector4(0.25f, 0.75f, 0f, 0f), packed);
    }

    /// <summary>Built without a surface, an instance is the neutral one rather than a mirror.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap this exists to close.</b> Roughness lives in <c>y</c>, so a zeroed
    ///     <c>Surface</c> is a perfect specular — a caller who omits the material draws a chrome ball
    ///     where a grey one belongs, which reads as a shading bug rather than as a missing argument.
    /// </remarks>
    [Fact]
    public void An_instance_built_without_a_material_is_not_a_mirror() {
        var instance = MeshInstance.Of(Matrix4x4.Identity, Color4.White);

        Assert.Equal(1f, instance.Surface.Y);
        Assert.Equal(0f, instance.Surface.X);
        Assert.Equal(0f, instance.Emissive.R);
    }
}
