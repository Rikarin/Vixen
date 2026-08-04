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
        "ContactOcclusion", "Accumulate", "Air", "Defocus", "Shutter", "Meter", "Adapt", "Flare",
        "Glow", "Tonemap", "Edges", "Recover", "Glass", "Edging"
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

    /// <summary>The nodes that are off stay off, until the gaps their comments name are closed.</summary>
    /// <remarks>
    ///     Locking the honest state rather than the ambition. <c>!ScreenProbeGather</c> enabled
    ///     against this frame's Depth32Float depth is a <c>CompositorBindingException</c> out of
    ///     every frame's build; <c>!Reflections</c> enabled against an unproduced SceneNormals is a
    ///     full-screen dispatch into garbage; the occlusion trio wants the ambient split. Whoever
    ///     closes a gap flips the document's line and then this list, in that order.
    /// </remarks>
    [Fact]
    public void What_the_document_says_is_off_is_off() {
        using var built = Build();

        Assert.False(built.Builder.Nodes["Gather"].Enabled, "the gather needs a readable depth format and a normals producer");
        Assert.False(built.Builder.Nodes["Mirrors"].Enabled, "reflections need a normals producer and a composite pass");
        Assert.False(built.Builder.Nodes["Occlusion"].Enabled, "the AO appliers wait on the ambient split");
        Assert.False(built.Builder.Nodes["Indirect"].Enabled, "the AO appliers wait on the ambient split");
        Assert.False(built.Builder.Nodes["ContactOcclusion"].Enabled, "the AO appliers wait on the ambient split");
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
