// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Tests;

/// <summary>
///     The quality waterfall: engine tier defaults, a project's <c>.vxpreset</c>, a document's
///     inline overlay — folded per parameter, consumed by the Standard Frame expansion.
/// </summary>
/// <remarks>
///     The layer tests work on <see cref="RenderQuality.Resolve" />'s product alone; the
///     consumption tests assert through the expanded <em>asset</em>, on
///     <c>StandardFrameTests</c>' exact terms — the resolved number is only real once a node
///     carries it and a resource's extent is its arithmetic.
/// </remarks>
public class RenderQualityTests {
    static StandardFrameAsset Bare => new() {
        Shadows = ShadowMode.Off,
        Gi = GiMode.Off,
        Reflections = ReflectionsMode.Off,
        Antialiasing = AntialiasingMode.Off,
        Exposure = ExposureMode.Fixed,
        Particles = false
    };

    static SequenceAsset Root(GraphicsCompositorAsset document) => Assert.IsType<SequenceAsset>(document.Game);

    static TAsset Node<TAsset>(GraphicsCompositorAsset document, string name) =>
        Assert.IsType<TAsset>(Root(document).Children.Single(child => child.Name == name));

    [Fact]
    public void The_engine_table_is_complete_for_every_tier() {
        // Resolve refuses a hole by name rather than reading it as zero, so completeness is
        // simply "every tier resolves" — plus a spot check that the columns differ the way the
        // ladder promises.
        foreach (var tier in Enum.GetValues<QualityTier>()) {
            _ = RenderQuality.Resolve(tier);
        }

        var low = RenderQuality.Resolve(QualityTier.Low);
        var epic = RenderQuality.Resolve(QualityTier.Epic);

        Assert.Equal(2, low.CascadeCount);
        Assert.Equal(CullingMode.Off, low.Culling);
        Assert.False(low.Bloom);

        Assert.Equal(2048, epic.CascadeResolution);
        Assert.True(epic.MotionBlur);
        Assert.Equal(FxaaPreset.Quality, epic.Fxaa);

        // Vegetation is a column like the rest: High is the libraries' shipped defaults, and the
        // ladder thins density before it shortens distance.
        Assert.Equal(1f, RenderQuality.Resolve(QualityTier.High).GrassDensityScale);
        Assert.Equal(256, RenderQuality.Resolve(QualityTier.High).GrassResidentCells);
        Assert.True(low.GrassDensityScale < 1f);
        Assert.True(epic.FoliageCullDistanceScale > 1f);
    }

    [Fact]
    public void Each_layer_out_votes_the_one_below_it() {
        var project = new RenderQualityAsset {
            High = new() { Shadows = new() { CascadeResolution = 4096 } }
        };

        var overlay = new RenderQualityAsset {
            High = new() { Shadows = new() { CascadeResolution = 8192 } }
        };

        Assert.Equal(2048, RenderQuality.Resolve(QualityTier.High).CascadeResolution);
        Assert.Equal(4096, RenderQuality.Resolve(QualityTier.High, project).CascadeResolution);
        Assert.Equal(8192, RenderQuality.Resolve(QualityTier.High, project, overlay).CascadeResolution);
    }

    [Fact]
    public void An_override_of_one_field_leaves_every_sibling_to_the_layer_below() {
        // One field in one group: the group's siblings, the other groups, and the layer's other
        // tiers all fall through — per parameter, never per group, which is the whole point of
        // the opinion model.
        var project = new RenderQualityAsset {
            High = new() { Shadows = new() { CascadeResolution = 4096 } }
        };

        var resolved = RenderQuality.Resolve(QualityTier.High, project);
        var engine = RenderQuality.Resolve(QualityTier.High);

        Assert.Equal(4096, resolved.CascadeResolution);
        Assert.Equal(engine.CascadeCount, resolved.CascadeCount);
        Assert.Equal(engine.ShadowDistance, resolved.ShadowDistance);
        Assert.Equal(engine.SsaoDirections, resolved.SsaoDirections);
        Assert.Equal(engine.BloomLevels, resolved.BloomLevels);

        // And the tiers are columns, not a chain: the preset said nothing about Low, so Low is
        // exactly the engine's.
        Assert.Equal(RenderQuality.Resolve(QualityTier.Low), RenderQuality.Resolve(QualityTier.Low, project));
    }

    [Fact]
    public void The_expansion_consumes_the_resolved_numbers_down_to_the_atlas_extent() {
        // A Low-tier frame under a project preset that shrinks the lamp atlas: the resolved
        // numbers must land on the nodes, and the resource extent must be the nodes' own
        // arithmetic over those numbers — the audit's guard, now fed by the waterfall.
        var project = new RenderQualityAsset {
            Low = new() { Shadows = new() { PunctualTilesPerSide = 2, PunctualResolution = 128 } }
        };

        var document = StandardFrame.Expand(
            new() { Game = Bare with { Quality = QualityTier.Low, Shadows = ShadowMode.Cascades } },
            project: project
        );

        var lamps = Node<PunctualShadowAsset>(document, "Lamps");

        Assert.Equal(2, lamps.TilesPerSide);
        Assert.Equal(128, lamps.Resolution);

        var atlas = document.Resources.Single(resource => resource.Name == lamps.Atlas);

        Assert.Equal(2 * 128, atlas.Width);
        Assert.Equal(atlas.Width, atlas.Height);

        // The sun's atlas stayed the engine's Low column — two cascades of 1024 — because the
        // preset had no opinion about it.
        var sun = Node<ShadowMapAsset>(document, "Sun");

        Assert.Equal(2, sun.CascadeCount);
        Assert.Equal(1024, sun.Resolution);
    }

    [Fact]
    public void The_documents_inline_preset_is_the_top_vote() {
        var project = new RenderQualityAsset {
            High = new() { PostFidelity = new() { BloomLevels = 7 } }
        };

        var inline = new RenderQualityAsset {
            High = new() { PostFidelity = new() { BloomLevels = 2 } }
        };

        var document = StandardFrame.Expand(
            new() { Game = Bare with { Quality = QualityTier.High, Preset = inline } },
            project: project
        );

        Assert.Equal(2, Node<BloomAsset>(document, "Glow").Levels);
    }

    [Fact]
    public void A_frame_that_names_no_tier_takes_the_hosts_pick_and_a_named_one_out_votes_it() {
        // Unset quality: the fallback decides — Low drops the whole bloom/fog block.
        var hosted = StandardFrame.Expand(new() { Game = Bare }, QualityTier.Low);

        Assert.DoesNotContain(Root(hosted).Children, child => child.Name is "Glow" or "Air");

        // A named tier is the document's own decision, whatever the host picked.
        var authored = StandardFrame.Expand(
            new() { Game = Bare with { Quality = QualityTier.High } },
            QualityTier.Low
        );

        Assert.Contains(Root(authored).Children, child => child.Name == "Glow");
    }

    [Fact]
    public void The_hosts_tier_flows_through_the_builder_into_the_transform() {
        using var system = new RenderSystem();

        var builder = new CompositorBuilder(system) { Quality = QualityTier.Low };
        var factory = new PostEffectFactory();

        var document = factory.Transform(new() { Game = Bare }, builder);

        // Low's column: no culling node, no fog, no bloom — the platform's pick decided, because
        // the document declined to.
        Assert.DoesNotContain(Root(document).Children, child => child.Name is "Cull" or "Air" or "Glow");
    }

    [Fact]
    public void A_late_phase_preset_emits_the_second_cull_after_the_reduce() {
        var project = new RenderQualityAsset {
            High = new() { Geometry = new() { Culling = CullingMode.LatePhase } }
        };

        var document = StandardFrame.Expand(
            new() { Game = Bare with { Quality = QualityTier.High } },
            project: project
        );

        var names = Root(document).Children.Select(child => child.Name).ToArray();

        // Main cull at the head; the late dispatch strictly after the pyramid it re-asks against.
        Assert.Equal(0, Array.IndexOf(names, "Cull"));
        Assert.True(Array.IndexOf(names, "Occluders") < Array.IndexOf(names, "LateCull"));

        var late = Node<GpuCullingAsset>(document, "LateCull");

        Assert.Equal(CullPhase.Late, late.Phase);
        Assert.True(late.IndirectDraws);
    }

    [Fact]
    public void Resolution_and_expansion_are_deterministic() {
        var project = new RenderQualityAsset {
            Epic = new() {
                Shadows = new() { CascadeResolution = 4096 },
                PostFidelity = new() { MotionBlurSamples = 16 }
            }
        };

        // The resolved struct is a record of plain values, so determinism is value equality.
        Assert.Equal(
            RenderQuality.Resolve(QualityTier.Epic, project),
            RenderQuality.Resolve(QualityTier.Epic, project)
        );

        var frame = Bare with { Quality = QualityTier.Epic, Shadows = ShadowMode.Cascades };
        var first = StandardFrame.Expand(new() { Game = frame }, project: project);
        var second = StandardFrame.Expand(new() { Game = frame }, project: project);

        Assert.Equal(
            Root(first).Children.Select(child => (child.Name, child.GetType())),
            Root(second).Children.Select(child => (child.Name, child.GetType()))
        );

        Assert.Equal(first.Stages, second.Stages);
        Assert.Equal(first.Resources, second.Resources);
    }

    [Fact]
    public void The_render_scale_lands_on_the_scene_planes_and_never_the_output() {
        var project = new RenderQualityAsset {
            High = new() { Resolution = new() { RenderScale = 0.75f } }
        };

        var document = StandardFrame.Expand(
            new() { Game = Bare with { Quality = QualityTier.High } },
            project: project
        );

        Assert.Equal(0.75f, document.Resources.Single(resource => resource.Name == "SceneHdr").Scale);
        Assert.Equal(0.75f, document.Resources.Single(resource => resource.Name == "SceneDepth").Scale);

        // The output is the window's image: scaling it would not upscale the frame, it would
        // shrink the picture the player is shown.
        Assert.Equal(1f, document.Resources.Single(resource => resource.Name == "SceneColour").Scale);
    }

    /// <summary>
    ///     The document's vote reaches a caller that never expands it, which is what the tier's
    ///     consumers outside the post chain are.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Asked before the build, necessarily.</b> The expansion replaces the frame node, so a
    ///     host reading the built document finds nothing that says <c>quality:</c> — which is how a
    ///     project shipping an Epic frame over a High host got Epic post effects and High ground.
    /// </remarks>
    [Fact]
    public void A_documents_own_tier_is_readable_without_expanding_it() {
        var document = new GraphicsCompositorAsset { Game = Bare with { Quality = QualityTier.Epic } };

        Assert.Equal(
            RenderQuality.Resolve(QualityTier.Epic),
            PostEffectFactory.QualityOf(document, QualityTier.Low, project: null)
        );
    }

    /// <summary>A document with no opinion keeps taking the platform's pick, unchanged.</summary>
    [Fact]
    public void A_document_that_names_no_tier_reads_back_as_the_hosts_pick() {
        // Both readings of "says nothing": a frame node with no quality line, and a hand-authored
        // document with no frame node at all.
        Assert.Equal(
            RenderQuality.Resolve(QualityTier.Medium),
            PostEffectFactory.QualityOf(new() { Game = Bare }, QualityTier.Medium, project: null)
        );

        Assert.Equal(
            RenderQuality.Resolve(QualityTier.Medium),
            PostEffectFactory.QualityOf(new(), QualityTier.Medium, project: null)
        );
    }

    /// <summary>
    ///     And the whole waterfall arrives, not only the column: the project preset under the
    ///     document's inline overlay, per parameter.
    /// </summary>
    [Fact]
    public void The_read_back_tier_is_the_same_fold_the_expansion_performs() {
        var project = new RenderQualityAsset {
            Epic = new() { Vegetation = new() { GrassResidentCells = 111 }, Shadows = new() { CascadeCount = 3 } }
        };

        var inline = new RenderQualityAsset { Epic = new() { Vegetation = new() { GrassResidentCells = 222 } } };

        var document = new GraphicsCompositorAsset {
            Game = Bare with { Quality = QualityTier.Epic, Preset = inline, Shadows = ShadowMode.Cascades }
        };

        var read = PostEffectFactory.QualityOf(document, QualityTier.Low, project);

        // The inline overlay out-votes the project preset, and a sibling the overlay says nothing
        // about still comes from the project — which is the per-parameter rule, read back.
        Assert.Equal(222, read.GrassResidentCells);
        Assert.Equal(3, read.CascadeCount);

        // And it is the same number the expansion put on the node, rather than a second fold that
        // happens to agree today.
        Assert.Equal(
            read.CascadeCount,
            Node<ShadowMapAsset>(StandardFrame.Expand(document, QualityTier.Low, project), "Sun").CascadeCount
        );
    }
}
