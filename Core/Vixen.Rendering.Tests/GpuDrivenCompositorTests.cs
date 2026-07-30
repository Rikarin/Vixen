// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A document asking for the GPU-driven path, and a device answering.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Everything in <c>docs/plan/23-bindless-materials.md</c> was reachable from a test and from
///         nothing a project authors.</strong> The compositor builder wired one thing out of the whole
///         chain — the argument buffer — and never created a table, never turned records on, never
///         asked for compaction. A mechanism nothing invokes is a mechanism that compiles.
///     </para>
///     <para>
///         ⚠ <strong>Every flag is a request and the device answers it.</strong> That is what makes
///         it safe to put in a document at all: one file runs on a machine with descriptor indexing
///         and on one without, and the second draws the same image through a descriptor set per
///         material. A document that could break a target would have to be a build configuration.
///     </para>
/// </remarks>
public sealed class GpuDrivenCompositorTests : IDisposable {
    const string Document = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled
        stages:
          - name: Opaque
        gpuDriven:
          shader: Lit
          materialRecords: true
          transformRecords: true
        game: !Sequence
          name: Frame
          children:
            - !GpuCulling
              name: Culling
              readBack: false
              indirectDraws: true
              compact: true
            - !RenderPass
              name: Main
              colourTargets: [SceneColour]
              children:
                - !SingleStage
                  name: OpaqueDraw
                  view: Camera
                  stage: Opaque
        """;

    readonly NullDevice device;
    readonly EffectSystem effects = new();

    public GpuDrivenCompositorTests() : this(capable: true) { }

    GpuDrivenCompositorTests(bool capable) {
        device = new(new() { Features = Features(capable) });
        effects.AddProvider(new AlwaysCompiles());
    }

    /// <summary>A device with the whole chain, or one with none of it.</summary>
    /// <remarks>
    ///     The second is not a contrivance: no descriptor indexing is GL, WebGL2 and MoltenVK below
    ///     argument-buffer tier 2, and no device-side draw count is GL, WebGPU and Metal. A machine
    ///     with neither is the common case rather than the awkward one.
    /// </remarks>
    static GraphicsDeviceFeatures Features(bool capable) =>
        capable
            ? NullDevice.Everything
            : NullDevice.Everything with {
                HasBindless = false,
                MaxBindlessDescriptors = 0,
                MaxDescriptorSets = 4,
                HasDrawIndirectCount = false
            };

    public void Dispose() => device.Dispose();

    /// <summary>A document that asks for the GPU-driven path gets it, on a device that has it.</summary>
    /// <remarks>
    ///     Both halves of both features: <c>UseRecords</c> says where a host writes the bytes and the
    ///     permutation says which shader is compiled to read them, and the two move together inside
    ///     <c>EnableRecords</c> because either alone draws a picture that is wrong.
    /// </remarks>
    [Fact]
    public void A_document_turns_the_whole_chain_on() {
        using var h = Build();

        h.Builder.Build(Parse(Document));

        Assert.True(h.Materials.UseRecords);
        Assert.True(h.Transforms.UseRecords);
        Assert.True(h.Builder.GpuDriven.MaterialRecords);
        Assert.True(h.Builder.GpuDriven.TransformRecords);

        Assert.True(h.Materials.Permutations.Get(Permutation("Lit.UseMaterialRecords")));
        Assert.Contains(h.Transforms.PermutationKeys, key => key.Name == "Lit.UseTransformRecords");
    }

    /// <summary>
    ///     And the same document on a device with none of it builds, and turns nothing on.
    /// </summary>
    /// <remarks>
    ///     <strong>The assertion the whole design rests on.</strong> One authored frame, two machines.
    ///     If a document asking for records could fail to build where they are absent, the flag would
    ///     have to be a build configuration and "the frame is data" would stop at the capability line.
    /// </remarks>
    [Fact]
    public void The_same_document_on_a_plain_device_asks_and_is_refused() {
        using var plain = new GpuDrivenCompositorTests(capable: false);
        using var h = plain.Build();

        h.Builder.Build(Parse(Document));

        Assert.False(h.Materials.UseRecords);
        Assert.False(h.Transforms.UseRecords);
        Assert.False(h.Builder.GpuDriven.MaterialRecords);
        Assert.False(h.Builder.GpuDriven.TransformRecords);
    }

    /// <summary>A document that asks for nothing leaves the features exactly as they were.</summary>
    /// <remarks>
    ///     The control, and the case every existing document is: no <c>gpuDriven</c> section at all.
    ///     Without this, "the device said no" and "nobody asked" would be the same observation.
    /// </remarks>
    [Fact]
    public void A_document_that_does_not_ask_gets_nothing() {
        using var h = Build();

        var document = Document.Replace("  materialRecords: true", "  materialRecords: false", StringComparison.Ordinal)
            .Replace("  transformRecords: true", "  transformRecords: false", StringComparison.Ordinal);

        h.Builder.Build(Parse(document));

        Assert.False(h.Materials.UseRecords);
        Assert.False(h.Transforms.UseRecords);
    }

    /// <summary>Compaction is asked for by the culling node and answered by the buffer.</summary>
    /// <remarks>
    ///     <c>Compact</c> is the request and <c>IsCompacted</c> is the answer, and they are separate
    ///     because a draw loop reads the second: reading a padded list as a compacted one draws every
    ///     object with another's arguments.
    /// </remarks>
    [Fact]
    public void The_culling_node_asks_for_compaction() {
        using var h = Build();
        using var visibility = new GpuVisibilityGroup(device);
        using var arguments = new GpuDrawArguments(device);

        h.Builder.Visibility = visibility;
        h.Builder.Arguments = arguments;
        h.Builder.Build(Parse(Document));

        Assert.True(arguments.Compact);
        Assert.Same(arguments, h.Meshes.Arguments);
    }

    /// <summary>And a document that does not ask leaves the buffer padded.</summary>
    [Fact]
    public void Without_the_flag_the_buffer_stays_padded() {
        using var h = Build();
        using var visibility = new GpuVisibilityGroup(device);
        using var arguments = new GpuDrawArguments(device);

        h.Builder.Visibility = visibility;
        h.Builder.Arguments = arguments;

        h.Builder.Build(Parse(Document.Replace("compact: true", "compact: false", StringComparison.Ordinal)));

        Assert.False(arguments.Compact);
    }

    /// <summary>
    ///     The per-object records follow the transforms, because they are addressed by them.
    /// </summary>
    /// <remarks>
    ///     <strong>Not an independent flag, and it must not become one.</strong> What addresses a
    ///     per-object record is the draw's instance index, and that holds the object's slot only
    ///     because the transform record path put it there. Asked without it, every draw carries zero
    ///     and every object reads record zero's probe — a picture, and a plausible one.
    /// </remarks>
    [Fact]
    public void The_object_records_follow_the_transforms() {
        using var h = Build();

        h.Builder.Build(Parse(Document));

        Assert.True(h.Builder.GpuDriven.TransformRecords);
        Assert.True(h.Builder.GpuDriven.ObjectRecords);
        Assert.True(h.Lighting.UseRecords);
    }

    /// <summary>And with the transforms refused, the records are refused with them.</summary>
    [Fact]
    public void Without_transforms_the_object_records_stay_off() {
        using var plain = new GpuDrivenCompositorTests(capable: false);
        using var h = plain.Build();

        h.Builder.Build(Parse(Document));

        Assert.False(h.Builder.GpuDriven.ObjectRecords);
        Assert.False(h.Lighting.UseRecords);
    }

    /// <summary>
    ///     A material feature with no device of its own is given the builder's.
    /// </summary>
    /// <remarks>
    ///     ⚠ A feature with no device cannot answer the capability question and so answers no — which
    ///     would make a document that asked for records get none, silently, on the machine that has
    ///     them. A host that set a device on the builder and not on the feature meant the same device.
    /// </remarks>
    [Fact]
    public void A_feature_with_no_device_is_given_the_builders() {
        using var h = Build(devices: false);

        Assert.Null(h.Materials.Device);
        h.Builder.Build(Parse(Document));

        Assert.Same(device, h.Materials.Device);
        Assert.True(h.Materials.UseRecords);
    }

    // --- The fixture --------------------------------------------------------

    static PermutationKey<bool> Permutation(string name) => ParameterKeys.NewPermutation(false, name);

    static GraphicsCompositorAsset Parse(string document) =>
        YamlSerializer.Parse<GraphicsCompositorAsset>(document);

    Harness Build(bool devices = true) {
        var system = new RenderSystem();

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects, Device = devices ? device : null };
        var transforms = new TransformRenderFeature { Device = devices ? device : null };
        var lighting = new ForwardLightingRenderFeature { Device = devices ? device : null, Clustered = true };

        // The lighting feature after the transform one, so the builder's second pass is doing work
        // rather than being saved by the order a fixture happened to add them in.
        meshes.Add(lighting);
        meshes.Add(transforms);
        meshes.Add(materials);
        system.AddFeature(meshes);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var builder = new CompositorBuilder(system) { Device = device };
        builder.Views["Camera"] = new RenderView("camera") {
            Position = Vector3.Zero, Frustum = new(view * projection)
        };

        return new() {
            System = system,
            Builder = builder,
            Meshes = meshes,
            Materials = materials,
            Transforms = transforms,
            Lighting = lighting
        };
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required CompositorBuilder Builder { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required TransformRenderFeature Transforms { get; init; }
        public required ForwardLightingRenderFeature Lighting { get; init; }

        public void Dispose() => System.Dispose();
    }

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => new() { Key = key, Stages = Modules };

        static ImmutableArray<EffectStage> Modules =>
            [new(ShaderStage.Vertex, [1, 2, 3, 4], "main"), new(ShaderStage.Fragment, [5, 6, 7, 8], "main")];
    }
}
