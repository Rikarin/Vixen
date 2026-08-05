// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Water;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     What the water pass declares, and what it refuses — [docs/plan/35 § D8].
/// </summary>
/// <remarks>
///     The look is a golden image's to check and the arithmetic is the seam test's. What is here is
///     the third thing, and the one that is silent when it is wrong: whether the pass is <em>in the
///     graph</em> reading what it says it reads, so that it is ordered behind the surface pass and the
///     copy rather than wherever the host happened to build it.
/// </remarks>
public sealed class WaterPassTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();
    readonly RenderSystem system = new();
    readonly RenderGraph graph;
    readonly SamplerCache samplers;
    readonly DescriptorAllocator descriptors;

    public WaterPassTests() {
        graph = new(device);
        samplers = new(device);
        descriptors = new(device);
    }

    /// <inheritdoc />
    public void Dispose() {
        graph.DisposePool();
        descriptors.Dispose();
        samplers.Dispose();
        system.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The fixture --------------------------------------------------------

    const int Size = 16;

    static RenderResourceAsset Declared(string name, TextureUsage usage = TextureUsage.ColourTarget | TextureUsage.Sampled) =>
        new() { Name = name, Format = PixelFormat.Rgba16Float, Usage = usage };

    WaterRenderer Node(string behind = "SceneColourCopy", string reflections = "") =>
        new() {
            Name = "Water",
            Output = "SceneColour",
            Behind = behind,
            SceneDepth = "SceneDepth",
            Surface = "WaterSurface",
            Normal = "WaterNormal",
            Reflections = reflections,
            Samplers = samplers,
            Allocator = descriptors,
            Device = device
        };

    GraphicsCompositor Compositor(SceneRenderer node) {
        var sequence = new SceneRendererSequence { Name = "Frame" };

        sequence.Children.Add(Writer("SceneColourCopy"));
        sequence.Children.Add(Writer("SceneDepth"));
        sequence.Children.Add(Writer("WaterSurface"));
        sequence.Children.Add(Writer("WaterNormal"));
        sequence.Children.Add(Writer("Reflections"));
        sequence.Children.Add(node);

        var compositor = new GraphicsCompositor(system) { FrameSize = new(Size, Size), Game = sequence };

        foreach (var name in (string[])["SceneColour", "SceneColourCopy", "SceneDepth", "WaterSurface", "WaterNormal", "Reflections"]) {
            compositor.Resources.Add(Declared(name));
        }

        return compositor;
    }

    /// <summary>A node that writes a plane, so the water pass has something to be ordered behind.</summary>
    static DelegateSceneRenderer Writer(string target) =>
        new() {
            Name = "Writer " + target,
            OnBuild = (_, frame) => {
                var texture = frame.Texture("Writer", target);

                frame.Graph.AddPass(
                    "Writer " + target,
                    pass => {
                        pass.ColourAttachment(texture);
                        pass.Execute(context => context.CommandList.Draw(3));
                    }
                );
            }
        };

    CompositorFrame Build(GraphicsCompositor compositor) {
        graph.Reset();
        return compositor.Build(graph, effects, device);
    }

    // --- What it declares ---------------------------------------------------

    /// <summary>The pass reads every plane it says it reads, which is what orders it.</summary>
    /// <remarks>
    ///     ⚠ The binding is what the shader reads a plane <em>through</em>; the declared read is what
    ///     orders this pass after whatever wrote it and keeps that producer from being culled. One
    ///     without the other is a validation error or a race, so both are asserted together.
    /// </remarks>
    [Fact]
    public void The_pass_declares_every_plane_it_binds() {
        using var water = Node(reflections: "Reflections");

        Build(Compositor(water));

        Assert.Equal(1, water.BuildCount);

        Assert.Contains("SceneColourCopy", water.Pass.Reads);
        Assert.Contains("SceneDepth", water.Pass.Reads);
        Assert.Contains("WaterSurface", water.Pass.Reads);
        Assert.Contains("WaterNormal", water.Pass.Reads);
        Assert.Contains("Reflections", water.Pass.Reads);
        Assert.Contains("SceneColour", water.Pass.ColourTargets);

        // Five sampled planes and two samplers, each on the binding the generated keys say.
        Assert.Equal(5, water.Pass.Descriptors.Bindings.Count(b => b.Kind == DescriptorKind.SampledTexture));
        Assert.Equal(2, water.Pass.Descriptors.Bindings.Count(b => b.Kind == DescriptorKind.Sampler));
    }

    /// <summary>
    ///     ⚠ The reflection plane is bound even when the permutation is off.
    /// </summary>
    /// <remarks>
    ///     A descriptor set is written wholly or not at all — the rule <c>!AmbientCombine</c>'s
    ///     stand-in planes follow. What switches the term off is the permutation; what would happen
    ///     without a binding is a descriptor the driver refuses, which is a frame that does not render
    ///     rather than a frame with no reflections.
    /// </remarks>
    [Fact]
    public void A_document_with_no_reflection_plane_still_binds_one() {
        using var water = Node();

        Build(Compositor(water));

        Assert.Equal(5, water.Pass.Descriptors.Bindings.Count(b => b.Kind == DescriptorKind.SampledTexture));
        Assert.DoesNotContain("Reflections", water.Pass.Reads);
    }

    /// <summary>
    ///     ⚠ Reading the target it writes is refused, by name, at build time.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § B1] calls the copy the blocker rather than the pass: sampling a target a
    ///     pass is also writing is <em>undefined</em>, which means it renders on one driver and not
    ///     another. A document that made the mistake would otherwise ship.
    /// </remarks>
    [Fact]
    public void Reading_the_target_it_writes_is_refused() {
        using var water = Node(behind: "SceneColour");

        var thrown = Assert.Throws<CompositorBindingException>(() => Build(Compositor(water)));

        Assert.Contains("!Copy", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("undefined", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A host that has not wired the renderer up gets a frame with no water.</summary>
    /// <remarks>
    ///     The terms <c>!ScreenProbeGather</c> and <c>!SurfaceCache</c> are built on: a node built
    ///     with nothing supplied does nothing rather than throwing, so one authored document serves a
    ///     project that has no water renderer.
    /// </remarks>
    [Fact]
    public void A_node_with_no_device_does_nothing_rather_than_throwing() {
        using var water = new WaterRenderer {
            Name = "Water",
            Output = "SceneColour",
            Behind = "SceneColourCopy",
            SceneDepth = "SceneDepth",
            Surface = "WaterSurface",
            Normal = "WaterNormal"
        };

        Build(Compositor(water));

        Assert.Equal(0, water.BuildCount);
        Assert.Empty(water.Pass.Reads);
    }

    // --- What a document says -----------------------------------------------

    /// <summary>The factory builds the node a document named, with the numbers it stated.</summary>
    [Fact]
    public void The_factory_carries_a_documents_numbers_onto_the_node() {
        var builder = new CompositorBuilder(system) { Samplers = samplers, Descriptors = descriptors, Device = device };

        var asset = new WaterAsset {
            Name = "Sea",
            Behind = "Snapshot",
            Reflections = "Mirrors",
            Absorption = new(0.4f, 0.05f, 0.01f),
            PhaseG = 0.55f,
            BehindScale = new(0.5f, 0.5f, 0.5f),
            SunColour = new(1.2f, 1f, 0.8f),
            SunDirection = new(0.5f, -0.7f, 0.5f),
            Foam = false
        };

        using var node = Assert.IsType<WaterRenderer>(new WaterRendererFactory().Create(asset, builder));

        Assert.Equal("Sea", node.Name);
        Assert.Equal("Snapshot", node.Behind);
        Assert.Equal("Mirrors", node.Reflections);
        Assert.Equal(0.55f, node.PhaseG);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), node.BehindScale);
        Assert.False(node.Foam);

        // ⚠ The sun rides the document too. Without these two the phase function's forward peak sits
        // under a noon sun whatever the sky in the same document says — a lake lit from a different
        // day than its sky, and nothing an author types elsewhere can move it.
        Assert.Equal(new Vector3(1.2f, 1f, 0.8f), node.SunColour);
        Assert.Equal(new Vector3(0.5f, -0.7f, 0.5f), node.SunDirection);

        // ⚠ And its defaults are water's, not zero. `behindScale` at zero is a perfectly black frame
        // behind the water, which reads as "the water is opaque" rather than as a parameter nobody set.
        Assert.Equal(Vector3.One, new WaterAsset().BehindScale);
        Assert.Equal(0.02f, new WaterAsset().SurfaceF0);
        Assert.Equal(new Vector3(0f, -1f, 0f), new WaterAsset().SunDirection);
    }

    /// <summary>A factory answers nothing for a node kind that is not its own.</summary>
    /// <remarks>
    ///     Asked after the built-ins, so a factory that claimed everything would quietly replace a
    ///     node kind the document's schema already defines.
    /// </remarks>
    [Fact]
    public void The_factory_declines_a_node_that_is_not_its_own() {
        var builder = new CompositorBuilder(system);

        Assert.Null(new WaterRendererFactory().Create(new SequenceAsset { Name = "Frame" }, builder));
    }
}
