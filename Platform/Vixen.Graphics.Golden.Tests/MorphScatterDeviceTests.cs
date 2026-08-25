// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Blend shapes on the device — <a href="../../docs/plan/33-character-creator.md">doc 33</a> § D4's
///     compute pre-pass, held to <see cref="MorphKernel" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim is that a weight moves a vertex to a computed position, and it is checked
///         against arithmetic rather than against a picture.</b> A morph applied in the wrong space,
///         at the wrong stride, or with the weight folded in twice renders <em>plausibly</em> — the
///         face still has a face on it — so a golden image would pass on all three. What cannot pass
///         is a float that is not the float the host computed.
///     </para>
///     <para>
///         ⚠ <b>The shipped kernel, compiled out of <c>Raven/Library</c>, not a stand-in.</b> A mirror
///         written for the test would agree with the test. This is the same rule
///         <c>WaterSurfaceSeamDeviceTests</c> follows next door and for the same reason.
///     </para>
///     <para>
///         <b>Two legs, because they answer different questions.</b> The exact leg uses deltas the
///         quantiser cannot round and a weight of one, so the two processors must produce the identical
///         float — that is the leg an off-by-one-quantum or a wrong-lane bug fails. The tolerant leg
///         uses awkward weights and overlapping shapes, where a device is free to contract
///         <c>v + Δ·w</c> into a fused multiply-add and the host is not; a difference of one unit in
///         the last place is that freedom, and is not a defect.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class MorphScatterDeviceTests {
    /// <summary>How many floats one <see cref="SurfaceVertex" /> is, and where its two vectors are.</summary>
    const int Stride = 12;
    const int PositionOffset = 0;
    const int NormalOffset = 3;

    /// <summary>How many vertices the fixture mesh has.</summary>
    const int Vertices = 96;

    /// <summary>
    ///     What the fused multiply-add is worth on a quantity of order one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Measured on this fixture rather than chosen.</b> The three shapes together move a vertex
    ///     by about a unit, so one unit in the last place of a float of that size is about 1.2 × 10⁻⁷;
    ///     the bound is an order of magnitude above it, which is loose enough for a contraction and two
    ///     orders tighter than one quantum of the coarsest target — the smallest error a real defect
    ///     could produce.
    /// </remarks>
    const float Tolerance = 1e-6f;

    /// <summary>The quantiser's own step for a target whose range is <c>32767·2⁻¹⁵</c>.</summary>
    const float Unit = 1f / 32768f;

    static Vector3 Exact(int x, int y, int z) => new(x * Unit, y * Unit, z * Unit);

    /// <summary>A mesh whose vertices are all different, so a scatter at the wrong stride shows.</summary>
    /// <remarks>
    ///     ⚠ Every component is an exact multiple of <c>2⁻¹⁵</c>, so that <c>base + Δ</c> is a float
    ///     addition that needs no rounding and the exact leg can assert equality rather than nearness.
    /// </remarks>
    static SurfaceVertex[] Mesh() {
        var mesh = new SurfaceVertex[Vertices];

        for (var index = 0; index < Vertices; index++) {
            mesh[index] = new() {
                Position = Exact(index * 71, index * -37, index * 13),
                Normal = Exact(index * 5, 32768, index * -3),
                Tangent = new(1f, 0f, 0f, 1f),
                TexCoord = new(index * 0.01f, index * 0.02f)
            };
        }

        return mesh;
    }

    /// <summary>Three shapes: two of them move vertices the others do too.</summary>
    static MorphTargetData[] Targets() {
        int[] jaw = [3, 11, 12, 40, 41, 42, 90];
        int[] brow = [11, 12, 13, 60, 91];
        int[] crease = [12, 42, 95];

        return [
            MorphTargetData.Encode(
                "jawOpen",
                jaw,
                [.. jaw.Select(v => Exact(32767, -16384, 8192 + (v * 3)))],
                [.. jaw.Select(v => Exact(v * 7, -4096, 16384))]
            ),
            MorphTargetData.Encode(
                "browRaise",
                brow,
                [.. brow.Select(v => Exact(-8192, 32767, v * -5))],
                []
            ),
            MorphTargetData.Encode(
                "crease",
                crease,
                [.. crease.Select(_ => Vector3.Zero)],
                [.. crease.Select(v => Exact(v * 11, 32767, -1024))]
            )
        ];
    }

    // --- The claim ----------------------------------------------------------

    /// <summary>
    ///     ⚠ At a weight of one, every vertex the shapes name is exactly where <see cref="MorphKernel" />
    ///     put it, and every vertex they do not name is untouched.
    /// </summary>
    /// <remarks>
    ///     The whole feature, asserted as a float comparison. Exact rather than near — see the class
    ///     remarks on why the fixture can afford it — and covering the untouched vertices too, because
    ///     a scatter that wrote at the wrong stride lands on a plausible vertex rather than off the end
    ///     of the buffer.
    /// </remarks>
    [Fact]
    public void A_weight_of_one_moves_every_vertex_to_where_the_kernel_says() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        var mesh = Mesh();
        var targets = Targets();
        float[] weights = [1f, 1f, 1f];

        var expected = new SurfaceVertex[Vertices];
        MorphKernel.Apply(mesh, targets, weights, expected);

        var actual = Scatter(owned.Device, mesh, targets, weights);

        for (var index = 0; index < Vertices; index++) {
            Assert.True(
                Identical(expected[index], actual[index]),
                $"vertex {index}: expected position {expected[index].Position} normal "
                + $"{expected[index].Normal}, got position {actual[index].Position} normal "
                + $"{actual[index].Normal}"
            );
        }
    }

    /// <summary>
    ///     ⚠ And with awkward weights, including a negative one, to a stated tolerance.
    /// </summary>
    /// <remarks>
    ///     A negative weight is not a curiosity: an exporter that authored a shape as the inverse of
    ///     its neighbour relies on it, and a kernel that clamped to <c>[0, 1]</c> somewhere would pass
    ///     the leg above and fail here.
    /// </remarks>
    [Fact]
    public void Awkward_weights_agree_within_a_fused_multiply_add() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        var mesh = Mesh();
        var targets = Targets();
        float[] weights = [0.37f, -0.25f, 0.9137f];

        var expected = new SurfaceVertex[Vertices];
        MorphKernel.Apply(mesh, targets, weights, expected);

        var actual = Scatter(owned.Device, mesh, targets, weights);

        var worst = 0f;
        var worstAt = 0;

        for (var index = 0; index < Vertices; index++) {
            var apart = Apart(expected[index], actual[index]);

            if (apart > worst) {
                worst = apart;
                worstAt = index;
            }
        }

        Assert.True(
            worst <= Tolerance,
            $"the kernel and the shader differ by {worst} at vertex {worstAt}: expected position "
            + $"{expected[worstAt].Position} normal {expected[worstAt].Normal}, got position "
            + $"{actual[worstAt].Position} normal {actual[worstAt].Normal}. That is more than a "
            + "contraction is worth, so one of the two is doing different arithmetic."
        );
    }

    /// <summary>
    ///     ⚠ A weight of zero dispatches nothing, so the mesh comes back byte for byte.
    /// </summary>
    /// <remarks>
    ///     The state a character is in most of the time. It is asserted because "the buffer holds the
    ///     base mesh" is what a morph pass that never ran also produces — and the copy that puts the
    ///     base there is part of this pass, so the two are only the same answer by construction rather
    ///     than by accident.
    /// </remarks>
    [Fact]
    public void No_active_shape_leaves_the_mesh_exactly_as_it_was() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        var mesh = Mesh();
        var actual = Scatter(owned.Device, mesh, Targets(), [0f, 0f, 0f]);

        for (var index = 0; index < Vertices; index++) {
            Assert.True(Identical(mesh[index], actual[index]), $"vertex {index} moved with no weight on it");
        }
    }

    // --- The dispatch -------------------------------------------------------

    /// <summary>Uploads the mesh, dispatches one group per active target, reads the result back.</summary>
    /// <remarks>
    ///     ⚠ <b>A barrier between the dispatches, and it is not optional.</b> Two shapes may move the
    ///     same vertex, so pass <i>n+1</i> reads what pass <i>n</i> wrote at an address pass <i>n</i>
    ///     chose. Without the barrier the symptom is a face that is <em>almost</em> right, on some
    ///     frames, on some drivers.
    /// </remarks>
    static SurfaceVertex[] Scatter(
        VulkanDevice device,
        SurfaceVertex[] mesh,
        MorphTargetData[] targets,
        float[] weights
    ) {
        var effect = Compiled(device);
        var block = effect.BlockOf(DescriptorSetSlot.PerDraw);
        var layout = effect.SetLayouts[(int)DescriptorSetSlot.PerDraw];

        var meshBytes = MemoryMarshal.AsBytes(mesh.AsSpan()).ToArray();

        var vertices = device.CreateBuffer(new(
            meshBytes.Length,
            BufferUsage.Storage | BufferUsage.CopySource,
            MemoryAccess.HostUpload,
            "morph vertices"
        ));

        var readback = device.CreateBuffer(new(
            meshBytes.Length,
            BufferUsage.CopyDestination,
            MemoryAccess.HostReadback,
            "morph readback"
        ));

        device.Write(vertices, 0, meshBytes);

        var shader = device.CreateShader(
            ShaderStage.Compute,
            effect.Stages.Single(stage => stage.Stage == ShaderStage.Compute).Bytecode.AsSpan(),
            "MorphScatter"
        );

        var pipeline = device.CreateComputePipeline(new(shader, effect.Layout, "MorphScatter"));

        List<BufferHandle> owned = [];
        List<DescriptorSetHandle> sets = [];
        List<(DescriptorSetHandle Set, int Groups)> passes = [];

        for (var index = 0; index < targets.Length; index++) {
            if (weights[index] == 0f) {
                continue;
            }

            var target = targets[index];
            var words = MorphKernel.Pack(target);
            var entryBytes = MemoryMarshal.AsBytes(words.AsSpan()).ToArray();

            var entries = device.CreateBuffer(new(
                entryBytes.Length,
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                $"morph entries {target.Name}"
            ));

            device.Write(entries, 0, entryBytes);
            owned.Add(entries);

            var constants = new byte[Math.Max(16, block.Size)];

            Write(block, constants, "entryCount", target.Count);
            Write(block, constants, "baseVertex", 0);
            Write(block, constants, "stride", Stride);
            Write(block, constants, "positionOffset", PositionOffset);
            Write(block, constants, "normalOffset", NormalOffset);
            Write(block, constants, "weight", weights[index]);
            Write(block, constants, "positionStep", MorphKernel.Step(target.PositionScale));
            Write(block, constants, "normalStep", MorphKernel.Step(target.NormalScale));

            var uniforms = device.CreateBuffer(new(
                constants.Length,
                BufferUsage.Uniform,
                MemoryAccess.HostUpload,
                $"morph constants {target.Name}"
            ));

            device.Write(uniforms, 0, constants);
            owned.Add(uniforms);

            var set = device.CreateDescriptorSet(layout, $"morph {target.Name}");

            device.UpdateDescriptorSet(
                set,
                [
                    DescriptorWrite.Uniform(block.Binding, uniforms, 0, constants.Length),
                    DescriptorWrite.Storage(Binding(effect, "vertices"), vertices),
                    DescriptorWrite.Storage(Binding(effect, "entries"), entries)
                ]
            );

            sets.Add(set);
            passes.Add((set, (target.Count + 63) / 64));
        }

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Compute, "morph scatter")) {
            commands.Barrier(new([new(vertices, ResourceState.Undefined, ResourceState.ShaderWrite)], []));
            commands.BindPipeline(pipeline);

            foreach (var (set, groups) in passes) {
                commands.BindDescriptorSet(DescriptorSetSlot.PerDraw, set);
                commands.Dispatch(groups);
                commands.Barrier(new([new(vertices, ResourceState.ShaderWrite, ResourceState.ShaderWrite)], []));
            }

            commands.Barrier(new([new(vertices, ResourceState.ShaderWrite, ResourceState.CopySource)], []));
            commands.CopyBuffer(vertices, 0, readback, 0, meshBytes.Length);
            commands.Finish();
            device.ComputeQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var bytes = new byte[meshBytes.Length];
        device.Read(readback, 0, bytes);

        device.Destroy(pipeline);
        device.Destroy(shader);

        foreach (var set in sets) {
            device.Destroy(set);
        }

        foreach (var buffer in owned) {
            device.Destroy(buffer);
        }

        device.Destroy(readback);
        device.Destroy(vertices);

        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The dispatch produced validation errors, so what came back means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        return MemoryMarshal.Cast<byte, SurfaceVertex>(bytes).ToArray();
    }

    /// <summary>Compiles the shipped kernel against <c>Core</c> and nothing else.</summary>
    /// <remarks>
    ///     It composes no slot and imports only <c>Vixen.Shaders.Core</c>, so handing it the material
    ///     tree would make it compile nothing at all — a declared slot has to be bound whether or not
    ///     this shader reaches it.
    /// </remarks>
    static Effect Compiled(VulkanDevice device) {
        var data = RavenEffects.Only(["Core"], Path.Combine("Pipeline", "MorphScatter.rvn"))
            .TryGet(EffectKey.Of("MorphScatter"));

        Assert.NotNull(data);

        return new EffectLoader(device).Load(data!);
    }

    static void Write(EffectBlock declared, byte[] constants, string name, int value) =>
        BitConverter.TryWriteBytes(constants.AsSpan(Offset(declared, name)), value);

    static void Write(EffectBlock declared, byte[] constants, string name, float value) =>
        BitConverter.TryWriteBytes(constants.AsSpan(Offset(declared, name)), value);

    /// <summary>Where a constant is, from the shader's own reflection rather than from a table here.</summary>
    static int Offset(EffectBlock declared, string name) {
        var member = declared.Members.FirstOrDefault(m => Named(m.Key.Name, name));

        Assert.True(
            member.Key is not null,
            $"the kernel declares no '{name}': {string.Join(", ", declared.Members.Select(m => m.Key.Name))}"
        );

        return member.Offset;
    }

    /// <summary>Which binding the shader gave a name, rather than a number written down here.</summary>
    static uint Binding(Effect effect, string name) {
        var found = effect.Bindings.Where(binding => Named(binding.Name, name)).ToArray();

        Assert.True(
            found.Length == 1,
            $"the kernel has {found.Length} bindings called '{name}': "
            + string.Join(", ", effect.Bindings.Select(binding => binding.Name))
        );

        return found[0].Binding;
    }

    static bool Named(string declared, string name) =>
        declared == name || declared.EndsWith("." + name, StringComparison.Ordinal);

    /// <summary>Skips when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }

    static bool Identical(in SurfaceVertex expected, in SurfaceVertex actual) =>
        expected.Position.X == actual.Position.X
        && expected.Position.Y == actual.Position.Y
        && expected.Position.Z == actual.Position.Z
        && expected.Normal.X == actual.Normal.X
        && expected.Normal.Y == actual.Normal.Y
        && expected.Normal.Z == actual.Normal.Z;

    /// <summary>How far apart two vertices are, over the widest of the six components that move.</summary>
    /// <remarks>
    ///     ⚠ Absolute rather than relative, for the reason the water seam gives: a component near zero
    ///     because two shapes nearly cancelled is millions of units in the last place away from its
    ///     counterpart while being a nanometre away in the units anybody cares about.
    /// </remarks>
    static float Apart(in SurfaceVertex expected, in SurfaceVertex actual) {
        var widest = 0f;

        widest = MathF.Max(widest, MathF.Abs(expected.Position.X - actual.Position.X));
        widest = MathF.Max(widest, MathF.Abs(expected.Position.Y - actual.Position.Y));
        widest = MathF.Max(widest, MathF.Abs(expected.Position.Z - actual.Position.Z));
        widest = MathF.Max(widest, MathF.Abs(expected.Normal.X - actual.Normal.X));
        widest = MathF.Max(widest, MathF.Abs(expected.Normal.Y - actual.Normal.Y));
        widest = MathF.Max(widest, MathF.Abs(expected.Normal.Z - actual.Normal.Z));

        return float.IsNaN(widest) ? float.PositiveInfinity : widest;
    }
}
