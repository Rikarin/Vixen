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
///     Dilation and the border sync, dispatched on a device and read back.
/// </summary>
/// <remarks>
///     <para>
///         <b>The repair is tested apart from the fill, and that is what makes it exact.</b> A fill on the
///         device marches <c>NoDistanceField</c> and leaves every probe valid, so dilation would have
///         nothing to do; giving it geometry means giving the shader a clipmap, and a clipmap is a
///         <i>sampled</i> field whose answer differs from the CPU's analytic one by its own resolution. The
///         comparison would then be against a tolerance nobody could attribute.
///     </para>
///     <para>
///         So the pool is <b>seeded</b> instead. The reference filler traces an analytic sphere on the CPU,
///         which leaves a ball of invalid probes and a rind of valid ones around it; that pool is copied up;
///         and the repair runs on it. Both sides then start from bit-identical probes and the only thing
///         under test is the repair — which is the arrangement that lets this assert every one of a brick's
///         hundred and twenty-five texels rather than the sixty-four a fill writes.
///     </para>
///     <para>
///         <b>Seeding and dispatching into one pool is not a test-only shape.</b> It is doc 19's two fillers
///         meeting: filler B writes a pool offline for a target with no compute, and a target that has
///         compute refines the same pool from the same bricks. The mirror carries both usages for that
///         reason, and this is the first thing to need it.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class IrradianceRepairDeviceTests {
    /// <summary>How bright the sky is.</summary>
    const float Radiance = 0.75f;

    /// <summary>What a border texel holds before the repair — a value no correct sync can produce.</summary>
    const float Poison = 4096f;

    /// <summary>
    ///     Every texel the repair wrote is the texel the reference repair would have written.
    /// </summary>
    /// <param name="refined">Whether to split a brick first, so two size classes are in play.</param>
    /// <remarks>
    ///     Refined is the case worth having: the border sync orders its dispatches coarsest first
    ///     <i>because</i> a fine brick borrows across a size boundary by interpolating the coarse one's own
    ///     field, and a uniform field never exercises that at all. It is also where the eight explicit
    ///     loads standing in for a trilinear fetch either agree with <c>IrradianceBrickPool.Sample</c> or
    ///     do not.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheDispatchedRepairAgreesWithTheReferenceRepair(bool refined) {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var reference = Seeded(refined);
        var device = Seeded(refined);

        // The reference answer, in the order the field itself insists on: a border is a copy, so
        // copying before the original is repaired copies the hole.
        var dilated = reference.Dilate();

        reference.SyncBorders();

        // ⚠ Asserted, because a repair with nothing to repair is a test that passes for the wrong
        // reason — and every assertion below would then be comparing two untouched pools.
        Assert.True(dilated > 0, "the seeded field has no invalid probes, so dilation proves nothing");

        var probes = Repair(owned, device, out var repaired, out var skipped, out var dispatches);

        Assert.Null(skipped);
        Assert.Equal(device.BrickCount, repaired);

        // Two per dilation pass, plus three per size class — the border sync is a dispatch per rank.
        Assert.Equal(refined ? 8 : 5, dispatches);

        var compared = 0;

        foreach (var brick in device.Bricks) {
            var origin = device.Pool.OriginOf(brick.Slot);

            // The whole padded footprint, borders included — which is the half a fill cannot be checked
            // on, because a fill does not write it.
            for (var z = 0; z <= IrradianceBrickPool.BrickResolution; z++) {
                for (var y = 0; y <= IrradianceBrickPool.BrickResolution; y++) {
                    for (var x = 0; x <= IrradianceBrickPool.BrickResolution; x++) {
                        var texel = Texel(device, origin.X + x, origin.Y + y, origin.Z + z);

                        Same(
                            reference.GetProbe(brick, x, y, z),
                            probes[texel],
                            $"texel {x},{y},{z} of slot {brick.Slot}"
                        );

                        compared++;
                    }
                }
            }
        }

        Assert.Equal(device.BrickCount * 125, compared);
    }

    /// <summary>Seeds the pool, runs the repair on the device, and reads the pool back.</summary>
    static IrradianceProbe[] Repair(
        Fixture fixture,
        IrradianceField field,
        out int repaired,
        out string? skipped,
        out int dispatches
    ) {
        var device = fixture.Device;

        using var allocator = new DescriptorAllocator(device);

        // PoolIsWritten is deliberately *off*: the upload is what seeds the pool, and this is exactly the
        // hand-over that makes the two fillers one field.
        using var texture = new IrradianceFieldTexture(field);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "IrradianceFields"])));

        using var repair = new IrradianceFieldRepair(device) {
            Effects = effects,
            Pipelines = new ComputePipelineCache(device),
            Descriptors = allocator
        };

        var probes = new IrradianceProbe[field.Pool.Texels.Length];

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "irradiance repair")) {
            texture.Upload(device, commands);

            // The upload leaves the pool readable; the repair writes it. Bracketing is the caller's,
            // because in a real frame the fill dispatch is inside the same pair.
            texture.TransitionPool(commands, ResourceState.ShaderRead, IrradianceFieldTexture.PoolIsBeingWritten);

            repaired = repair.Record(commands, field, texture);

            texture.TransitionPool(commands, IrradianceFieldTexture.PoolIsBeingWritten, ResourceState.ShaderRead);

            Assert.True(texture.RecordReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        skipped = repair.Skipped;
        dispatches = repair.Dispatches;

        Assert.Empty(effects.Misses);
        Assert.True(texture.TryRead(probes));

        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The repair produced validation errors, so what it wrote is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        return probes;
    }

    /// <summary>A field whose probes have been traced against a sphere, so some of them are invalid.</summary>
    /// <param name="refined">Whether to split one corner into finer bricks, leaving two size classes.</param>
    /// <remarks>
    ///     ⚠ Four cells and a refinement of one octant, not two cells and a refinement of everything. A
    ///     grid one brick across is refined into finer bricks and no coarse ones — one size class again,
    ///     and the ordering the refined case exists to exercise is never reached. The dispatch count is
    ///     asserted for exactly that reason: it is the cheapest thing that notices.
    /// </remarks>
    static IrradianceField Seeded(bool refined) {
        var field = new IrradianceField(new BoundingBox(new(-2f), new(2f)), new(4), new(new(4)));

        field.AllocateAll(refined ? 2 : 1);

        if (refined) {
            field.Refine(new(new(-2f), new(-1.5f)));
        }

        new TracedIrradianceFiller(new Ball(), new UniformSky(Radiance)).Fill(field);

        // Every border texel poisoned, because a border texel can be the source of another border
        // texel's copy — an edge texel of a brick at the grid's outer face reads its neighbour's
        // border plane — and the copy has to be of what this repair wrote, not of what the pool held
        // before. A dispatch that reads a border texel its own pass has not finished writing copies
        // either the poison or a value from the wrong moment, and the comparison sees both.
        var poison = new IrradianceProbe(
            new(new Vector3(Poison), Vector3.Zero, Vector3.Zero, Vector3.Zero),
            1f,
            0f
        );

        foreach (var brick in field.Bricks) {
            for (var z = 0; z <= IrradianceBrickPool.BrickResolution; z++) {
                for (var y = 0; y <= IrradianceBrickPool.BrickResolution; y++) {
                    for (var x = 0; x <= IrradianceBrickPool.BrickResolution; x++) {
                        if (x == IrradianceBrickPool.BrickResolution
                            || y == IrradianceBrickPool.BrickResolution
                            || z == IrradianceBrickPool.BrickResolution) {
                            field.Pool[brick.Slot, x, y, z] = poison;
                        }
                    }
                }
            }
        }

        return field;
    }

    /// <summary>Where one pool texel is in the flat array a readback produces.</summary>
    static int Texel(IrradianceField field, int x, int y, int z) {
        var resolution = field.Pool.TexelResolution;

        return (((z * resolution.Y) + y) * resolution.X) + x;
    }

    /// <summary>Whether two probes are the same answer, allowing for two different machines' maths.</summary>
    static void Same(IrradianceProbe expected, IrradianceProbe actual, string what) {
        const float Tolerance = 1e-4f;

        Assert.True(
            MathF.Abs(expected.Validity - actual.Validity) < Tolerance,
            $"{what} validity: the reference says {expected.Validity} and the dispatch wrote {actual.Validity}"
        );

        Assert.True(
            MathF.Abs(expected.SunShadow - actual.SunShadow) < Tolerance,
            $"{what} sun: the reference says {expected.SunShadow} and the dispatch wrote {actual.SunShadow}"
        );

        Same(expected.Radiance.L00, actual.Radiance.L00, $"{what} L00");
        Same(expected.Radiance.L1m1, actual.Radiance.L1m1, $"{what} L1m1");
        Same(expected.Radiance.L10, actual.Radiance.L10, $"{what} L10");
        Same(expected.Radiance.L11, actual.Radiance.L11, $"{what} L11");

        static void Same(Vector3 expected, Vector3 actual, string what) {
            Assert.True(
                MathF.Abs(expected.X - actual.X) < Tolerance
                && MathF.Abs(expected.Y - actual.Y) < Tolerance
                && MathF.Abs(expected.Z - actual.Z) < Tolerance,
                $"{what}: the reference says {expected} and the dispatch wrote {actual}"
            );
        }
    }

    /// <summary>A sphere at the origin, so the probes inside it are the ones nothing can fill.</summary>
    /// <remarks>
    ///     Exact rather than sampled, and that is what makes the comparison exact too: both sides read this
    ///     same C# object, so the seeded pool is bit-identical before either repair touches it.
    /// </remarks>
    sealed class Ball : IDistanceField {
        const float Radius = 1.1f;

        public float Sample(Vector3 position) => position.Length() - Radius;

        public Vector3 SampleGradient(Vector3 position) =>
            position.LengthSquared() > 1e-12f ? Vector3.Normalize(position) : Vector3.UnitY;
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
