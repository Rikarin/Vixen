// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     One forward frame, against the forward shader's own binding plan.
/// </summary>
/// <remarks>
///     <para>
///         The test that should have existed before any of the six commits that led to it. Each of
///         them was a disagreement between two halves that both looked right alone — the shader put
///         its bindings in four sets and <c>Effect</c> described one block; set 0 became bindable and
///         nothing bound it; the generator named the first block and the pass had four; the probe
///         index was written and the array was empty — and every one of them would have shown up here
///         as a frame that bound three sets instead of four.
///     </para>
///     <para>
///         <strong>The effect is built from <c>ForwardPlus.reflect.json</c> and loaded by the real
///         <see cref="EffectLoader" />.</strong> A hand-written fake would assert that the renderer
///         agrees with itself; this asserts it agrees with the shader, and it fails when someone adds
///         a binding to the <c>.rvn</c> that nothing fills. What is faked is the bytecode alone,
///         because compiling it needs a toolchain a unit test cannot assume.
///     </para>
/// </remarks>
public sealed class ForwardFrameTests : IDisposable {
    /// <summary>The forward shader's <c>MaxLights</c>, which sizes the per-object block.</summary>
    /// <remarks>
    ///     Sixteen, and the feature has to agree: the block is a count and sixteen lights, so a
    ///     feature that wrote eight would leave every draw reading half a block of stale memory. It is
    ///     asserted against the reflection below rather than trusted.
    /// </remarks>
    const int MaxLights = 16;

    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly SamplerCache samplers;
    readonly Effect effect;

    public ForwardFrameTests() {
        allocator = new(device);
        samplers = new(device);
        effect = new EffectLoader(device).Load(Reflected());
        effects.AddProvider(new Baked(effect));
    }

    public void Dispose() {
        samplers.Dispose();
        allocator.Dispose();
        device.Dispose();
    }

    /// <summary>Every variant is the one variant, which is what a baked bundle looks like from here.</summary>
    sealed class Baked(Effect effect) : IEffectProvider {
        public Effect? TryGet(EffectKey key) => effect;
    }

    // --- The shader's plan, as the runtime receives it ----------------------

    /// <summary>The forward pass as a baked effect, translated from its checked-in reflection.</summary>
    /// <remarks>
    ///     The same translation <c>Tools/Vixen.ShaderCompiler</c> does on the build side, reduced to
    ///     what a descriptor set needs: every binding with its set, index, kind, count and stages, and
    ///     every block member with its offset — array members expanded one key per element, which is
    ///     how <c>probeVolumes[2].radius</c> comes to be a name at all.
    /// </remarks>
    static EffectData Reflected() {
        var root = JsonDocument.Parse(File.ReadAllText(ReflectionPath())).RootElement;

        List<EffectBindingData> bindings = [];
        List<EffectParameterData> parameters = [];

        foreach (var set in root.GetProperty("Sets").EnumerateArray()) {
            var slot = (DescriptorSetSlot)set.GetProperty("Set").GetInt32();

            foreach (var binding in set.GetProperty("Bindings").EnumerateArray()) {
                bindings.Add(
                    new(
                        binding.GetProperty("Name").GetString()!,
                        slot,
                        (uint)binding.GetProperty("Binding").GetInt32(),
                        KindOf(binding.GetProperty("Type").GetString()!),
                        StagesOf(binding.GetProperty("Stages").GetString()!),
                        binding.GetProperty("Count").GetInt32(),
                        binding.GetProperty("Size").GetInt32()
                    )
                );

                Lengths(binding, out var lengths);

                foreach (var member in root.GetProperty("Parameters").EnumerateArray()) {
                    if (member.GetProperty("Set").GetInt32() != (int)slot
                        || member.GetProperty("Binding").GetInt32() != binding.GetProperty("Binding").GetInt32()) {
                        continue;
                    }

                    parameters.AddRange(Expand(member, lengths, slot));
                }
            }
        }

        return new() {
            ShaderName = "ForwardPlus",
            ConstantBufferSize = bindings.First(b => b.Set == DescriptorSetSlot.PerFrame).Size,
            Bindings = [.. bindings],
            Parameters = [.. parameters],
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
            ]
        };
    }

    /// <summary>How long each of a block's arrays is, by the array's name.</summary>
    static void Lengths(JsonElement binding, out Dictionary<string, int> lengths) {
        lengths = new(StringComparer.Ordinal);

        if (!binding.TryGetProperty("Members", out var members)) {
            return;
        }

        foreach (var member in members.EnumerateArray()) {
            var type = member.GetProperty("Type");

            if (type.TryGetProperty("ArrayLength", out var length)) {
                lengths[member.GetProperty("Name").GetString()!] = length.GetInt32();
            }
        }
    }

    /// <summary>One flattened member as the keys a host fills it through.</summary>
    static IEnumerable<EffectParameterData> Expand(
        JsonElement member,
        Dictionary<string, int> lengths,
        DescriptorSetSlot slot
    ) {
        var name = member.GetProperty("Name").GetString()!;
        var kind = ValueOf(member.GetProperty("Type"));

        if (kind == ShaderValueKind.Unknown) {
            // An aggregate — the array itself, or a struct. It has no value of its own; its leaves
            // are separate entries in the same list.
            yield break;
        }

        var offset = member.GetProperty("Offset").GetInt32();
        var size = member.GetProperty("Size").GetInt32();
        var marker = name.IndexOf("[]", StringComparison.Ordinal);

        if (marker < 0) {
            yield return new($"ForwardPlus.{name}", kind, offset, size, slot);
            yield break;
        }

        var stride = member.GetProperty("ArrayStride").GetInt32();
        var count = lengths.TryGetValue(name[..marker], out var length) ? length : 1;

        for (var index = 0; index < count; index++) {
            yield return new(
                $"ForwardPlus.{name[..marker]}[{index}]{name[(marker + 2)..]}",
                kind,
                offset + (index * stride),
                size,
                slot
            );
        }
    }

    static DescriptorKind KindOf(string type) =>
        type switch {
            "UniformBuffer" => DescriptorKind.UniformBuffer,
            "SampledTexture" => DescriptorKind.SampledTexture,
            "StorageTexture" => DescriptorKind.StorageTexture,
            "Sampler" => DescriptorKind.Sampler,
            _ => DescriptorKind.StorageBuffer
        };

    static ShaderStage StagesOf(string stages) {
        var result = ShaderStage.None;

        foreach (var stage in stages.Split(',', StringSplitOptions.TrimEntries)) {
            result |= stage switch {
                "Vertex" => ShaderStage.Vertex,
                "Fragment" => ShaderStage.Fragment,
                "Compute" => ShaderStage.Compute,
                _ => ShaderStage.None
            };
        }

        return result;
    }

    static ShaderValueKind ValueOf(JsonElement type) {
        if (type.GetProperty("IsStruct").GetBoolean() || type.GetProperty("IsArray").GetBoolean()) {
            return ShaderValueKind.Unknown;
        }

        var rows = type.GetProperty("Rows").GetInt32();

        if (type.GetProperty("IsMatrix").GetBoolean()) {
            return rows == 3 ? ShaderValueKind.Matrix3x3 : ShaderValueKind.Matrix4x4;
        }

        return type.GetProperty("Scalar").GetString() switch {
            "Int" => rows switch { 2 => ShaderValueKind.Int2, 3 => ShaderValueKind.Int3, 4 => ShaderValueKind.Int4, _ => ShaderValueKind.Int },
            "UInt" => ShaderValueKind.UInt,
            "Bool" => ShaderValueKind.Bool,
            _ => rows switch { 2 => ShaderValueKind.Float2, 3 => ShaderValueKind.Float3, 4 => ShaderValueKind.Float4, _ => ShaderValueKind.Float }
        };
    }

    static string ReflectionPath() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", "Pipeline", "ForwardPlus.reflect.json");

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Raven/Library/Pipeline/ForwardPlus.reflect.json was not found above "
            + $"'{AppContext.BaseDirectory}'. Regenerate it with VIXEN_REGENERATE=1 in Vixen.Raven.Tests."
        );
    }

    // --- The frame ----------------------------------------------------------

    sealed class Frame : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required ViewConstants View { get; init; }
        public required SceneConstants Scene { get; init; }
        public required SceneLighting Lighting { get; init; }
        public required ForwardLightingRenderFeature Lights { get; init; }
        public required RenderPassRenderer Pass { get; init; }
        public required ShadowMapRenderer Shadows { get; init; }

        public void Dispose() {
            Scene.Dispose();
            View.Dispose();
            System.Dispose();
        }
    }

    Frame Build() {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));
        var casters = system.AddStage(new("Shadow"));

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects, Device = device, Descriptors = allocator };
        // ⚠ The effect's own, not one the feature builds: a set bound at slot 3 is compatible only
        // with a layout identically defined to the one the pipeline was created with, and the feature
        // used to believe the block was fragment-only where the shader declares it for both stages.
        var lights = new ForwardLightingRenderFeature {
            Device = device,
            MaxLightsPerObject = MaxLights,
            Layout = effect.SetLayouts[(int)DescriptorSetSlot.PerDraw]
        };

        var transforms = new TransformRenderFeature { Device = device };

        meshes.Add(transforms);
        meshes.Add(materials);
        meshes.Add(lights);
        system.AddFeature(meshes);

        var camera = new RenderView("camera") {
            Camera = new(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f), MathF.PI / 3f, 16f / 9f, 0.1f, 1000f)
        };

        camera.Frustum = new(camera.ViewProjection);

        var view = new ViewConstants(device) {
            Descriptors = allocator,
            Layout = effect.SetLayouts[(int)DescriptorSetSlot.PerView]
        };

        var scene = new SceneConstants(device) { Descriptors = allocator };

        // The sun the shading pass reads and the sun the cascades are fitted to are the same object,
        // which is the whole reason ISunSource exists.
        lights.Lights.Add(RenderLight.Directional(new(-0.4f, -1f, -0.3f), new(1f, 0.95f, 0.9f), 3f));
        lights.Lights.Add(RenderLight.Point(new(0f, 1f, 9f), 20f, new(1f, 0.5f, 0.2f), 5f));

        var probes = new ReflectionProbeSelector();

        probes.Probes.Add(
            new() {
                Bounds = new(new(-8f, -8f, 2f), new(8f, 8f, 18f)),
                CapturePosition = new(0f, 0f, 10f),
                Prefiltered = Cube(),
                Sampler = samplers.LinearClamp,
                MipCount = 5
            }
        );

        var lighting = new SceneLighting {
            Environment = new() { Prefiltered = Cube(), Sampler = samplers.LinearClamp, MipCount = 7 },
            Probes = probes,
            Sun = lights,
            Camera = camera.Camera
        };

        scene.Lighting = lighting;
        lights.Probes = probes;

        // The buffers the features own, published by the features. Not frame resources, so no pass
        // has anything to say about them — and the transform buffer is published whether or not this
        // frame reads it, because `transforms` is declared by the shader either way and a set short
        // one entry is not bound at all.
        lights.Scene = scene.Parameters;
        transforms.Scene = scene.Parameters;

        // And the irradiance field's, for exactly the same reason one line up. The published variant
        // composes `IrradianceFieldProbes` into the pass's `irradiance` slot, so set 0 declares its two
        // volumes' worth of textures whether or not `UseIrradianceField` is on — and a set one entry
        // short is a set nothing binds. A project that composes `NoIrradiance` instead declares none of
        // them and needs none of this, which is what `MaterialCompiler` gives every material by default.
        //
        // No device is touched: `Apply` writes the handles the mirror holds, and before an upload those
        // are default ones. What is being checked here is the *names*, which is the half that fails
        // silently — a binding whose name nobody wrote is indistinguishable from a frame nobody drew.
        new IrradianceFieldTexture(new(new(new(-8f), new(8f)), new(2))).Apply(
            scene.Parameters,
            $"ForwardPlus.{MaterialCompiler.IrradianceFieldShader}"
        );

        var shadows = new ShadowMapRenderer {
            Name = "Shadows",
            CasterStage = casters,
            Atlas = "ShadowAtlas",
            Camera = camera,
            Sun = lights,
            Constants = view,
            Scene = scene.Parameters,
            Samplers = samplers
        };

        var pass = new RenderPassRenderer {
            Name = "Forward",
            SceneConstants = scene,
            Children = { new SingleStageRenderer { View = camera, Stage = opaque, Constants = view } }
        };

        pass.ColourTargets.Add("SceneColour");

        // The two frame resources set 0 wants, published by the pass that declared it reads them.
        pass.SceneTextures["shadowMap"] = "ShadowAtlas";
        pass.SceneBuffers["clusters"] = "Clusters";

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(1280, 720),
            Game = new SceneRendererSequence { Children = { shadows, pass } }
        };

        var colour = new TextureDescription(
            PixelFormat.Rgba16Float,
            1280,
            720,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: "SceneColour"
        );

        var target = device.CreateTexture(colour);
        compositor.Imports["SceneColour"] = new(target, device.CreateTextureView(target), colour);

        compositor.Resources.Add(
            new() {
                Name = "ShadowAtlas",
                Format = PixelFormat.Depth32Float,
                Width = shadows.AtlasSize.X,
                Height = shadows.AtlasSize.Y,
                Usage = TextureUsage.DepthStencilTarget | TextureUsage.Sampled
            }
        );

        // Imported rather than declared, because this frame has no culling pass to write it: the
        // graph refuses a declared resource that a pass reads and nobody produced, which is exactly
        // the check that keeps a forward pass from reading last frame's clusters.
        var clusters = new BufferDescription(ClusterGrid.BufferSize, BufferUsage.Storage, MemoryAccess.DeviceLocal, "Clusters");
        compositor.BufferImports["Clusters"] = new(device.CreateBuffer(clusters), clusters);

        var vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex });

        for (var i = 0; i < 3; i++) {
            Add(system, meshes, materials, opaque, casters, vertices, new(i * 2f, 0f, 10f));
        }

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            View = view,
            Scene = scene,
            Lighting = lighting,
            Lights = lights,
            Pass = pass,
            Shadows = shadows
        };
    }

    static void Add(
        RenderSystem system,
        MeshRenderFeature meshes,
        MaterialRenderFeature materials,
        RenderStage opaque,
        RenderStage casters,
        BufferHandle vertices,
        Vector3 at
    ) {
        var id = system.Objects.Add(
            new() { Bounds = new(at, 1f), Stages = opaque.Mask | casters.Mask, FeatureIndex = meshes.Index }
        );

        system.Objects.Data.Data(meshes.Draws)[id.Index] = new() {
            VertexBuffer = vertices, Count = 3, InstanceCount = 1
        };
        materials.Assign(system, id, new("ForwardPlus"));
    }

    TextureViewHandle Cube() =>
        device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 8, Height = 8, Depth = 1, MipLevels = 5, ArrayLayers = 6, SampleCount = 1,
                    Format = PixelFormat.Rgba16Float, Usage = TextureUsage.Sampled
                }
            )
        );

    void Record(Frame frame) {
        allocator.BeginFrame();

        var list = device.BeginCommandList();

        frame.Graph.Reset();
        frame.Compositor.Build(frame.Graph, effects, device);
        frame.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    static IEnumerable<long> SetsBound(NullDevice device) =>
        device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet).Select(command => command.A).Distinct();

    // --- The claim ----------------------------------------------------------

    /// <summary>
    ///     A forward frame binds all four of the shader's sets and draws.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Four sets, from four owners that never speak to each other: the scene's own objects, the
    ///         frame's camera, the material, and the object's light list. Every one of them resolves a
    ///         name through the shader's plan, and nothing anywhere writes down a binding index.
    ///     </para>
    ///     <para>
    ///         The frame also has to <em>complete</em> set 0, which is the part that was missing until
    ///         its last three pieces arrived: the atlas and the cluster list come from the pass that
    ///         declared it reads them, the light buffer from the feature that owns it, and the matrix,
    ///         the biases and the sampler from the node that rendered the shadows.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_forward_frame_binds_every_set_the_shader_declares() {
        using var frame = Build();

        Record(frame);

        Assert.True(frame.Scene.IsComplete);
        Assert.Equal(1, frame.Scene.WriteCount);

        var sets = SetsBound(device).Order().ToArray();

        Assert.Equal(
            [
                (long)DescriptorSetSlot.PerFrame,
                (long)DescriptorSetSlot.PerView,
                (long)DescriptorSetSlot.PerMaterial,
                (long)DescriptorSetSlot.PerDraw
            ],
            sets
        );

        Assert.True(device.Recorder!.CountOf(RecordedCommandKind.Draw) > 0);
    }

    /// <summary>
    ///     Set 0 is bound once for the run, and after the first pipeline.
    /// </summary>
    /// <remarks>
    ///     After, because <c>BindDescriptorSet</c> takes no pipeline layout and infers one from what
    ///     is bound — the Vulkan backend refuses a set before the first pipeline outright. Once,
    ///     because every pipeline in the frame is layout-compatible up to set 1, which covers set 0
    ///     with it.
    /// </remarks>
    [Fact]
    public void The_scenes_set_is_bound_once_and_after_a_pipeline() {
        using var frame = Build();

        Record(frame);

        var commands = device.Recorder!.Commands;
        var pipeline = -1;
        var scene = -1;
        var bound = 0;

        for (var i = 0; i < commands.Count; i++) {
            if (pipeline < 0 && commands[i].Kind == RecordedCommandKind.BindPipeline) {
                pipeline = i;
            }

            if (commands[i].Kind != RecordedCommandKind.BindDescriptorSet
                || commands[i].A != (long)DescriptorSetSlot.PerFrame) {
                continue;
            }

            bound++;

            if (scene < 0) {
                scene = i;
            }
        }

        Assert.Equal(1, bound);
        Assert.True(pipeline >= 0);
        Assert.True(pipeline < scene);
    }

    /// <summary>
    ///     A frame missing one of set 0's resources binds no set 0 at all.
    /// </summary>
    /// <remarks>
    ///     The pairing that makes the test above mean something, and the behaviour that turns a
    ///     forgotten hand-off into a visible failure rather than a silent one: a set is written wholly
    ///     or not at all, because a set with a hole in it is a validation error on one backend and a
    ///     sampled nothing on the next.
    /// </remarks>
    [Fact]
    public void A_frame_that_never_publishes_the_atlas_binds_no_scene_set() {
        using var frame = Build();

        frame.Pass.SceneTextures.Clear();

        Record(frame);

        Assert.False(frame.Scene.IsComplete);
        Assert.Equal(0, frame.Scene.WriteCount);
        Assert.DoesNotContain((long)DescriptorSetSlot.PerFrame, SetsBound(device));

        // And the rest of the frame is unharmed: the other three sets have other owners.
        Assert.Contains((long)DescriptorSetSlot.PerMaterial, SetsBound(device));
        Assert.True(device.Recorder!.CountOf(RecordedCommandKind.Draw) > 0);
    }

    /// <summary>
    ///     Every name the frame publishes is one the shader declares.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The general form of the failure this whole area keeps producing, asserted once for all
    ///         of its publishers. Six different types write into set 0 by string —
    ///         <see cref="SceneLighting" />, <see cref="EnvironmentLight.Apply" />,
    ///         <see cref="ReflectionProbe.Apply" />, <see cref="ClusterGrid.Apply" />,
    ///         <see cref="ShadowMapRenderer" /> and <see cref="ForwardLightingRenderFeature" /> — and
    ///         a typo in any of them is <em>silent</em>: the value is written, no binding claims it,
    ///         and the surface is lit by whatever the shader declared as a default.
    ///     </para>
    ///     <para>
    ///         So the assertion is not that a particular name is right but that <em>no</em> name is
    ///         orphaned. It notices a renamed uniform, an index written past an array's length, and a
    ///         publisher that was never updated when the shader moved on.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Nothing_the_frame_publishes_is_a_name_the_shader_does_not_have() {
        using var frame = Build();

        Record(frame);

        var declared = effect.Parameters
            .Select(parameter => parameter.Key.Name)
            .Concat(effect.Bindings.SelectMany(Names))
            .ToHashSet(StringComparer.Ordinal);

        var orphaned = frame.Scene.Parameters.Keys
            .Select(key => key.Name)
            .Where(name => !declared.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([], orphaned);

        // And the frame really did write something, so an empty collection cannot pass this.
        Assert.True(frame.Scene.Parameters.Count > 20);
    }

    /// <summary>The names one binding can be filled through — its own, and its elements'.</summary>
    static IEnumerable<string> Names(EffectBinding binding) {
        yield return $"ForwardPlus.{binding.Name}";

        for (var i = 0; i < binding.Count; i++) {
            yield return $"ForwardPlus.{binding.Name}[{i}]";
        }
    }

    /// <summary>
    ///     Every cascade the atlas holds is filled, and the shader has an array that long.
    /// </summary>
    /// <remarks>
    ///     <see cref="ShadowMapRenderer.CascadeCount" /> and the shader's <c>CascadeCount</c>
    ///     permutation size the same array from opposite sides, and nothing connects them — the same
    ///     shape as <c>MaxLights</c>, one array along. A host that fitted three into a block sized for
    ///     four leaves the last one a matrix nobody wrote, and a fragment far enough away projects
    ///     with it.
    /// </remarks>
    [Fact]
    public void Every_cascade_the_shader_declares_is_filled() {
        using var frame = Build();

        Record(frame);

        for (var i = 0; i < frame.Shadows.CascadeCount; i++) {
            var matrix = ParameterKeys.New<Matrix4x4>($"ForwardPlus.cascades[{i}].viewProjection");
            var split = ParameterKeys.New<float>($"ForwardPlus.cascades[{i}].split");

            Assert.True(frame.Scene.Parameters.Has(matrix), $"cascade {i} has no matrix");
            Assert.True(frame.Scene.Parameters.Has(split), $"cascade {i} has no split");

            // Near first, which is the order the shader's search assumes.
            if (i > 0) {
                var previous = ParameterKeys.New<float>($"ForwardPlus.cascades[{i - 1}].split");
                Assert.True(frame.Scene.Parameters.Get(split) > frame.Scene.Parameters.Get(previous));
            }
        }

        // The array is exactly as long as the host fills, which is what stops a fragment projecting
        // with a matrix nobody wrote.
        Assert.False(
            frame.Scene.Parameters.Has(
                ParameterKeys.New<Matrix4x4>($"ForwardPlus.cascades[{frame.Shadows.CascadeCount}].viewProjection")
            )
        );

        Assert.Equal(
            frame.Shadows.CascadeCount,
            effect.Parameters.Count(
                p => p.Key.Name.StartsWith("ForwardPlus.cascades[", StringComparison.Ordinal)
                    && p.Key.Name.EndsWith("].viewProjection", StringComparison.Ordinal)
            )
        );
    }

    /// <summary>
    ///     The shader selects a cascade the way the host's mirror does.
    /// </summary>
    /// <remarks>
    ///     A test that reads shader source, for what the reflection cannot see. Everything above binds
    ///     the host's <em>names</em> to the shader's declared parameters, so a rename or a resize
    ///     fails loudly — but the comparison itself is invisible to it, and
    ///     <see cref="ShadowCascades.CascadeOf" /> is a copy of it. Reversed, the host would fit
    ///     cascades for one set of distances and the shader would read them for another, which is a
    ///     shadow at the wrong resolution rather than a missing one.
    ///
    ///     Read from <c>ClusteredShading.rvn</c> rather than from the pass: the cascades and the light
    ///     loops moved to a base shader so a visibility-buffer resolve could reach them, and the forward
    ///     pass now inherits the selection rather than declaring it. Same arithmetic, one file over.
    /// </remarks>
    [Fact]
    public void The_shader_picks_a_cascade_the_way_the_host_says_it_does() {
        var source = File.ReadAllText(
            ReflectionPath().Replace("ForwardPlus.reflect.json", "ClusteredShading.rvn", StringComparison.Ordinal)
        );

        Assert.Contains("if (viewDepth <= cascades[i].split) {", source, StringComparison.Ordinal);
        Assert.Contains("var index = CascadeCount - 1", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The shared per-view block is never <em>smaller</em> than what a shader declares for set 1.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Set 1 is a contract between shaders rather than any one shader's business, so
    ///         <see cref="ViewConstants" /> configures it rather than taking it from an effect — which
    ///         means nothing connects the two but this. It was eighty bytes here against the shader's
    ///         144: a descriptor range shorter than the block it points at, which the validation
    ///         layers report and a release driver reads past.
    ///     </para>
    ///     <para>
    ///         <b>It is an inequality now and it used to be an equality</b>, because the block grew to
    ///         208 for <c>previousViewProjection</c> and <c>ForwardPlus</c> still declares 144. That
    ///         direction is safe and is what every pass here already relied on — <c>ShadowCaster</c>
    ///         declares one <c>mat4</c> of a block that has been three members long for a long time.
    ///         Only the shader that reads the last member declares the whole thing, and
    ///         <c>MotionVectorTests</c> is what holds its offset.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_shared_view_block_is_never_shorter_than_a_shader_declares() {
        using var view = new ViewConstants(device);

        Assert.True(
            view.Size >= effect.BlockOf(DescriptorSetSlot.PerView).Size,
            $"the block is {view.Size} bytes and the shader declares {effect.BlockOf(DescriptorSetSlot.PerView).Size}"
        );
    }

    /// <summary>
    ///     The per-object block the feature writes is the size the shader declares.
    /// </summary>
    /// <remarks>
    ///     <see cref="ForwardLightingRenderFeature.MaxLightsPerObject" /> and the shader's
    ///     <c>MaxLights</c> permutation size the same array from opposite sides, and nothing connects
    ///     them: a feature that wrote eight into a block sized for sixteen would leave every draw
    ///     shading with half a block of whatever was there before.
    /// </remarks>
    [Fact]
    public void The_per_object_block_is_the_size_the_shader_declared() {
        var block = effect.BlockOf(DescriptorSetSlot.PerDraw);

        Assert.Equal(ForwardLightingRenderFeature.HeaderSize + (MaxLights * 80), block.Size);
    }
}
