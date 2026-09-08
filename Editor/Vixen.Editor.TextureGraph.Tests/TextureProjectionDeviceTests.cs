// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § D10's triplanar and planar projection, on a real device, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>The closed forms this file rests on.</b> A world normal driven down one axis selects
///         that plane and <em>exactly</em> that plane, so the triplanar answer must equal the planar
///         one byte for byte; a normal at 45° between two axes is their mean, because the weights are
///         normalised; a planar axis ignores the normal entirely; and doubling the scale makes the
///         picture repeat, which is the wrap.
///     </para>
///     <para>
///         ⚠ <b>The one-plane form is what pins the <em>decode</em>, which is the error that produces
///         a plausible picture.</b> The <c>world</c> bake is signed — <c>0.5 + 0.5·n</c> — so a
///         kernel that forgot to decode reads +z as <c>(0.5, 0.5, 1)</c>, whose weights at a
///         sharpness of four are about <c>(0.06, 0.06, 0.89)</c>: still mostly the right plane, still
///         a fill that covers the mesh, and about a tenth of two other pictures mixed in. Nothing
///         about that looks like a bug. Requiring equality with the planar run is what makes it one.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is paired with the reason it is not vacuous.</b> "The three
///         planes agree" is trivially true of a kernel that ignores the position, so each test first
///         shows the two planes it is comparing are in fact different pictures.
///     </para>
/// </remarks>
public class TextureProjectionDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>A world normal pointing one way, encoded as the <c>world</c> bake writes it.</summary>
    static byte[] Facing(float x, float y, float z) {
        var pixels = new byte[Side * Side * 4];

        for (var at = 0; at < pixels.Length; at += 4) {
            pixels[at] = Encode(x);
            pixels[at + 1] = Encode(y);
            pixels[at + 2] = Encode(z);
            pixels[at + 3] = 255;
        }

        return pixels;
    }

    /// <summary>⚠ Signed, because <c>MeshMapBake.Vector</c> writes the world normal signed.</summary>
    static byte Encode(float value) => (byte)Math.Clamp(MathF.Round(((value * 0.5f) + 0.5f) * 255f), 0f, 255f);

    /// <summary>One world position everywhere, as the <c>position</c> bake writes it: unsigned, 0..1.</summary>
    static byte[] At(float x, float y, float z) {
        var pixels = new byte[Side * Side * 4];

        for (var at = 0; at < pixels.Length; at += 4) {
            pixels[at] = (byte)MathF.Round(x * 255f);
            pixels[at + 1] = (byte)MathF.Round(y * 255f);
            pixels[at + 2] = (byte)MathF.Round(z * 255f);
            pixels[at + 3] = 255;
        }

        return pixels;
    }

    /// <summary>A world position sweeping along x across the image, with y and z held.</summary>
    static byte[] Sweeping() {
        var pixels = new byte[Side * Side * 4];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = ((y * Side) + x) * 4;

                pixels[at] = (byte)(x * 255 / (Side - 1));
                pixels[at + 1] = 0;
                pixels[at + 2] = 0;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    ///     ⚠ A world normal down one axis selects that plane exactly — which is what a kernel that
    ///     forgot to decode the signed map cannot do.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fixture proves itself first: the x plane and the z plane are different
    ///         pictures.</b> They sample <c>(z, y)</c> and <c>(x, y)</c> of one position, so a
    ///         position whose x and z differ makes them different — and without that, a kernel that
    ///         ignored the normal entirely would satisfy the equality below.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Byte-for-byte and not a tolerance.</b> At a sharpness of four a decoded +z gives
    ///         the other two planes a weight of about 2·10⁻¹⁰, which is below an eight-bit step by
    ///         eight orders of magnitude; an undecoded one gives them a tenth each, which is not.
    ///         A tolerance wide enough to be comfortable would be wide enough to admit the bug.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_world_normal_down_one_axis_selects_that_plane_exactly() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var place = At(0.25f, 0.5f, 0.75f);

        var onX = Project(device, source, place, Facing(1f, 0f, 0f), TextureProjectionAxis.X);
        var onZ = Project(device, source, place, Facing(0f, 0f, 1f), TextureProjectionAxis.Z);

        // The instrument. The x plane reads (z, y) and the z plane reads (x, y), so a position whose
        // x and z differ makes these two different pictures — and if they were not, everything below
        // would pass for a kernel that never looked at the normal.
        Assert.NotEqual(
            TextureKernelHarness.At(onX, 0, 0, 0),
            TextureKernelHarness.At(onZ, 0, 0, 0)
        );

        var blended = Project(device, source, place, Facing(0f, 0f, 1f), TextureProjectionAxis.Triplanar);

        output.WriteLine(
            $"planar x {TextureKernelHarness.At(onX, 0, 0, 0)}, planar z {TextureKernelHarness.At(onZ, 0, 0, 0)}, "
            + $"triplanar facing z {TextureKernelHarness.At(blended, 0, 0, 0)}"
        );

        TextureKernelHarness.AssertSame(onZ, blended, 3, "a triplanar fill facing z against the z plane alone");
    }

    /// <summary>⚠ The weights are normalised, so a normal between two axes is their mean.</summary>
    /// <remarks>
    ///     <b>The failure this excludes is an exposure and not a projection.</b> Without the division
    ///     by the weights' own sum, a normal at 45° with a sharpness of one sums to 1.41 and the fill
    ///     comes out half as bright again as the picture it was made from — which reads as a material
    ///     that is too light, and is fixed by an artist with a levels curve rather than reported.
    /// </remarks>
    [Fact]
    public void The_weights_are_normalised_so_a_diagonal_normal_is_the_mean_of_two_planes() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var place = At(0.25f, 0.5f, 0.75f);
        var diagonal = 1f / MathF.Sqrt(2f);

        var onX = Project(device, source, place, Facing(1f, 0f, 0f), TextureProjectionAxis.X);
        var onZ = Project(device, source, place, Facing(0f, 0f, 1f), TextureProjectionAxis.Z);

        var between = Project(
            device,
            source,
            place,
            Facing(diagonal, 0f, diagonal),
            TextureProjectionAxis.Triplanar,
            sharpness: 1f
        );

        var discriminating = 0;

        for (var channel = 0; channel < 3; channel++) {
            var first = TextureKernelHarness.At(onX, 8, 8, channel);
            var second = TextureKernelHarness.At(onZ, 8, 8, channel);
            var actual = TextureKernelHarness.At(between, 8, 8, channel);
            var mean = (first + second) / 2;

            output.WriteLine($"channel {channel}: x {first}, z {second}, mean {mean}, blended {actual}");

            // ⚠ A channel where the two planes already agree says nothing about the blend, because
            // every weighting of two equal numbers is that number — and one of the three is always
            // like that here: both planes read the position's y as their v, and `Unique`'s green is
            // a function of the row alone. Counted rather than asserted per channel, with the count
            // checked below, because "skip the uninformative ones" and "there were some informative
            // ones" are two statements and only the pair of them is a test.
            if (first == second) {
                continue;
            }

            discriminating++;

            Assert.InRange(actual, mean - 3, mean + 3);
        }

        Assert.True(
            discriminating > 0,
            "the x and z planes agreed on every channel, so nothing here is a claim about the blend"
        );
    }

    /// <summary>A single-axis projection reads no normal at all.</summary>
    /// <remarks>
    ///     ⚠ <b>The property that makes <c>Planar</c> usable on a mesh with no <c>world</c> bake, and
    ///     the one a shared kernel could most easily lose.</b> Triplanar and planar are one kernel
    ///     here — the sampling, the wrap and the scale are the same arithmetic three times — so
    ///     "planar still blends a little" is exactly the kind of leak that arrangement invites, and
    ///     it would show up as a projection that changes when a mesh's normals do.
    /// </remarks>
    [Fact]
    public void A_planar_axis_reads_no_normal() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var place = At(0.25f, 0.5f, 0.75f);

        var facingY = Project(device, source, place, Facing(0f, 1f, 0f), TextureProjectionAxis.Y);
        var facingX = Project(device, source, place, Facing(1f, 0f, 0f), TextureProjectionAxis.Y);

        // The instrument: the same two normals do change a triplanar fill, so "identical" below is a
        // claim about the axis selector rather than about a kernel that ignores its third input.
        var blendedY = Project(device, source, place, Facing(0f, 1f, 0f), TextureProjectionAxis.Triplanar);
        var blendedX = Project(device, source, place, Facing(1f, 0f, 0f), TextureProjectionAxis.Triplanar);

        Assert.NotEqual(
            TextureKernelHarness.At(blendedY, 0, 0, 0),
            TextureKernelHarness.At(blendedX, 0, 0, 0)
        );

        TextureKernelHarness.AssertSame(facingY, facingX, 3, "a planar y fill under two different world normals");
    }

    /// <summary>⚠ The scale is a repeat count over a world range, so doubling it tiles the picture.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two properties in one picture: the wrap, and that the scale is not a length.</b> A
    ///         position sweeping from 0 to 1 across the image at a scale of two covers two turns, so
    ///         the texel half way along reads the same source coordinate as the one at the start.
    ///         A kernel that clamped instead of wrapping would smear the source's last column over
    ///         the whole second half, which is a picture with a plausible-looking gradient in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument is the same comparison at a scale of one.</b> There the two texels
    ///         are half a turn apart and must differ — without that, "these two texels agree" would
    ///         be satisfied by a kernel that ignored the position entirely.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Doubling_the_scale_repeats_the_picture_across_the_position_range() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var facing = Facing(0f, 0f, 1f);

        var once = Project(device, source, Sweeping(), facing, TextureProjectionAxis.Z);
        var twice = Project(device, source, Sweeping(), facing, TextureProjectionAxis.Z, scale: 2f);

        var discriminating = 0;

        for (var channel = 0; channel < 3; channel++) {
            var head = TextureKernelHarness.At(twice, 1, 8, channel);
            var wrapped = TextureKernelHarness.At(twice, 1 + (Side / 2), 8, channel);

            output.WriteLine(
                $"channel {channel}: at scale 2 texel 1 reads {head} and texel {1 + (Side / 2)} reads {wrapped}; "
                + $"at scale 1 they read {TextureKernelHarness.At(once, 1, 8, channel)} and "
                + $"{TextureKernelHarness.At(once, 1 + (Side / 2), 8, channel)}"
            );

            if (TextureKernelHarness.At(once, 1, 8, channel) == TextureKernelHarness.At(once, 1 + (Side / 2), 8, channel)) {
                // A channel that is constant along x proves nothing about a repeat along x.
                continue;
            }

            discriminating++;

            Assert.InRange(wrapped, head - 3, head + 3);
        }

        // ⚠ The instrument, and without it this test asserted nothing under exactly the defect its
        // own remark names. A kernel that ignored `position` produces a constant `once`, every
        // channel takes the `continue` above, and the loop finishes having made zero assertions —
        // green. The guard is right and it needed a count beside it.
        Assert.True(
            discriminating > 0,
            "no channel varied along x at scale 1, so nothing here compared a repeat — a kernel that "
            + "ignored the position entirely would reach this line."
        );
    }

    static Bitmap Project(
        VulkanDevice device,
        byte[] source,
        byte[] position,
        byte[] world,
        TextureProjectionAxis axis,
        float scale = 1f,
        float sharpness = 4f
    ) {
        var (first, firstStaging) = TextureKernelHarness.Upload(device, source, Side, Side);
        var (second, secondStaging) = TextureKernelHarness.Upload(device, position, Side, Side);
        var (third, thirdStaging) = TextureKernelHarness.Upload(device, world, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [TextureProjections.Triplanar(3, 0, 1, 2, scale, sharpness, axis)],
                Outputs = [3]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);

            using var bake = evaluator.Evaluate(
                plan,
                new Dictionary<int, TextureExternal> {
                    [0] = new(first, TextureKernelHarness.SourceUsage, new(Side, Side)),
                    [1] = new(second, TextureKernelHarness.SourceUsage, new(Side, Side)),
                    [2] = new(third, TextureKernelHarness.SourceUsage, new(Side, Side))
                }
            );

            return bake.Read(3);
        } finally {
            device.Destroy(firstStaging);
            device.Destroy(first);
            device.Destroy(secondStaging);
            device.Destroy(second);
            device.Destroy(thirdStaging);
            device.Destroy(third);
        }
    }
}
