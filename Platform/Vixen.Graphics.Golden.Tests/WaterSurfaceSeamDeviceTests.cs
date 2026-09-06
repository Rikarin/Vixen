// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Vixen.Water;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The water surface, evaluated by both hosts and compared — [docs/plan/35 § D2].
/// </summary>
/// <remarks>
///     <para>
///         <b>The highest-value test in doc 35, and it is written before there is a renderer to see it
///         fail in.</b> The surface height at a position is defined once, as arithmetic over the
///         field's surface channel and the Gerstner sum at that position and time. That arithmetic
///         exists in exactly two places — <c>Vixen.Water.WaterEvaluator</c> and
///         <c>Raven/Library/Water/Surface.rvn</c> — and this is what holds them together.
///     </para>
///     <para>
///         <b>Why it exists.</b> Both engines doc 35 surveys evaluate the surface twice and neither
///         pins the two together, and the symptoms are the reason people believe water is hard: a boat
///         that hovers a hand's width above the crests in a swell, a character whose swimming state
///         flickers at the shoreline, a buoy that sinks when the frame rate drops. Unreal reaches for a
///         per-body <c>Max Wave Height Offset</c> to correct exactly that drift — a knob whose
///         existence is a bug report.
///     </para>
///     <para>
///         ⚠ <b>The stated tolerance is <see cref="Tolerance" /> metres, and doc 35 asked for exact.
///         This is the risk table's own fallback, taken, and here is the measurement behind it.</b>
///         Doc 35 § Risks says: "Exact float agreement between a C# evaluator and a SPIR-V one is a
///         real claim… If that turns out to be too expensive, the tolerance becomes a stated ULP bound
///         and the test still holds — what must not happen is the test being deleted." So: it is not
///         exact, this is what it is instead, and the reading is written down rather than rounded off.
///     </para>
///     <para>
///         <b>What the structural half bought.</b> Neither side calls <c>sin</c>: Vulkan allows
///         <c>OpSin</c> 8192 ULP over the useful range and a driver may implement it in a
///         special-function unit whose polynomial nobody has written down — at the phases a long
///         session reaches that is not a rounding difference, it is a different wave. Both sides call
///         the same stated polynomial over the same stated four-part range reduction, and what is left
///         is four orders of magnitude smaller than the intrinsic's own licence.
///     </para>
///     <para>
///         ⚠ <b>What is left, and why it is not zero.</b> The residue is the device's freedom to
///         contract a multiply and an add into one FMA — one rounding where the source writes two.
///         Vixen's own emitters do not contract, but the Metal translation layer this repository's
///         only Vulkan device runs behind is free to, and it does. It shows up in the <em>phase</em>
///         sum, which is the one quantity here that grows without bound: measured over four thousand
///         positions and times, the drift is <b>7 × 10⁻⁷ m</b> where the phase stays under a hundred
///         radians and <b>4.9 × 10⁻⁵ m</b> where it reaches five thousand — linear in the phase, which
///         is the signature of a one-ULP difference in the phase itself rather than of anything in the
///         sum. A tenth of a millimetre on a surface whose crests are metres, and it does not
///         accumulate over a session because the surface is a closed form rather than a simulation.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests: <see cref="VulkanDiagnostics" /> is
///         process-wide.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class WaterSurfaceSeamDeviceTests {
    /// <summary>How far apart the two evaluators may be, in metres. Measured, not guessed.</summary>
    /// <remarks>
    ///     Four times the worst reading over this test's own sweep, which is headroom for a driver
    ///     that contracts differently rather than licence for the arithmetic to change. ⚠ A change
    ///     that pushes past this is a change to one side of § D2 and not to the other — raise it only
    ///     with a new measurement and a sentence saying what moved.
    /// </remarks>
    const float Tolerance = 2e-4f;

    /// <summary>How many positions and times are compared.</summary>
    /// <remarks>
    ///     Past one subgroup on every part worth testing, and spread over a range no periodicity of
    ///     the wave sum divides: a grid aligned to a wavelength would compare the same phase over and
    ///     over and agree about all of them.
    /// </remarks>
    const int Queries = 4_096;

    /// <summary>One question, laid out as the shader declares it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Query {
        public Vector2 Position;
        public float WaterTime;
        public float Damping;
    }

    /// <summary>One answer, laid out as the shader declares it.</summary>
    /// <remarks>
    ///     Two <c>float4</c>s rather than seven floats: std430 pads a <c>float3</c> to sixteen bytes,
    ///     so the explicit padding is what the shader writes and not something this hopes about.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    struct Result {
        public Vector3 Offset;
        public float Pad;
        public Vector3 Normal;
        public float Pad2;
    }

    /// <summary>
    ///     ⚠ Every displacement and every normal agrees, to the stated tolerance.
    /// </summary>
    [Fact]
    public void The_shader_and_the_evaluator_agree_about_the_surface() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // A busy sea state at the largest quantised count, because the sum's order is part of what is
        // being compared and thirty-two terms is where a reassociation would show.
        var spectrum = WaterWaveSpectrum.Default with {
            Count = WaterWaveCount.ThirtyTwo,
            WindSpeed = 11f,
            DirectionalSpread = 0.9f,
            Steepness = 0.7f,
            Seed = 9u
        };

        var waves = new GerstnerWave[(int)spectrum.Count];
        var waveCount = spectrum.Generate(waves);

        var queries = Questions();
        var expected = new Result[Queries];
        var evaluator = new WaterEvaluator(null, waves.AsSpan(0, waveCount), WaterAttenuation.Default);

        for (var index = 0; index < Queries; index++) {
            var query = queries[index];

            evaluator.Displace(query.Position, query.WaterTime, query.Damping, out var offset, out var normal);
            expected[index] = new() { Offset = offset, Normal = normal };
        }

        var effect = Compiled(device);
        var actual = Dispatch(device, effect, waves.AsSpan(0, waveCount), queries);

        Clean();

        var worst = 0f;
        var worstAt = 0;

        for (var index = 0; index < Queries; index++) {
            var apart = Apart(expected[index], actual[index]);

            if (apart > worst) {
                worst = apart;
                worstAt = index;
            }
        }

        Assert.True(
            worst <= Tolerance,
            $"the shader and the evaluator differ by {worst} at query {worstAt} "
                + $"(position {queries[worstAt].Position}, t = {queries[worstAt].WaterTime}): "
                + $"expected {Describe(expected[worstAt])}, got {Describe(actual[worstAt])}. "
                + "The two sides of docs/plan/35 § D2 have drifted — which is what Unreal's "
                + "Max Wave Height Offset exists to paper over. Fix the arithmetic, or re-measure "
                + "and say in Tolerance's remarks what moved; do not delete this."
        );
    }

    /// <summary>
    ///     ⚠ And every quantised wave count agrees, not only the one somebody happened to try.
    /// </summary>
    /// <remarks>
    ///     Float addition is not associative, so a reordering that looks like a tidy-up — hoisting a
    ///     term, splitting the loop, accumulating the normal after the offset — is a different answer.
    ///     Eight waves and thirty-two are compared separately because a loop the compiler unrolls at
    ///     one count and not the other is exactly how the two shapes diverge, and the count is a shader
    ///     permutation precisely so that it can differ.
    /// </remarks>
    [Theory]
    [InlineData(WaterWaveCount.Eight)]
    [InlineData(WaterWaveCount.Sixteen)]
    [InlineData(WaterWaveCount.ThirtyTwo)]
    public void Every_quantised_wave_count_agrees(WaterWaveCount count) {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var spectrum = WaterWaveSpectrum.Default with { Count = count, Seed = 3u };
        var waves = new GerstnerWave[(int)count];
        var waveCount = spectrum.Generate(waves);

        var queries = Questions();
        var evaluator = new WaterEvaluator(null, waves.AsSpan(0, waveCount), WaterAttenuation.Default);
        var actual = Dispatch(device, Compiled(device), waves.AsSpan(0, waveCount), queries);

        Clean();

        for (var index = 0; index < Queries; index++) {
            var query = queries[index];

            evaluator.Displace(query.Position, query.WaterTime, query.Damping, out var offset, out var normal);

            var expected = new Result { Offset = offset, Normal = normal };
            var apart = Apart(expected, actual[index]);

            Assert.True(
                apart <= Tolerance,
                $"{(int)count} waves differ by {apart} at query {index}: "
                    + $"expected {Describe(expected)}, got {Describe(actual[index])}"
            );
        }
    }

    /// <summary>Positions, times and dampings spread over ranges nothing about the sum divides.</summary>
    /// <remarks>
    ///     ⚠ Irrational-ish strides rather than a grid. A grid aligned to a wavelength compares the
    ///     same phase four thousand times and agrees about all of them, which is a test that passes
    ///     because it asked one question.
    /// </remarks>
    static Query[] Questions() {
        var queries = new Query[Queries];

        for (var index = 0; index < Queries; index++) {
            queries[index] = new() {
                Position = new(index * 0.7331f, index * -0.4177f),
                WaterTime = index * 0.01913f,

                // The whole attenuation range, including both ends: zero damping is the shoreline,
                // where every term is multiplied away and a degenerate normal is a real possibility.
                Damping = index % 97 / 96f
            };
        }

        return queries;
    }

    /// <summary>Uploads, dispatches and reads back.</summary>
    static Result[] Dispatch(
        VulkanDevice device,
        Effect effect,
        ReadOnlySpan<GerstnerWave> waves,
        Query[] queries
    ) {
        var waveBytes = MemoryMarshal.AsBytes(waves).ToArray();
        var queryBytes = MemoryMarshal.AsBytes(queries.AsSpan()).ToArray();
        var resultBytes = Queries * Marshal.SizeOf<Result>();

        var waveBuffer = device.CreateBuffer(
            new(waveBytes.Length, BufferUsage.Storage, MemoryAccess.HostUpload, "water waves")
        );

        var queryBuffer = device.CreateBuffer(
            new(queryBytes.Length, BufferUsage.Storage, MemoryAccess.HostUpload, "water queries")
        );

        var resultBuffer = device.CreateBuffer(
            new(resultBytes, BufferUsage.Storage | BufferUsage.CopySource, MemoryAccess.DeviceLocal, "water results")
        );

        var readback = device.CreateBuffer(
            new(resultBytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "water readback")
        );

        device.Write(waveBuffer, 0, waveBytes);
        device.Write(queryBuffer, 0, queryBytes);

        // The counts, through the effect's own member offsets rather than at byte positions written
        // down here — the same reason every other device test in this directory does it that way.
        //
        // ⚠ Two blocks, because the sea state is PerFrame and the questions are PerDraw. Composing
        // BufferedWaterWaves brings its slot with it, and a test that assumed one block would write
        // the wave count into the query block's padding and dispatch a sum over zero waves.
        var perFrame = effect.BlockOf(DescriptorSetSlot.PerFrame);
        var perDraw = effect.BlockOf(DescriptorSetSlot.PerDraw);

        var frameConstants = new byte[Math.Max(4, perFrame.Size)];
        var drawConstants = new byte[Math.Max(4, perDraw.Size)];

        Write(perFrame, frameConstants, "waveCount", waves.Length);
        Write(perDraw, drawConstants, "queryCount", Queries);

        var frameBlock = device.CreateBuffer(
            new(Math.Max(frameConstants.Length, 16), BufferUsage.Uniform, MemoryAccess.HostUpload, "water sea state")
        );

        var drawBlock = device.CreateBuffer(
            new(Math.Max(drawConstants.Length, 16), BufferUsage.Uniform, MemoryAccess.HostUpload, "water constants")
        );

        device.Write(frameBlock, 0, frameConstants);
        device.Write(drawBlock, 0, drawConstants);

        var frameSet = device.CreateDescriptorSet(effect.SetLayouts[(int)DescriptorSetSlot.PerFrame], "water sea");
        var set = device.CreateDescriptorSet(effect.SetLayouts[(int)DescriptorSetSlot.PerDraw], "water probe");

        device.UpdateDescriptorSet(
            frameSet,
            [
                DescriptorWrite.Uniform(perFrame.Binding, frameBlock, 0, frameConstants.Length),
                DescriptorWrite.Storage(Binding(effect, "waves"), waveBuffer)
            ]
        );

        device.UpdateDescriptorSet(
            set,
            [
                DescriptorWrite.Uniform(perDraw.Binding, drawBlock, 0, drawConstants.Length),
                DescriptorWrite.Storage(Binding(effect, "queries"), queryBuffer),
                DescriptorWrite.Storage(Binding(effect, "results"), resultBuffer)
            ]
        );

        var shader = device.CreateShader(
            ShaderStage.Compute,
            effect.Stages.Single(stage => stage.Stage == ShaderStage.Compute).Bytecode.AsSpan(),
            "WaterSurfaceProbe"
        );

        PipelineHandle pipeline;

        try {
            pipeline = device.CreateComputePipeline(new(shader, effect.Layout, "WaterSurfaceProbe"));
        } catch (VulkanException error) {
            throw new InvalidOperationException(
                $"{error.Message} The layers said: {string.Join(Environment.NewLine, VulkanDiagnostics.Messages)}",
                error
            );
        }

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Compute, "water seam")) {
            commands.Barrier(new([new(resultBuffer, ResourceState.Undefined, ResourceState.ShaderWrite)], []));

            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, frameSet);
            commands.BindDescriptorSet(DescriptorSetSlot.PerDraw, set);
            commands.Dispatch((Queries + 63) / 64, 1, 1);

            commands.Barrier(new([new(resultBuffer, ResourceState.ShaderWrite, ResourceState.CopySource)], []));
            commands.CopyBuffer(resultBuffer, 0, readback, 0, resultBytes);
            commands.Finish();
            device.ComputeQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var bytes = new byte[resultBytes];
        device.Read(readback, 0, bytes);

        device.Destroy(pipeline);
        device.Destroy(shader);
        device.Destroy(readback);
        device.Destroy(resultBuffer);
        device.Destroy(queryBuffer);
        device.Destroy(waveBuffer);
        device.Destroy(drawBlock);
        device.Destroy(frameBlock);

        return MemoryMarshal.Cast<byte, Result>(bytes).ToArray();
    }

    static void Write(EffectBlock declared, byte[] constants, string name, int value) {
        var member = declared.Members.FirstOrDefault(m => Named(m.Key.Name, name));

        Assert.True(
            member.Key is not null,
            $"the probe declares no '{name}': {string.Join(", ", declared.Members.Select(m => m.Key.Name))}"
        );

        BitConverter.TryWriteBytes(constants.AsSpan(member.Offset), value);
    }

    /// <summary>Which binding the shader gave a name, rather than a number written down here.</summary>
    /// <remarks>
    ///     ⚠ By suffix, because a composed shader qualifies what its slot brought: the wave buffer is
    ///     <c>BufferedWaterWaves</c>' declaration and reaches the probe as <c>sea.waves</c>. Matching
    ///     the bare name finds nothing and fails a long way from the composition that renamed it.
    /// </remarks>
    static uint Binding(Effect effect, string name) {
        var found = effect.Bindings.Where(binding => Named(binding.Name, name)).ToArray();

        Assert.True(
            found.Length == 1,
            $"the probe has {found.Length} bindings called '{name}': "
                + string.Join(", ", effect.Bindings.Select(binding => binding.Name))
        );

        return found[0].Binding;
    }

    static bool Named(string declared, string name) =>
        declared == name || declared.EndsWith("." + name, StringComparison.Ordinal);

    /// <summary>Compiles <c>WaterSurfaceProbe.rvn</c> against the shipped library.</summary>
    /// <remarks>
    ///     <para>
    ///         Against <c>Core</c> and <em>one file</em> of the water package. The probe composes
    ///         nothing, and handing it the material tree would make it compile nothing at all — a slot
    ///         the sources declare has to be bound whether or not this shader reaches it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Surface.rvn</c> by name rather than the whole package.</b> The package also
    ///         holds the water <em>pass</em>, which imports <c>PostFx</c> for the fullscreen triangle
    ///         — so naming the package would drag the post-process tree into a test about a wave sum,
    ///         and the seam would start failing for reasons that have nothing to do with the seam.
    ///     </para>
    /// </remarks>
    static Effect Compiled(VulkanDevice device) {
        var path = Path.Combine(AppContext.BaseDirectory, "Shaders", "WaterSurfaceProbe.rvn");

        Assert.True(File.Exists(path), $"the probe shader is not beside the binary at {path}");

        var data = RavenEffects.Only(["Core"], Path.Combine("Water", "Surface.rvn"), path)
            .TryGet(EffectKey.Of("WaterSurfaceProbe"));

        Assert.NotNull(data);

        return new EffectLoader(device).Load(data!);
    }

    /// <summary>How far apart two answers are, in metres, over the widest of their six components.</summary>
    /// <remarks>
    ///     ⚠ <b>An absolute bound and not a ULP one, and the reason is cancellation.</b> A sum of
    ///     thirty-two terms with alternating signs produces components that are near zero because they
    ///     nearly cancelled, and a relative measure of a nearly-cancelled quantity says the two sides
    ///     differ by two million ULP when they differ by a nanometre. The surface's own units are
    ///     metres, the question is "would a boat sit somewhere else", and a metre is what that question
    ///     is asked in.
    /// </remarks>
    static float Apart(in Result expected, in Result actual) {
        var widest = 0f;

        widest = MathF.Max(widest, MathF.Abs(expected.Offset.X - actual.Offset.X));
        widest = MathF.Max(widest, MathF.Abs(expected.Offset.Y - actual.Offset.Y));
        widest = MathF.Max(widest, MathF.Abs(expected.Offset.Z - actual.Offset.Z));
        widest = MathF.Max(widest, MathF.Abs(expected.Normal.X - actual.Normal.X));
        widest = MathF.Max(widest, MathF.Abs(expected.Normal.Y - actual.Normal.Y));
        widest = MathF.Max(widest, MathF.Abs(expected.Normal.Z - actual.Normal.Z));

        return float.IsNaN(widest) ? float.PositiveInfinity : widest;
    }

    static string Describe(in Result value) =>
        $"offset {value.Offset.X}, {value.Offset.Y}, {value.Offset.Z} "
        + $"normal {value.Normal.X}, {value.Normal.Y}, {value.Normal.Z}";

    /// <summary>Refuses an answer produced alongside validation errors.</summary>
    static void Clean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The dispatch produced validation errors, so what came back means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }
}
