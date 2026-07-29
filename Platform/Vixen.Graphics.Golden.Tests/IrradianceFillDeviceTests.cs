// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Doc 19 § L2's filler A, dispatched on a device and read back.
/// </summary>
/// <remarks>
///     <para>
///         <b>Not an image test, and deliberately.</b> A picture of a lit frame answers "does the whole
///         path work", which is what <see cref="IndirectDiffuseImageTests" /> is for; it cannot
///         distinguish a dispatch that wrote nothing from one that wrote somewhere else, because both
///         draw the same unlit frame. This reads the texels the sampler would read and holds them
///         against <see cref="TracedIrradianceFiller" />'s answer for the same bricks — which is the
///         only check that says <i>which</i> of those two happened.
///     </para>
///     <para>
///         <b>The reference is the CPU filler, not a closed form, and that is the stronger test.</b>
///         The closed form for a uniform sky constrains only the constant coefficient — the three
///         linear ones integrate to zero and any transposition or sign error among them is invisible
///         to it. Sixty-four Fibonacci directions do not sum to exactly zero, so the reference's
///         linear coefficients are small nonzero numbers that a wrong shader has no way of
///         reproducing. Comparing against them is comparing against the whole payload.
///     </para>
///     <para>
///         <b><c>NoDistanceField</c> fills the slot.</b> The composition matters for what is being
///         tested: with it, every ray reaches the sky and the answer depends on the projection, the
///         addressing and the packing alone. A clipmap behind the slot would add its own upload, its
///         own sampling and its own tolerance to a test about none of those. What it does exercise is
///         the empty set 0 — a variant whose composed source declares no resources at all, which is
///         the case a fill node that assumed a set 0 would get wrong.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class IrradianceFillDeviceTests {
    /// <summary>How bright the sky is.</summary>
    /// <remarks>Not one, so a shader that ignored <c>skyColor</c> would be caught rather than passed.</remarks>
    const float Radiance = 0.75f;

    /// <summary>
    ///     Every probe the dispatch wrote is the probe the reference filler would have written.
    /// </summary>
    [Fact]
    public void TheDispatchAgreesWithTheReferenceFiller() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var reference = Field();
        var device = Field();

        new TracedIrradianceFiller(new EmptyWorld(), new UniformSky(Radiance)).Fill(reference);

        var probes = Dispatch(owned, device, out var filled, out var skipped);

        Assert.Null(skipped);
        Assert.Equal(device.BrickCount, filled);

        // ⚠ Against the pool's own texels rather than against GetProbe, because a brick's slot is the
        // dispatch's to write and this is checking that it wrote the right one: comparing through the
        // field's addressing on both sides would agree even if every brick had landed in its
        // neighbour's slot.
        var compared = 0;

        foreach (var brick in device.Bricks) {
            for (var z = 0; z < IrradianceBrickPool.BrickResolution; z++) {
                for (var y = 0; y < IrradianceBrickPool.BrickResolution; y++) {
                    for (var x = 0; x < IrradianceBrickPool.BrickResolution; x++) {
                        var origin = device.Pool.OriginOf(brick.Slot);
                        var texel = Texel(device, origin.X + x, origin.Y + y, origin.Z + z);

                        Same(reference.GetProbe(brick, x, y, z), probes[texel], $"probe {x},{y},{z} of slot {brick.Slot}");
                        compared++;
                    }
                }
            }
        }

        Assert.Equal(device.BrickCount * 64, compared);
    }

    /// <summary>
    ///     <b>And a border texel the dispatch did not write stays unwritten.</b>
    /// </summary>
    /// <remarks>
    ///     The point of asserting it rather than leaving it implicit: a device-filled pool has no
    ///     border sync and no dilation, so the fifth plane of every brick holds whatever the texture
    ///     was created with. That is a real seam and the next thing owed on this path — a test that
    ///     quietly tolerated a written border would be a test that stopped noticing when one appeared
    ///     for the wrong reason.
    /// </remarks>
    [Fact]
    public void TheBorderPlaneIsLeftToTheSyncThatDoesNotExistYet() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var field = Field();
        var probes = Dispatch(owned, field, out var filled, out var skipped);

        Assert.Null(skipped);
        Assert.True(filled > 0);

        var brick = field.Bricks.First();
        var origin = field.Pool.OriginOf(brick.Slot);
        var border = probes[Texel(field, origin.X + IrradianceBrickPool.BrickResolution, origin.Y, origin.Z)];
        var interior = probes[Texel(field, origin.X, origin.Y, origin.Z)];

        Assert.NotEqual(0f, interior.Validity);
        Assert.Equal(0f, border.Validity);
    }

    /// <summary>Runs one dispatch over a whole field and reads the pool back.</summary>
    static IrradianceProbe[] Dispatch(Fixture fixture, IrradianceField field, out int filled, out string? skipped) {
        var device = fixture.Device;

        using var allocator = new DescriptorAllocator(device);
        using var texture = new IrradianceFieldTexture(field) { PoolIsWritten = true };

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        // The whole IrradianceFields package, which is where IrradianceFill lives — and DistanceFields
        // beside it, because the fill declares a slot and NoDistanceField is what fills it.
        effects.AddProvider(
            new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "IrradianceFields"]))
        );

        using var fill = new IrradianceFieldFill(device) {
            Effects = effects,
            Pipelines = new ComputePipelineCache(device),
            Descriptors = allocator,
            SkyColour = new(Radiance)
        };

        var probes = new IrradianceProbe[field.Pool.Texels.Length];

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "irradiance fill")) {
            // The index volume, and the textures themselves — a dispatch before the first upload has
            // nothing to write into.
            texture.Upload(device, commands);

            filled = fill.Record(commands, field, texture, field.BrickCount);

            Assert.True(texture.RecordReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        skipped = fill.Skipped;

        Assert.Empty(effects.Misses);
        Assert.True(texture.TryRead(probes));

        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The dispatch produced validation errors, so what it wrote is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        return probes;
    }

    /// <summary>A field small enough to fill whole and large enough to have more than one brick.</summary>
    static IrradianceField Field() {
        var field = new IrradianceField(new BoundingBox(new(-2f), new(2f)), new(2));

        field.AllocateAll();

        return field;
    }

    /// <summary>Where one pool texel is in the flat array a readback produces.</summary>
    static int Texel(IrradianceField field, int x, int y, int z) {
        var resolution = field.Pool.TexelResolution;

        return (((z * resolution.Y) + y) * resolution.X) + x;
    }

    /// <summary>Whether two probes are the same answer, allowing for two different machines' maths.</summary>
    /// <remarks>
    ///     Absolute rather than relative, and the tolerance is what a single-precision sum of
    ///     sixty-four terms costs when the two sides accumulate in a different order — the shader's
    ///     <c>sin</c> and <c>cos</c> are the driver's rather than the CLR's. Anything structurally
    ///     wrong is off by far more than this: a transposed pack swaps whole coefficients, and a
    ///     misaddressed brick reads a texel that was never written at all.
    /// </remarks>
    static void Same(IrradianceProbe expected, IrradianceProbe actual, string what) {
        const float Tolerance = 1e-4f;

        Assert.Equal(expected.Validity, actual.Validity, Tolerance);
        Assert.Equal(expected.SunShadow, actual.SunShadow, Tolerance);

        Same(expected.Radiance.L00, actual.Radiance.L00, $"{what} L00");
        Same(expected.Radiance.L1m1, actual.Radiance.L1m1, $"{what} L1m1");
        Same(expected.Radiance.L10, actual.Radiance.L10, $"{what} L10");
        Same(expected.Radiance.L11, actual.Radiance.L11, $"{what} L11");

        static void Same(Vector3 expected, Vector3 actual, string what) {
            Assert.True(
                MathF.Abs(expected.X - actual.X) < Tolerance
                && MathF.Abs(expected.Y - actual.Y) < Tolerance
                && MathF.Abs(expected.Z - actual.Z) < Tolerance,
                $"{what}: the reference filler says {expected} and the dispatch wrote {actual}"
            );
        }
    }

    /// <summary>A world with nothing in it, so every ray reaches the sky.</summary>
    /// <remarks>
    ///     The CPU's counterpart to <c>NoDistanceField</c>, which reports the same thing with a
    ///     different number. Either is further than any march is allowed to go, which is all a tracer
    ///     reads off it.
    /// </remarks>
    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => Vector3.Zero;
    }

    /// <summary>One radiance from every direction, and surfaces that give back nothing.</summary>
    sealed class UniformSky(float radiance) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(radiance);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
