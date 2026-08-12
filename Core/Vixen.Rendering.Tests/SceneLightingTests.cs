// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Features;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     The scene's lighting reaching the set that holds it.
/// </summary>
/// <remarks>
///     <para>
///         Everything either side of this had been built and nothing joined them.
///         <see cref="EnvironmentLight.Apply" /> and <see cref="ReflectionProbe.Apply" /> knew how to
///         write themselves; <see cref="ForwardLightingRenderFeature" /> wrote a probe
///         <em>index</em> per object; <c>ForwardPlus.rvn</c> declared an array of cubes for that
///         index to reach. No code put a probe in the array — so every object's index pointed into
///         descriptors nobody had written, which is a validation error on one backend and a device
///         loss on another.
///     </para>
///     <para>
///         Two claims, and the second is the one that cannot be checked from either side alone: the
///         array is filled <em>completely</em>, and slot <em>i</em> holds the probe that the feature
///         will name with the index <em>i</em>.
///     </para>
/// </remarks>
public sealed class SceneLightingTests : IDisposable {
    const uint BlockBinding = 0;
    const uint EnvironmentBinding = 2;
    const uint ProbesBinding = 3;
    const uint EnvironmentSamplerBinding = 5;
    const uint ProbeSamplerBinding = 6;

    /// <summary>What the shader declares room for, and what the tests bind against.</summary>
    const int Slots = 4;

    readonly NullDevice device = new(new() { Record = true });
    readonly List<EffectConstants> blocks = [];

    public void Dispose() {
        foreach (var block in blocks) {
            block.Dispose();
        }

        device.Dispose();
    }

    // --- Fixture ------------------------------------------------------------

    /// <summary>
    ///     A pass shaped like the forward one's set 0: a block, the sky, and an array of probes.
    /// </summary>
    /// <remarks>
    ///     Named <c>ForwardPlus</c> so the generated keys are the oracle for what the extract writes.
    ///     A fake naming its bindings something else would assert that the code agrees with itself.
    /// </remarks>
    static Effect Pass(int slots = Slots, bool probes = true, DescriptorSetLayoutHandle frame = default) {
        List<EffectBinding> bindings = [
            new("block", DescriptorSetSlot.PerFrame, BlockBinding, DescriptorKind.UniformBuffer) { Size = BlockSize },
            new("environment", DescriptorSetSlot.PerFrame, EnvironmentBinding, DescriptorKind.SampledTexture),
            new("environmentSampler", DescriptorSetSlot.PerFrame, EnvironmentSamplerBinding, DescriptorKind.Sampler)
        ];

        if (probes) {
            bindings.Add(
                new("probes", DescriptorSetSlot.PerFrame, ProbesBinding, DescriptorKind.SampledTexture) {
                    Count = slots
                }
            );

            bindings.Add(new("probeSampler", DescriptorSetSlot.PerFrame, ProbeSamplerBinding, DescriptorKind.Sampler));
        }

        return new() {
            Key = EffectKey.Of(ForwardPlusKeys.ShaderName),
            Stages = [],
            SetLayouts = [frame, default, default, default],
            ConstantBufferSize = BlockSize,
            Bindings = [.. bindings],
            Parameters = [.. Volumes()]
        };
    }

    /// <summary>The same pass with a real set-0 layout, so a frame can actually bind it.</summary>
    Effect Bindable() {
        var layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerFrame,
                [
                    new(BlockBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
                    new(EnvironmentBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(ProbesBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, Slots),
                    new(EnvironmentSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(ProbeSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "Scene"
            )
        );

        return Pass(frame: layout);
    }

    /// <summary>
    ///     The probe volumes as the block holds them, at Raven's own offsets.
    /// </summary>
    /// <remarks>
    ///     Element by element, which is what <c>EffectTranslator</c> expands the flattened
    ///     <c>probeVolumes[]</c> into on the way to a baked effect — one key per slot, at the base
    ///     offset plus the stride. Taken from the checked-in reflection rather than typed here, so a
    ///     change to the shader's block moves these with it.
    /// </remarks>
    static IEnumerable<EffectParameter> Volumes() {
        var (offset, stride) = Reflected("probeVolumes[].radius");

        for (var index = 0; index < Slots; index++) {
            yield return new(
                ParameterKeys.New<float>($"{ForwardPlusKeys.ShaderName}.probeVolumes[{index}].radius"),
                offset + (index * stride),
                4
            ) { Set = DescriptorSetSlot.PerFrame };
        }
    }

    /// <summary>How big set 0's block is, taken from the shader rather than typed here.</summary>
    /// <remarks>
    ///     A number that moves whenever the pass gains a uniform, and a block one member short is a
    ///     descriptor range shorter than what it points at. Reading it is free; remembering to update
    ///     it is not.
    /// </remarks>
    static int BlockSize {
        get {
            var reflection = JsonDocument.Parse(File.ReadAllText(ReflectionPath())).RootElement;

            foreach (var set in reflection.GetProperty("Sets").EnumerateArray()) {
                if (set.GetProperty("Set").GetInt32() == 0) {
                    return set.GetProperty("Bindings").EnumerateArray().First().GetProperty("Size").GetInt32();
                }
            }

            throw new InvalidOperationException("ForwardPlus.reflect.json has no set 0.");
        }
    }

    /// <summary>Where one flattened array member sits, and how far apart its elements are.</summary>
    static (int Offset, int Stride) Reflected(string name) {
        var reflection = JsonDocument.Parse(File.ReadAllText(ReflectionPath())).RootElement;

        foreach (var parameter in reflection.GetProperty("Parameters").EnumerateArray()) {
            if (parameter.GetProperty("Name").GetString() == name) {
                return (parameter.GetProperty("Offset").GetInt32(), parameter.GetProperty("ArrayStride").GetInt32());
            }
        }

        throw new InvalidOperationException($"'{name}' is not in ForwardPlus.reflect.json.");
    }

    /// <summary>The checked-in reflection, found by walking up rather than by counting directories.</summary>
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

    TextureViewHandle Cube() =>
        device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = 6, SampleCount = 1,
                    Format = PixelFormat.Rgba16Float, Usage = TextureUsage.Sampled
                }
            )
        );

    EnvironmentLight Sky() =>
        new() { Prefiltered = Cube(), Sampler = device.CreateSampler(new()), MipCount = 7, Intensity = 1f };

    ReflectionProbe Room(float z, float radius = 0f) =>
        new() {
            Bounds = new(new(-5f, -5f, z - 5f), new(5f, 5f, z + 5f)),
            CapturePosition = new(0f, 0f, z),
            Radius = radius,
            Prefiltered = Cube(),
            Sampler = device.CreateSampler(new()),
            MipCount = 5
        };

    /// <summary>The set's block, filled — so what a write test is measuring is the resources.</summary>
    /// <remarks>
    ///     Real rather than null, because a set is written wholly or not at all and a missing block
    ///     would make every one of these fail for a reason that is not the one under test.
    /// </remarks>
    EffectConstants Filled(Effect effect, ParameterCollection parameters) {
        var constants = new EffectConstants(device, "Scene");
        blocks.Add(constants);

        var block = effect.BlockOf(DescriptorSetSlot.PerFrame);
        constants.Update(effect, block.Size, block.Members.AsSpan(), parameters);

        return constants;
    }

    static ParameterKey<TextureViewHandle> Slot(int index) =>
        ParameterKeys.New<TextureViewHandle>($"{ForwardPlusKeys.ShaderName}.probes[{index}]");

    // --- Filling the array --------------------------------------------------

    /// <summary>
    ///     Every slot of the array is written, including the ones no probe occupies.
    /// </summary>
    /// <remarks>
    ///     The half a driver enforces. The shader samples
    ///     <c>probes[clamp(probeIndex, 0, ProbeCount - 1)]</c> and only weighs the result afterwards,
    ///     so a slot with no descriptor is read rather than skipped — and what an unwritten one reads
    ///     is undefined rather than black.
    /// </remarks>
    [Fact]
    public void Every_slot_of_the_probe_array_is_written() {
        var sky = Sky();

        var lighting = new SceneLighting { Environment = sky, Probes = new() };
        lighting.Probes!.Probes.Add(Room(10f));

        var parameters = new ParameterCollection();
        var effect = Pass();

        lighting.Extract(parameters, effect);

        List<DescriptorWrite> writes = [];

        Assert.True(
            EffectSetWriter.TryWrite(effect, DescriptorSetSlot.PerFrame, parameters, Filled(effect, parameters), writes)
        );

        var array = writes.Where(write => write.Binding == ProbesBinding).OrderBy(write => write.ArrayIndex).ToArray();

        Assert.Equal(Slots, array.Length);
        Assert.Equal([0, 1, 2, 3], array.Select(write => write.ArrayIndex));

        // The one probe there is takes slot zero; the three slots left over take the sky, which is
        // what a surface with no probe reflects anyway.
        Assert.Equal(lighting.Probes.Probes[0].Prefiltered, array[0].TextureView);
        Assert.Equal(sky.Prefiltered, array[1].TextureView);
        Assert.Equal(sky.Prefiltered, array[2].TextureView);
        Assert.Equal(sky.Prefiltered, array[3].TextureView);

        Assert.Equal(1, lighting.Bound);
        Assert.Equal(0, lighting.Dropped);
    }

    /// <summary>
    ///     An array one element short of complete binds nothing at all.
    /// </summary>
    /// <remarks>
    ///     The rule the whole writer is built on, at the one place an array makes it easy to break: a
    ///     set is written wholly or not at all, because a set with a hole in it is a validation error
    ///     on one backend and a sampled nothing on the next — and neither says which element was
    ///     missing.
    /// </remarks>
    [Fact]
    public void A_probe_array_with_a_hole_in_it_binds_nothing() {
        var parameters = new ParameterCollection();
        var effect = Pass();

        // Everything else the set wants, so the array is the only thing that can fail.
        parameters.Set(ForwardPlusKeys.Environment, Cube());
        parameters.Set(ForwardPlusKeys.EnvironmentSampler, device.CreateSampler(new()));
        parameters.Set(ForwardPlusKeys.ProbeSampler, device.CreateSampler(new()));

        // Two of four, and no whole-array fallback for the rest.
        parameters.Set(Slot(0), Cube());
        parameters.Set(Slot(1), Cube());

        List<DescriptorWrite> writes = [];
        var block = Filled(effect, parameters);

        Assert.False(EffectSetWriter.TryWrite(effect, DescriptorSetSlot.PerFrame, parameters, block, writes));

        // And the fourth one arriving is what completes it, rather than anything else changing.
        parameters.Set(Slot(2), Cube());
        parameters.Set(Slot(3), Cube());

        Assert.True(EffectSetWriter.TryWrite(effect, DescriptorSetSlot.PerFrame, parameters, block, writes));
    }

    /// <summary>
    ///     A variant compiled without probes binds none, and the extract writes none.
    /// </summary>
    /// <remarks>
    ///     <c>UseReflectionProbe</c> is a permutation because it changes the <em>bindings</em>: with it
    ///     off the cubes, their sampler and their volumes fold away entirely. A host that had to know
    ///     which variant it resolved before deciding what to write would be keeping the permutation
    ///     twice; the plan says so instead.
    /// </remarks>
    [Fact]
    public void A_variant_without_probes_is_filled_without_them() {
        var lighting = new SceneLighting { Environment = Sky(), Probes = new() };
        lighting.Probes!.Probes.Add(Room(10f));

        var parameters = new ParameterCollection();
        var effect = Pass(probes: false);

        lighting.Extract(parameters, effect);

        Assert.Equal(0, lighting.Slots);
        Assert.Equal(0, lighting.Bound);
        Assert.False(parameters.Has(Slot(0)));

        List<DescriptorWrite> writes = [];

        Assert.True(
            EffectSetWriter.TryWrite(effect, DescriptorSetSlot.PerFrame, parameters, Filled(effect, parameters), writes)
        );

        Assert.DoesNotContain(writes, write => write.Binding == ProbesBinding);
    }

    /// <summary>Probes the array has no room for are dropped, and counted.</summary>
    /// <remarks>
    ///     Not silent, because the failure is invisible from the frame: an object that selected the
    ///     fifth probe carries an index the shader clamps, so it reflects the wrong room rather than
    ///     nothing — which is the kind of thing that gets blamed on the capture.
    /// </remarks>
    [Fact]
    public void Probes_the_array_cannot_hold_are_counted() {
        var lighting = new SceneLighting { Environment = Sky(), Probes = new() };

        for (var i = 0; i < 6; i++) {
            lighting.Probes!.Probes.Add(Room(10f + (i * 20f)));
        }

        lighting.Extract(new(), Pass());

        Assert.Equal(Slots, lighting.Slots);
        Assert.Equal(Slots, lighting.Bound);
        Assert.Equal(2, lighting.Dropped);
    }

    // --- Agreeing with the object that picked ------------------------------

    /// <summary>
    ///     The slot an object's index names holds the probe that was chosen for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The claim neither side can make alone, and the reason the two read one list rather than
    ///         two equal ones. <see cref="ForwardLightingRenderFeature" /> writes a <em>position in
    ///         the selector's list</em> into the per-object block;
    ///         <see cref="SceneLighting" /> fills the array from that same list in that same order.
    ///         Sorting the probes on either side — by weight, by priority, by distance — would leave
    ///         both halves internally consistent and every object reflecting somebody else's room.
    ///     </para>
    ///     <para>
    ///         Asserted through the bytes the feature actually uploaded and the handle the writer
    ///         actually produced, rather than through either one's intent.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_slot_an_object_selected_holds_the_probe_it_chose() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature();
        var lighting = new ForwardLightingRenderFeature { Device = device };

        meshes.Add(lighting);
        system.AddFeature(meshes);

        var selector = new ReflectionProbeSelector();
        selector.Probes.Add(Room(10f));
        selector.Probes.Add(Room(30f));
        lighting.Probes = selector;

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        system.SetViews([new RenderView("camera") { Stages = opaque.Mask, Frustum = new(view * projection) }]);

        var near = Add(system, meshes, opaque, new(0f, 0f, 10f));
        var far = Add(system, meshes, opaque, new(0f, 0f, 30f));

        system.Draw();

        var scene = new SceneLighting { Environment = Sky(), Probes = selector };
        var parameters = new ParameterCollection();

        scene.Extract(parameters, Pass());

        // The index the object was given, read back out of the block that was uploaded for it.
        var chosen = ProbeOf(lighting, system, near);
        var other = ProbeOf(lighting, system, far);

        Assert.NotEqual(chosen, other);

        Assert.Equal(selector.Probes[0].Prefiltered, parameters.Get(Slot(chosen)));
        Assert.Equal(selector.Probes[1].Prefiltered, parameters.Get(Slot(other)));

        // And the volume beside it is that probe's too, which is what the parallax correction re-aims
        // from — a cube from one room corrected by another room's box is a reflection that slides.
        Assert.Equal(
            selector.Probes[1].MipCount,
            parameters.Get(ParameterKeys.New<float>($"{ForwardPlusKeys.ShaderName}.probeVolumes[{other}].mipCount"))
        );
    }

    /// <summary>
    ///     Each probe's volume lands in its own slot of the block, at Raven's offsets.
    /// </summary>
    /// <remarks>
    ///     The names agreeing is not the same as the bytes agreeing: the array's elements are one
    ///     parameter per slot at a stride the reflection states, so a volume written under the right
    ///     name and placed at the wrong offset is a probe correcting against the next probe's box.
    ///     This checks the bytes that went to the GPU.
    /// </remarks>
    [Fact]
    public void Each_probes_volume_lands_in_its_own_slot_of_the_block() {
        var lighting = new SceneLighting { Environment = Sky(), Probes = new() };

        // Radii a byte comparison cannot confuse: distinct, and none of them a default.
        lighting.Probes!.Probes.Add(Room(10f, 3f));
        lighting.Probes.Probes.Add(Room(30f, 7f));

        var parameters = new ParameterCollection();
        var effect = Pass();

        lighting.Extract(parameters, effect);

        using var constants = new EffectConstants(device, "Scene");
        var block = effect.BlockOf(DescriptorSetSlot.PerFrame);

        Assert.True(constants.Update(effect, block.Size, block.Members.AsSpan(), parameters));

        var (offset, stride) = Reflected("probeVolumes[].radius");

        Assert.Equal(3f, MemoryMarshal.Read<float>(constants.Bytes[offset..]));
        Assert.Equal(7f, MemoryMarshal.Read<float>(constants.Bytes[(offset + stride)..]));

        // The slots no probe filled keep the shader's own default rather than the last probe's, which
        // is what stops an object clamped into an empty slot reflecting a room that is not there.
        Assert.Equal(0f, MemoryMarshal.Read<float>(constants.Bytes[(offset + (2 * stride))..]));
    }

    // --- The sun ------------------------------------------------------------

    /// <summary>The sun the lighting feature found is the one the frame's block carries.</summary>
    /// <remarks>
    ///     Including its absence. The parameters outlive the frame that filled them, so a scene whose
    ///     sun was removed would go on being lit by it — and "no sun" is a value nobody thinks to set.
    /// </remarks>
    [Fact]
    public void The_sun_reaches_the_frames_block_and_so_does_its_absence() {
        var sun = new Sunlight {
            Sun = RenderLight.Directional(new(0f, -1f, 0f), new(1f, 0.5f, 0.25f), 3f)
        };

        var lighting = new SceneLighting { Sun = sun };
        var parameters = new ParameterCollection();

        lighting.Extract(parameters, Pass());

        Assert.Equal(new(0f, -1f, 0f), parameters.Get(ForwardPlusKeys.LightDirection));
        Assert.Equal(sun.Sun.Value.Radiance, parameters.Get(ForwardPlusKeys.LightColor));

        sun.Sun = null;
        lighting.Extract(parameters, Pass());

        Assert.Equal(Vector3.Zero, parameters.Get(ForwardPlusKeys.LightColor));
    }

    // --- The specular-ambient scale -----------------------------------------

    /// <summary>
    ///     The scale is written for every frame, sky or no sky, because the shader's declared
    ///     default is not a fallback.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <c>ClusteredShading.ambientSpecularScale</c> is declared <c>= 1f</c>, and that
    ///         number only reaches the buffer through the generated <c>ForwardPlusKeys</c> class.
    ///         This path interns its names as strings, which takes the <em>no-default</em> overload
    ///         — so a member nobody sets is zero, and the split path would silently lose every
    ///         surface's specular ambient rather than keeping it.
    ///     </para>
    ///     <para>
    ///         Which is why it is written outside <c>WriteEnvironment</c>: that method returns early
    ///         for a scene with no sky, and this member's zero does not mean "no sky", it means
    ///         "drop the term".
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_specular_ambient_scale_is_written_even_with_no_environment() {
        var lighting = new SceneLighting();
        var parameters = new ParameterCollection();

        lighting.Extract(parameters, Pass());

        Assert.Equal(1f, parameters.Get(ForwardPlusKeys.AmbientSpecularScale));

        // And the zero a reflections plane asks for arrives as a zero rather than as an absence.
        lighting.AmbientSpecular = 0f;
        lighting.Extract(parameters, Pass());

        Assert.Equal(0f, parameters.Get(ForwardPlusKeys.AmbientSpecularScale));
    }

    // --- In a frame ---------------------------------------------------------

    /// <summary>
    ///     A frame binds its own set without a host writing a single name down.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Where the hook belongs, and why it is on <see cref="SceneConstants" /> rather than in a
    ///         host's frame loop: the probe array's length is the <em>shader's</em>, and the bind is
    ///         where the shader is known. A host that had to size the array itself would be keeping
    ///         the <c>ProbeCount</c> permutation in two places.
    ///     </para>
    ///     <para>
    ///         Paired with its negative, because "it bound" is only interesting against a run where
    ///         nothing did: the same set, the same effect, no lighting, and the set stays unbound
    ///         rather than binding a hole.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_frame_binds_the_scenes_set_from_the_scenes_own_objects() {
        using var allocator = new DescriptorAllocator(device);
        var effect = Bindable();

        using var bare = new SceneConstants(device) { Descriptors = allocator };
        var list = device.BeginCommandList();
        allocator.BeginFrame();

        Assert.False(bare.Bind(list, effect));

        using var scene = new SceneConstants(device) {
            Descriptors = allocator,
            Lighting = new() { Environment = Sky(), Probes = new() }
        };

        scene.Lighting!.Probes!.Probes.Add(Room(10f));
        scene.Lighting.Probes.Probes.Add(Room(30f));

        Assert.True(scene.Bind(list, effect));
        Assert.True(scene.IsComplete);
        Assert.Equal(1, scene.WriteCount);

        // The extract ran off the effect's plan rather than off anything configured here.
        Assert.Equal(Slots, scene.Lighting.Slots);
        Assert.Equal(2, scene.Lighting.Bound);

        list.Finish();
    }

    /// <summary>
    ///     A frame with no lighting camera says so once, and a frame that has one says nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The degrade is right; the silence was not.</b> <see cref="SceneLighting.Camera" />
    ///         had exactly two writers in the whole tree and both were unit tests, so in every running
    ///         game <see cref="ClusterGrid.Apply" /> was never reached and every clustered fragment
    ///         looked itself up with <c>ClusteredShading.rvn</c>'s declared defaults — a 16:9 camera
    ///         at ninety degrees horizontal, which is a <em>plausible</em> grid for a camera nobody
    ///         has. Nothing about the picture said the numbers were missing.
    ///     </para>
    ///     <para>
    ///         Three claims, and the third is the one that makes a log line affordable:
    ///         <see cref="SceneLighting.Extract" /> runs per shading pass per frame, so a line per
    ///         call would be a line per pass per frame — which is a log nobody reads about a frame
    ///         nobody can profile.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_missing_lighting_camera_is_said_once_and_a_healthy_frame_says_nothing() {
        var effect = Pass();
        var log = new CaptureLogger();
        var parameters = new ParameterCollection();
        var key = ParameterKeys.New<Vector2>($"{ForwardPlusKeys.ShaderName}.tanHalfFov");

        var lighting = new SceneLighting {
            Logger = log,
            Camera = new RenderCamera(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, MathF.PI / 3f, 16f / 9f, 0.1f, 500f)
        };

        lighting.Extract(parameters, effect);

        // The healthy path: the grid is written, and nothing is said about it.
        Assert.True(parameters.Has(key));
        Assert.Empty(log.Lines);

        lighting.Camera = null;

        for (var pass = 0; pass < 64; pass++) {
            lighting.Extract(parameters, effect);
        }

        // ⚠ Once, not once per pass and not once per frame. Sixty-four extracts, one line.
        var line = Assert.Single(log.Lines);

        Assert.Equal(4004, line.Id);
        Assert.Equal(LogLevel.Warning, line.Level);

        // It names the input and the pass that noticed, so a reader gets a cause rather than a
        // symptom — the whole point of the line existing.
        Assert.Contains("SceneLighting.Camera", line.Message, StringComparison.Ordinal);
        Assert.Contains(ForwardPlusKeys.ShaderName, line.Message, StringComparison.Ordinal);

        // And it re-arms: a camera that comes back and goes away again is a second degrade, which is
        // a different event from the first and worth its own line.
        lighting.Camera = new RenderCamera(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, 1f, 1f, 0.1f, 500f);
        lighting.Extract(parameters, effect);

        Assert.Single(log.Lines);

        lighting.Camera = null;
        lighting.Extract(parameters, effect);

        Assert.Equal(2, log.Lines.Count);
    }

    /// <summary>A lighting with no logger degrades exactly as quietly as it always did.</summary>
    /// <remarks>
    ///     The other half of the contract, and the reason nothing in a test or a tool had to change:
    ///     a null logger is the default, and a frame built without one behaves identically.
    /// </remarks>
    [Fact]
    public void A_lighting_with_no_logger_still_degrades_in_silence() {
        var lighting = new SceneLighting();
        var parameters = new ParameterCollection();

        lighting.Extract(parameters, Pass());

        Assert.False(parameters.Has(ParameterKeys.New<Vector2>($"{ForwardPlusKeys.ShaderName}.tanHalfFov")));
    }

    /// <summary>Every line the extract wrote, with the id it wrote it under.</summary>
    sealed class CaptureLogger : ILogger {
        public List<(int Id, LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add((eventId.Id, logLevel, formatter(state, exception)));
    }

    sealed class Sunlight : ISunSource {
        public RenderLight? Sun { get; set; }
    }

    static RenderObjectId Add(RenderSystem system, MeshRenderFeature meshes, RenderStage opaque, Vector3 at) {
        var id = system.Objects.Add(new() { Bounds = new(at, 1f), Stages = opaque.Mask, FeatureIndex = meshes.Index });
        system.Objects.Data.Data(meshes.Draws)[id.Index] = new() { Count = 3, InstanceCount = 1 };

        return id;
    }

    static int ProbeOf(ForwardLightingRenderFeature lighting, RenderSystem system, RenderObjectId id) =>
        MemoryMarshal.Read<int>(lighting.Block(system, id)[ForwardLightingRenderFeature.ProbeIndexOffset..]);
}
