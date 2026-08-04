// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Vixen.Samples.ThirdPersonShooter.Tests;

/// <summary>The project's frame document, parsed and built the way the game builds it.</summary>
/// <remarks>
///     <para>
///         <b>Why this exists: a YAML mistake in <c>Frame.vxcompositor</c> used to be a launch.</b>
///         The document is loaded by address inside <c>AppGraphics</c>' constructor, so a bad tag,
///         a renamed stage or a node kind nothing registered threw from inside start-up — on a
///         machine with a window and a GPU, which CI is not. This builds the same document against
///         the Null device on <c>CompositorAssetTests</c>' pattern, so the failure is a test.
///     </para>
///     <para>
///         It builds with <em>empty host slots</em>, deliberately — no visibility group, no store,
///         no fillers — because that is exactly the state of the game's first build, the one that
///         runs before <c>OnInitialise</c> exists to wire anything. A document that only builds
///         once the host is fully wired is a document that crashes every editor that opens it.
///     </para>
/// </remarks>
public sealed class FrameDocumentTests : IDisposable {
    /// <summary>The same registration <c>CompositorImporter</c> makes, for the same reason: the
    ///     document writes colours and vectors as plain scalars — <c>colour: 0.42 0.30 0.20</c> —
    ///     and the generator describes no such shape on its own.</summary>
    static FrameDocumentTests() => MathScalars.Register();

    readonly NullDevice device = new(new() { Record = true });

    /// <summary>The names this document promises, and the game's code reaches for by name.</summary>
    /// <remarks>
    ///     <c>Arena</c> finds the clipmap node to fill its instances, <c>ArenaIllumination.Feed</c>
    ///     finds four more, and the tonemap's <c>Meter.Exposure</c> buffer name is derived from
    ///     <c>Meter</c> — so a rename here is game code silently doing nothing, which is why the
    ///     list is asserted rather than merely enumerated.
    /// </remarks>
    static readonly string[] NamedNodes = [
        "Cull", "Clipmap", "Probes", "Cache", "Sun", "Lamps", "Traversal", "Visibility", "Sky",
        "Main", "Velocity", "Sparks", "Occluders", "Gather", "Mirrors", "Occlusion", "Indirect",
        "ContactOcclusion", "Combine", "Accumulate", "Air", "Defocus", "Shutter", "Meter", "Adapt",
        "Flare", "Glow", "Tonemap", "Edges", "Recover", "Glass", "Edging"
    ];

    static string DocumentPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Frame.vxcompositor");

    [Fact]
    public void The_document_parses_and_builds_against_a_headless_device() {
        using var built = Build();

        Assert.NotNull(built.Compositor.Game);

        foreach (var name in NamedNodes) {
            Assert.True(built.Builder.Nodes.ContainsKey(name), $"the document lost its '{name}' node");
        }
    }

    /// <summary>The enabled set is the split's: everything that composes is on, and what is off has a reason.</summary>
    /// <remarks>
    ///     <para>
    ///         Still a lock on the honest state, and the state moved twice: the ambient split closed
    ///         the gaps that kept the gather, the occlusion pair and the combine off, and the engine
    ///         publishing <c>ReflectionRenderer</c>'s target into the frame's namespace closed the
    ///         seam that kept the mirrors off — so every node on doc 19's page is on now, and its
    ///         plane reaches the combine's <c>reflections:</c> seat. Whoever changes the document's
    ///         lines changes this list with them, in that order.
    ///     </para>
    ///     <para>
    ///         What stays off stays for stated reasons rather than closed-and-forgotten gaps:
    ///         <c>!IndirectDiffuse</c> because the gather already supplies the combine's screen
    ///         irradiance — running both is the same skylight added twice — and <c>!Outline</c>
    ///         because it is a look this level does not want.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_enabled_set_is_what_the_split_made_composable() {
        using var built = Build();

        Assert.True(built.Builder.Nodes["Gather"].Enabled, "the split gave the gather its depth decode and its normals producer");
        Assert.True(built.Builder.Nodes["Occlusion"].Enabled, "the combine consumes the occlusion plane now");
        Assert.True(built.Builder.Nodes["ContactOcclusion"].Enabled, "the combine multiplies contact into the same ambient");
        Assert.True(built.Builder.Nodes["Combine"].Enabled, "the combine is where the split frame becomes whole");
        Assert.True(built.Builder.Nodes["Mirrors"].Enabled, "the reflections target enters the frame's namespace now — the last of doc 19's nodes to flip");

        Assert.False(built.Builder.Nodes["Indirect"].Enabled, "the gather already supplies the screen irradiance — both is double-counted skylight");
        Assert.False(built.Builder.Nodes["Edging"].Enabled, "outlines are a look this level does not want");
    }

    /// <summary>The Main pass's target order is ForwardPlus.SplitOutputs' contract, member for member.</summary>
    /// <remarks>
    ///     Location 0 is direct light, 1 is albedo with occlusion in alpha, 2 is world normal with
    ///     roughness in alpha — the shader dictates the order and the document can only repeat it,
    ///     so a reorder here is albedo shaded as radiance with every counter reporting success.
    ///     Asserted with the visibility resolve's split knobs, because the two paths writing the
    ///     same planes on the same terms is the whole reason the knobs exist — and with the
    ///     combine's seats, because the planes the frame produces and the names the combine reads
    ///     are one contract seen from both ends, the mirrors' plane now among them.
    /// </remarks>
    [Fact]
    public void The_main_pass_writes_the_split_targets_in_the_shaders_order() {
        using var built = Build();

        var main = Assert.IsType<RenderPassRenderer>(built.Builder.Nodes["Main"]);

        Assert.Equal(["SceneHdr", "SceneAlbedo", "SceneNormals"], main.ColourTargets);

        var visibility = Assert.IsType<VisibilityBufferRenderer>(built.Builder.Nodes["Visibility"]);

        Assert.Equal("SceneAlbedo", visibility.Albedo);
        Assert.Equal("SceneNormals", visibility.Normals);

        // The consuming end of the same contract: each seat names exactly the plane a node above
        // publishes, the reflections target included now that the renderer can publish one.
        var combine = Assert.IsType<AmbientCombineRenderer>(built.Builder.Nodes["Combine"]);

        Assert.Equal("SceneHdr", combine.Direct);
        Assert.Equal("SceneAlbedo", combine.Albedo);
        Assert.Equal("SceneNormals", combine.Normals);
        Assert.Equal("Reflections", combine.Reflections);

        Assert.Equal(
            "Reflections",
            Assert.IsType<ReflectionRenderer>(built.Builder.Nodes["Mirrors"]).Target
        );
    }

    /// <summary>Two nodes march the nearest chain, so its ring must be sized for both.</summary>
    /// <remarks>
    ///     The gather's screen traces and the reflections kernel both skip by the host's one
    ///     nearest-reduced pyramid, and each rebuilds it once a frame — two descriptor rewrites
    ///     before a single submission, which is exactly what <c>TakeTracePyramid</c> counts takers
    ///     for. A ring left at one is a rewrite beneath a frame in flight, and nothing about the
    ///     picture would say so. Both rebuilds are real now that the mirrors are enabled; the
    ///     count was two even while they were off, because the factory takes the chain for the
    ///     node it builds, not for the frames it happens to record.
    /// </remarks>
    [Fact]
    public void Both_marching_nodes_deepen_the_trace_chains_ring() {
        using var chain = new HiZPyramid(device) { Reduction = HiZReduction.Nearest };

        using var built = Build(builder => builder.TracePyramid = chain);

        Assert.Equal(2, chain.BuildsPerFrame);
    }

    /// <summary>The culling node adopts the host's group, which is the handover the game relies on.</summary>
    [Fact]
    public void The_document_turns_the_hosts_culling_group_on() {
        using var visibility = new GpuVisibilityGroup(device);
        using var pyramid = new HiZPyramid(device);

        using var built = Build(
            builder => {
                builder.Visibility = visibility;
                builder.Occluders = pyramid;
            }
        );

        Assert.Same(visibility, built.System.Visibility);
        Assert.Same(pyramid, visibility.Occluders);
        Assert.Same(pyramid, Assert.IsType<HiZRenderer>(built.Builder.Nodes["Occluders"]).Pyramid);
    }

    static Built Build(Action<CompositorBuilder>? wire = null) {
        // Constructing the factory is also what first touches Vixen.Rendering.PostFx, whose module
        // initializer registers the !Bloom-family YAML tags — parse before that and the tags are
        // unknown. The game's OnConfigure makes the same point about the same line.
        var factory = new PostEffectFactory();
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(File.ReadAllText(DocumentPath));

        Assert.Equal(CompositorBuilder.SupportedVersion, asset.Version);

        var system = new RenderSystem();

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var builder = new CompositorBuilder(system);

        builder.Factories.Add(factory);
        builder.Views["Camera"] = new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };

        wire?.Invoke(builder);

        return new(system, builder, builder.Build(asset));
    }

    sealed record Built(RenderSystem System, CompositorBuilder Builder, GraphicsCompositor Compositor) : IDisposable {
        public void Dispose() => System.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }
}
