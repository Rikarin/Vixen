// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Tests;

/// <summary>
///     The <c>!StandardFrame</c> expansion: the graph each knob combination stands for.
/// </summary>
/// <remarks>
///     <para>
///         Structural tests over the expanded <em>asset</em>, deliberately — no device, no build.
///         The expansion's whole promise is that the same knobs produce the same document, so what
///         is asserted is the document: node presence and order, resource extents against the nodes'
///         own arithmetic, seat lines against produced planes. The one build test at the bottom is
///         the seam test — that a document whose <c>game:</c> is the preset node builds through the
///         ordinary factory registration and nothing downstream ever meets the node.
///     </para>
///     <para>
///         The full-configuration order is asserted against a hardcoded list rather than against
///         sample 13's file, because the two must be allowed to diverge deliberately (the sample is
///         hand-authored and art-directed) and never accidentally (this list is the review gate).
///     </para>
/// </remarks>
public class StandardFrameTests {
    /// <summary>Every knob at its floor, which is the bare frame: background, scene, curve.</summary>
    static StandardFrameAsset AllOff => new() {
        Quality = QualityTier.Low,
        Shadows = ShadowMode.Off,
        Gi = GiMode.Off,
        Reflections = ReflectionsMode.Off,
        Antialiasing = AntialiasingMode.Off,
        Exposure = ExposureMode.Fixed,
        Particles = false
    };

    /// <summary>Every knob at its ceiling — the sample-13 configuration.</summary>
    static StandardFrameAsset AllOn => new() {
        Quality = QualityTier.Epic,
        Shadows = ShadowMode.Virtual,
        Gi = GiMode.Probes,
        Reflections = ReflectionsMode.Screen,
        Antialiasing = AntialiasingMode.TaaFxaa,
        Exposure = ExposureMode.Automatic,
        Particles = true
    };

    static GraphicsCompositorAsset Expand(StandardFrameAsset frame, GraphicsCompositorAsset? document = null) =>
        StandardFrame.Expand((document ?? new()) with { Game = frame });

    static SequenceAsset Root(GraphicsCompositorAsset document) => Assert.IsType<SequenceAsset>(document.Game);

    static string[] Names(GraphicsCompositorAsset document) =>
        [.. Root(document).Children.Select(child => child.Name)];

    static TAsset Node<TAsset>(GraphicsCompositorAsset document, string name) =>
        Assert.IsType<TAsset>(Root(document).Children.Single(child => child.Name == name));

    [Fact]
    public void All_off_is_sky_main_tonemap_and_nothing_else() {
        var document = Expand(AllOff);

        Assert.Equal(["Sky", "Main", "Tonemap"], Names(document));

        // No split, so the one colour target; no shadows, so no seats published into set 0.
        var main = Node<RenderPassAsset>(document, "Main");

        Assert.Equal(["SceneHdr"], main.ColourTargets);
        Assert.Equal(["SceneHdr"], main.Loaded);
        Assert.Empty(main.SceneTextures);

        // With nothing after the curve, the tonemap itself writes the output resource.
        var tonemap = Node<TonemapAsset>(document, "Tonemap");

        Assert.Equal("SceneHdr", tonemap.Source);
        Assert.Equal("SceneColour", tonemap.Output);
        Assert.Equal("", tonemap.Bloom);
        Assert.Equal("", tonemap.ExposureBuffer);

        // Fixed exposure is the camera's: the lens decides until a look profile pins an EV.
        Assert.Equal("Camera", tonemap.View);

        Assert.Equal(["Opaque"], document.Stages.Select(stage => stage.Name));
        Assert.Equal(["SceneColour", "SceneHdr", "SceneDepth"], document.Resources.Select(resource => resource.Name));
    }

    [Fact]
    public void Cascades_add_the_shadow_pass_the_seats_and_an_atlas_of_the_nodes_own_arithmetic() {
        var document = Expand(AllOff with { Shadows = ShadowMode.Cascades });

        Assert.Equal(["Sun", "Lamps", "Sky", "Main", "Tonemap"], Names(document));

        // The caster stage carries the structural decisions, not the artistic ones: back faces,
        // zero raster bias, clamped depth, the depth-only override shader without composition.
        var caster = document.Stages.Single(stage => stage.Name == "Shadow");

        Assert.Equal("ShadowCaster", caster.Shader);
        Assert.Equal(CullMode.Back, caster.Cull);
        Assert.Equal(0f, caster.DepthBias);
        Assert.Equal(0f, caster.DepthBiasSlope);
        Assert.True(caster.DepthClamp);
        Assert.False(caster.ComposeFromMaterial);

        // The extents are the nodes' arithmetic applied to the nodes' own knobs — the guard the
        // audit paid for. Any literal here would be the 8192×2048 mistake waiting to recur.
        var sun = Node<ShadowMapAsset>(document, "Sun");
        var atlas = document.Resources.Single(resource => resource.Name == sun.Atlas);
        var expected = ShadowCascades.AtlasSize(sun.CascadeCount, sun.Resolution);

        Assert.Equal(expected, new Int2(atlas.Width, atlas.Height));

        var lamps = Node<PunctualShadowAsset>(document, "Lamps");
        var punctual = document.Resources.Single(resource => resource.Name == lamps.Atlas);

        Assert.Equal(lamps.TilesPerSide * lamps.Resolution, punctual.Width);
        Assert.Equal(punctual.Width, punctual.Height);

        // And the atlas the lamps fill is published under the compose slot's qualified name.
        Assert.Equal(["ForwardPlus.PunctualShadowAtlas"], lamps.Passes);

        // The consuming pass publishes both atlases into set 0 — the seat lines whose absence is
        // four cascades drawn into an atlas nothing samples.
        var main = Node<RenderPassAsset>(document, "Main");

        Assert.Equal(
            [("shadowMap", sun.Atlas), ("PunctualShadowAtlas.atlas", lamps.Atlas)],
            main.SceneTextures.Select(seat => (seat.Binding, seat.Resource))
        );

        Assert.Equal("Camera", sun.View);
    }

    [Fact]
    public void Virtual_shadows_keep_the_cascades_as_the_maps_fall_through() {
        var document = Expand(AllOff with { Shadows = ShadowMode.Virtual });

        // The A/B, not a swap: the map shades where it has a drawn page, the cascades everywhere
        // else — so both nodes are in the frame, and the map sits after the finished depth.
        Assert.Equal(["Sun", "Lamps", "Sky", "Main", "SunPages", "Tonemap"], Names(document));

        var pages = Node<VirtualShadowAsset>(document, "SunPages");

        Assert.Equal("SceneDepth", pages.Depth);
        Assert.Equal(["ForwardPlus.VirtualShadowLookup"], pages.Passes);
    }

    [Fact]
    public void Probes_add_the_gather_and_a_combine_whose_every_seat_names_a_produced_plane() {
        var document = Expand(AllOff with { Gi = GiMode.Probes, Quality = QualityTier.High });

        var main = Node<RenderPassAsset>(document, "Main");
        var gather = Node<ScreenProbeGatherAsset>(document, "Gather");
        var occlusion = Node<DistanceFieldAoAsset>(document, "Occlusion");
        var contact = Node<SsaoAsset>(document, "ContactOcclusion");
        var combine = Node<AmbientCombineAsset>(document, "Combine");

        // The split: three targets in the shader's order, and the combine reads the same names —
        // one contract seen from both ends.
        Assert.Equal(["SceneHdr", "SceneAlbedo", "SceneNormals"], main.ColourTargets);
        Assert.Equal(main.ColourTargets[0], combine.Direct);
        Assert.Equal(main.ColourTargets[1], combine.Albedo);
        Assert.Equal(main.ColourTargets[2], combine.Normals);

        // Every optional seat names the plane the node above it publishes, never a guess.
        Assert.Equal(gather.Output, combine.Irradiance);
        Assert.Equal(occlusion.Output, combine.Occlusion);
        Assert.Equal(contact.Output, combine.ContactOcclusion);
        Assert.Equal("", combine.Reflections);

        // The gather's host-side placement copies depth and normals back every frame, so the two
        // planes carry CopySource — without it the copy is a validation error per frame.
        Assert.True(
            document.Resources.Single(resource => resource.Name == "SceneDepth").Usage
                .HasFlag(TextureUsage.CopySource)
        );

        Assert.True(
            document.Resources.Single(resource => resource.Name == "SceneNormals").Usage
                .HasFlag(TextureUsage.CopySource)
        );

        // One sun shadow, and the cascades own it here: gi Off kept the march's sun cone off in
        // this configuration too, because shadows are on.
        var shadowed = Expand(AllOff with { Gi = GiMode.Probes, Shadows = ShadowMode.Cascades });

        Assert.False(Node<DistanceFieldAoAsset>(shadowed, "Occlusion").SunShadow);
        Assert.True(occlusion.SunShadow);
    }

    [Fact]
    public void Ambient_gi_runs_the_occlusion_pair_without_the_probe_machinery() {
        var document = Expand(AllOff with { Gi = GiMode.Ambient });

        Assert.Equal(
            ["Clipmap", "Sky", "Main", "Occlusion", "ContactOcclusion", "Combine", "Tonemap"],
            Names(document)
        );

        // No gather, so nothing reads the planes back — and the combine's irradiance seat stays
        // empty rather than naming a resource with no producer.
        Assert.Equal("", Node<AmbientCombineAsset>(document, "Combine").Irradiance);

        Assert.False(
            document.Resources.Single(resource => resource.Name == "SceneDepth").Usage
                .HasFlag(TextureUsage.CopySource)
        );
    }

    [Fact]
    public void Screen_reflections_land_in_the_combines_validity_seat() {
        var document = Expand(AllOff with { Reflections = ReflectionsMode.Screen });

        var mirrors = Node<ReflectionsAsset>(document, "Mirrors");
        var combine = Node<AmbientCombineAsset>(document, "Combine");

        // The trace answers into its own plane and the combine blends it by validity — the only
        // compositor the plane has, which is why reflections imply the split even with gi off.
        Assert.Equal(mirrors.Target, combine.Reflections);
        Assert.Equal("SceneHdr", mirrors.Colour);
        Assert.Equal("", combine.Irradiance);
        Assert.Equal("", combine.Occlusion);
    }

    [Fact]
    public void Taa_pays_for_the_velocity_pass_and_runs_before_the_atmosphere() {
        var document = Expand(AllOff with { Antialiasing = AntialiasingMode.Taa, Quality = QualityTier.High });

        var names = Names(document);

        // The velocity pass exists exactly because TAA does, shares Main's depth read-only, and
        // draws the Motion stage the host's CasterStages must include.
        var velocity = Node<RenderPassAsset>(document, "Velocity");

        Assert.Equal(["SceneMotion"], velocity.ColourTargets);
        Assert.Equal(LoadAction.Load, velocity.DepthLoad);
        Assert.True(velocity.ReadOnlyDepth);
        Assert.Equal("Motion", Assert.IsType<SingleStageAsset>(velocity.Children.Single()).Stage);

        var motion = document.Stages.Single(stage => stage.Name == "Motion");

        Assert.Equal("MotionVectors", motion.Shader);
        Assert.Equal(DepthPreset.TestOnly, motion.Depth);

        // The accumulator sees the finished frame and only the finished frame: after Main, before
        // the fog — averaging half-fogged radiance is a different image in the history.
        Assert.True(Array.IndexOf(names, "Accumulate") < Array.IndexOf(names, "Air"));

        var taa = Node<TemporalAntialiasingAsset>(document, "Accumulate");

        Assert.Equal("SceneHdr", taa.Source);
        Assert.Equal("SceneMotion", taa.MotionVectors);

        // Sharpen rides the TAA knob — it recovers what the resolve softened — and FXAA is absent
        // in plain Taa mode.
        Assert.Contains("Recover", names);
        Assert.DoesNotContain("Edges", names);
    }

    [Fact]
    public void Automatic_exposure_wires_the_meter_into_the_tonemap_by_its_buffer_name() {
        var document = Expand(AllOff with { Exposure = ExposureMode.Automatic });

        var tonemap = Node<TonemapAsset>(document, "Tonemap");

        // The pairing is structural: the buffer's name is the meter node's own plus `.Exposure`,
        // and a metered frame must not also hand the lens a vote.
        Assert.Equal("Meter.Exposure", tonemap.ExposureBuffer);
        Assert.Equal("", tonemap.View);
        Assert.True(Node<AutoExposureAsset>(document, "Meter").UseHistogram);
    }

    /// <summary>The ceiling configuration is sample 13's graph, in sample 13's order.</summary>
    /// <remarks>
    ///     A hardcoded list, not a read of the sample file: the sample is hand-authored and
    ///     art-directed and may diverge deliberately; this list is what makes an accidental
    ///     reordering — the audit's silent killers were all orderings — a red test.
    /// </remarks>
    [Fact]
    public void The_full_configuration_reproduces_the_sample_thirteen_sequence() {
        var document = Expand(AllOn);

        Assert.Equal(
            [
                "Cull", "Clipmap", "Probes", "Cache", "Sun", "Lamps", "Sky", "Main", "Velocity",
                "Sparks", "Occluders", "SunPages", "Gather", "Mirrors", "Occlusion",
                "ContactOcclusion", "Combine", "Accumulate", "Air", "Defocus", "Shutter", "Meter",
                "Adapt", "Flare", "Glow", "Tonemap", "Edges", "Recover", "Glass"
            ],
            Names(document)
        );

        // The colour chain is contiguous: each transforming node reads what the previous one
        // wrote, the pyramid rides beside it, and the last node writes the output resource.
        Assert.Equal("SceneCombined", Node<AmbientCombineAsset>(document, "Combine").Output);
        Assert.Equal("SceneCombined", Node<TemporalAntialiasingAsset>(document, "Accumulate").Source);
        Assert.Equal("SceneResolved", Node<FogAsset>(document, "Air").Source);
        Assert.Equal("SceneFogged", Node<DepthOfFieldAsset>(document, "Defocus").Source);
        Assert.Equal("SceneDefocused", Node<MotionBlurAsset>(document, "Shutter").Source);
        Assert.Equal("SceneBlurred", Node<AutoExposureAsset>(document, "Meter").Source);
        Assert.Equal("SceneBlurred", Node<LocalExposureAsset>(document, "Adapt").Source);
        Assert.Equal("SceneAdapted", Node<LensFlareAsset>(document, "Flare").Source);
        Assert.Equal("SceneFlared", Node<BloomAsset>(document, "Glow").Source);

        var tonemap = Node<TonemapAsset>(document, "Tonemap");

        Assert.Equal("SceneFlared", tonemap.Source);
        Assert.Equal(Node<BloomAsset>(document, "Glow").Output, tonemap.Bloom);
        Assert.Equal("Meter.Exposure", tonemap.ExposureBuffer);

        Assert.Equal("SceneGraded", Node<FxaaAsset>(document, "Edges").Source);
        Assert.Equal("SceneAntialiased", Node<SharpenAsset>(document, "Recover").Source);
        Assert.Equal("SceneSharpened", Node<VignetteAsset>(document, "Glass").Source);
        Assert.Equal("SceneColour", Node<VignetteAsset>(document, "Glass").Output);

        // The four stages, and the sparks pass beside them; the ember stage's whole character is
        // structural — additive, test-only, unculled, sorted for the depth test.
        Assert.Equal(["Opaque", "Shadow", "Motion", "Embers"], document.Stages.Select(stage => stage.Name));

        var embers = document.Stages.Single(stage => stage.Name == "Embers");

        Assert.Equal(BlendPreset.Additive, embers.Blend);
        Assert.Equal(DepthPreset.TestOnly, embers.Depth);
        Assert.Equal(CullMode.None, embers.Cull);
        Assert.Equal(RenderSortMode.BackToFront, embers.SortMode);
    }

    [Fact]
    public void The_same_knobs_expand_to_the_same_document() {
        var first = Expand(AllOn);
        var second = Expand(AllOn);

        // Records with arrays do not compare by value, so structure is compared piecewise: the
        // node walk by name and type, the flat lists by their records' own equality.
        Assert.Equal(Names(first), Names(second));

        Assert.Equal(
            Root(first).Children.Select(child => child.GetType()),
            Root(second).Children.Select(child => child.GetType())
        );

        Assert.Equal(first.Stages, second.Stages);
        Assert.Equal(first.Resources, second.Resources);
    }

    /// <summary>
    ///     ⚠ The look never enters the expansion: the same knobs with and without one are one graph.
    /// </summary>
    /// <remarks>
    ///     The whole point of the <c>.vxlook</c> being volume layering rather than structure: the
    ///     emitted nodes stay neutral and the look lands on them at run time, so editing it relights
    ///     the same document with nothing rebuilt. A look that leaked into the emission would break
    ///     that promise silently — the snapshot equality here is the tripwire.
    /// </remarks>
    [Fact]
    public void A_look_changes_nothing_about_the_expanded_document() {
        var look = new LookAsset { Settings = new() { Ev100 = 13f, Saturation = 0.8f, FogDensity = 0.03f } };

        var plain = Expand(AllOn);
        var dressed = Expand(AllOn with { Look = look });

        Assert.Equal(Names(plain), Names(dressed));

        Assert.Equal(
            Root(plain).Children.Select(child => child.GetType()),
            Root(dressed).Children.Select(child => child.GetType())
        );

        Assert.Equal(plain.Stages, dressed.Stages);
        Assert.Equal(plain.Resources, dressed.Resources);
    }

    /// <summary>The transformer deposits the document's inline look on the builder — and only there.</summary>
    /// <remarks>
    ///     The seam's two halves: a document that wrote one leaves it where the host will look after
    ///     <c>Load</c>, and a document without one leaves null — so a reload from a document that
    ///     dropped its look drops it, rather than the previous build's look surviving as a ghost.
    /// </remarks>
    [Fact]
    public void The_transform_deposits_the_documents_look_on_the_builder() {
        var look = new LookAsset { Settings = new() { Ev100 = 13f } };

        using var system = new RenderSystem();

        var dressed = new CompositorBuilder(system);
        dressed.Factories.Add(new PostEffectFactory());
        dressed.Views["Camera"] = new("camera") { Position = Vector3.Zero };
        dressed.Build(new() { Game = AllOff with { Look = look } });

        Assert.Same(look, dressed.Look);

        var plain = new CompositorBuilder(system);
        plain.Factories.Add(new PostEffectFactory());
        plain.Views["Camera"] = new("camera") { Position = Vector3.Zero };
        plain.Build(new() { Game = AllOff });

        Assert.Null(plain.Look);
    }

    [Fact]
    public void Extensions_splice_at_their_named_seams() {
        var afterOpaque = new FullScreenAsset { Name = "Decals" };
        var beforePost = new FullScreenAsset { Name = "Heatwave" };
        var beforeUi = new FullScreenAsset { Name = "Wipe" };

        var document = Expand(
            AllOn with {
                Extensions = new() {
                    AfterOpaque = [afterOpaque],
                    BeforePost = [beforePost],
                    BeforeUi = [beforeUi]
                }
            }
        );

        var names = Names(document);

        // After the scene, before the velocity pass that shares its depth.
        Assert.Equal(Array.IndexOf(names, "Main") + 1, Array.IndexOf(names, "Decals"));

        // After lighting is whole, before the accumulator sees the frame.
        Assert.Equal(Array.IndexOf(names, "Combine") + 1, Array.IndexOf(names, "Heatwave"));
        Assert.True(Array.IndexOf(names, "Heatwave") < Array.IndexOf(names, "Accumulate"));

        // Dead last: after the node that wrote the output resource.
        Assert.Equal(names.Length - 1, Array.IndexOf(names, "Wipe"));
    }

    [Fact]
    public void A_document_may_agree_with_a_canonical_declaration_and_may_not_contradict_one() {
        // Identical redeclaration collapses — the document agreeing with the expansion is fine.
        var agreeing = new GraphicsCompositorAsset {
            Resources = [
                new() {
                    Name = "SceneHdr",
                    Format = PixelFormat.Rgba16Float,
                    Usage = TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.Storage
                }
            ]
        };

        var document = Expand(AllOff, agreeing);

        Assert.Single(document.Resources, resource => resource.Name == "SceneHdr");

        // A differing one is refused by name: both readings of that document are somebody's bug.
        var contradicting = new GraphicsCompositorAsset {
            Resources = [new() { Name = "SceneHdr", Format = PixelFormat.Rgba8UNorm }]
        };

        var refusal = Assert.Throws<InvalidOperationException>(() => Expand(AllOff, contradicting));

        Assert.Contains("SceneHdr", refusal.Message);
    }

    [Fact]
    public void A_documents_own_declarations_survive_the_expansion() {
        var document = Expand(
            AllOff,
            new() {
                Stages = [new() { Name = "Overlay", Blend = BlendPreset.AlphaBlend }],
                Resources = [new() { Name = "Mask", Format = PixelFormat.R32UInt }]
            }
        );

        Assert.Contains(document.Stages, stage => stage.Name == "Overlay");
        Assert.Contains(document.Resources, resource => resource.Name == "Mask");
    }

    [Fact]
    public void Two_frames_or_a_frame_inside_a_pass_are_refused() {
        var two = new GraphicsCompositorAsset {
            Game = new SequenceAsset { Children = [AllOff, AllOff with { Quality = QualityTier.Epic }] }
        };

        Assert.Throws<InvalidOperationException>(() => StandardFrame.Expand(two));

        var nested = new GraphicsCompositorAsset {
            Game = new RenderPassAsset { Name = "Outer", Children = [AllOff] }
        };

        Assert.Throws<InvalidOperationException>(() => StandardFrame.Expand(nested));
    }

    [Fact]
    public void A_document_without_the_node_passes_through_untouched() {
        var document = GraphicsCompositorAsset.Default;

        Assert.Same(document, StandardFrame.Expand(document));
    }

    /// <summary>
    ///     The explode path's half: the notes overload explains everything the expansion emitted.
    /// </summary>
    /// <remarks>
    ///     Everything, asserted as a rule rather than as samples, because the exploded file is doc
    ///     39's answer to eleven hundred uncommented lines — a resource or a pass the explode writes
    ///     bare is the promise broken for exactly the reader it was made to.
    /// </remarks>
    [Fact]
    public void The_notes_overload_explains_every_emitted_declaration() {
        var document = PostEffectFactory.Transform(new() { Game = AllOn }, out var notes);

        Assert.All(document.Resources, resource => Assert.True(notes.ContainsKey(resource), resource.Name));
        Assert.All(document.Stages, stage => Assert.True(notes.ContainsKey(stage), stage.Name));
        Assert.All(Root(document).Children, node => Assert.True(notes.ContainsKey(node), node.Name));
        Assert.True(notes.ContainsKey(Root(document)));

        // And the graph is the plain overload's, member for member — the notes are a side channel,
        // never a second expansion.
        Assert.Equal(Names(Expand(AllOn)), Names(document));
    }

    [Fact]
    public void A_document_with_nothing_to_expand_yields_no_notes() {
        var document = GraphicsCompositorAsset.Default;

        Assert.Same(document, PostEffectFactory.Transform(document, out var notes));
        Assert.Empty(notes);
    }

    /// <summary>
    ///     A spliced extension node is the project's own: the expansion did not write it, so it has
    ///     no business explaining it.
    /// </summary>
    [Fact]
    public void An_extension_node_gets_no_note() {
        var custom = new FullScreenAsset { Name = "Heatwave" };

        PostEffectFactory.Transform(
            new() { Game = AllOn with { Extensions = new() { BeforePost = [custom] } } },
            out var notes
        );

        Assert.False(notes.ContainsKey(custom));
    }

    /// <summary>The seam: a preset document builds through the ordinary factory registration.</summary>
    /// <remarks>
    ///     On <c>FrameDocumentTests</c>' terms — empty host slots, no device — because that is the
    ///     state of a game's first build and of every editor that opens the document. What is
    ///     asserted is that the builder's transform ran: the nodes it holds are the expansion's,
    ///     and no <see cref="StandardFrameAsset" /> ever reached the node switch.
    /// </remarks>
    [Fact]
    public void The_builder_expands_the_preset_through_the_registered_factory() {
        using var system = new RenderSystem();

        var builder = new CompositorBuilder(system);

        builder.Factories.Add(new PostEffectFactory());

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        builder.Views["Camera"] = new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };

        var compositor = builder.Build(new() { Game = AllOn });

        Assert.NotNull(compositor.Game);

        foreach (var name in new[] { "Cull", "Sun", "Lamps", "Main", "Gather", "Combine", "Tonemap", "Glass" }) {
            Assert.True(builder.Nodes.ContainsKey(name), $"the expansion lost its '{name}' node");
        }

        // The stages were created from the expansion's declarations, caster settings included.
        Assert.True(builder.Stages.ContainsKey("Shadow"));
        Assert.True(builder.Stages.ContainsKey("Motion"));
        Assert.True(builder.Stages.ContainsKey("Embers"));
    }
}
