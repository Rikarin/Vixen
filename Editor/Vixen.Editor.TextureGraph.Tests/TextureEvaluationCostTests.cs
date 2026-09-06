// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48's forty-node-graph criterion — "a forty-node graph at 2048² is <i>recorded</i>, and what
///     is gated is the work rather than the clock" — as the properties that make the milliseconds what
///     they are, rather than as the milliseconds.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the number is not asserted anywhere, and this file is.</b>
///         <c>TexturePlanDeviceTests.A_forty_op_evaluation_is_measured_at_one_two_and_four_K</c>
///         prints 13 / 27 / 54 ms on the machine this was written on — nine times under the 250 ms
///         the criterion used to name, before the document amended that clause away for this very
///         reason — and asserts only a two-minute hang check. That is deliberate and it is
///         this repository's own rule: a wall-clock budget calibrated on an idle machine is its
///         largest flake source, and the established replacements are a deterministic counter first,
///         a differential measured on the same machine at the same moment second, and an absurd
///         ceiling last. So the measurement stays a measurement, attributed to an adapter, and the
///         <em>gate</em> is here: the deterministic counters that decide whether a bake costs one
///         round trip or forty, and two textures or forty.
///     </para>
///     <para>
///         ⚠ <b>Each of these has a failure that is invisible in a picture and enormous in a
///         clock.</b> A schedule that gave every op its own texture draws exactly the same image and
///         asks the allocator for 2.6 GB at 4K — which does not fail, it swaps, or it is refused on
///         somebody else's machine. An evaluator that ended the frame per op draws exactly the same
///         image and pays a full device drain forty times. A variant cache keyed by anything finer
///         than (kernel, output format) draws exactly the same image and runs the Raven front end
///         forty times. None of the three is a wrong picture, so no golden in this assembly can see
///         any of them, and until this file existed nothing else did either.
///     </para>
///     <para>
///         <b>On the Null device, for the frame and dispatch counts.</b> What is being counted is
///         RHI calls, which that device records exactly and a real one does not expose at all;
///         nothing here reads a texel, which is the rule this suite keeps for a device that would
///         happily prove a black image equals a black image.
///     </para>
/// </remarks>
public class TextureEvaluationCostTests {
    /// <summary>The side the counted plans are built at. Small: nothing here is a measurement.</summary>
    const int Side = 16;

    /// <summary>The op count doc 48 § M0 names, and what the criterion is about.</summary>
    const int Forty = 40;

    /// <summary>An identity levels curve, whose parameters are exactly the kernel's members.</summary>
    static TextureOp Levels(int target, int source) =>
        new() {
            Kernel = "Levels",
            Output = target,
            Inputs = [source],
            Parameters = [
                new("inputBlack", 0f),
                new("inputWhite", 1f),
                new("gamma", 1f),
                new("outputBlack", 0f),
                new("outputWhite", 1f),
                new("dither", 0f)
            ]
        };

    /// <summary>A chain of <paramref name="ops" /> levels curves, each reading the one before it.</summary>
    static TexturePlan Chain(int ops, int side = Side) {
        var images = ImmutableArray.CreateBuilder<TextureImage>();
        var chain = ImmutableArray.CreateBuilder<TextureOp>();

        images.Add(new(TextureFormat.Rgba8, External: true));

        for (var index = 0; index < ops; index++) {
            images.Add(new(TextureFormat.Rgba8));
            chain.Add(Levels(index + 1, index));
        }

        return new() {
            BaseWidth = side,
            BaseHeight = side,
            Images = images.ToImmutable(),
            Ops = chain.ToImmutable(),
            Outputs = [ops]
        };
    }

    static TextureHandle Source(NullDevice device, int side = Side) =>
        device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                side,
                side,
                TextureUsage.Sampled | TextureUsage.CopyDestination | TextureUsage.CopySource,
                Name: "cost test source"
            )
        );

    /// <summary>
    ///     ⚠ A chain of any length is two textures, and its footprint does not grow with it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half of criterion 1 that is arithmetic rather than a clock.</b> Forty ops at
    ///         4K in forty <c>rgba8</c> textures is 2.6 GB; in two it is 128 MB, which is what the
    ///         measurement above prints. The picture is identical either way, so this is the only
    ///         thing that can say which one happened.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Derived across a family rather than asserted at forty</b>, because "forty ops
    ///         allocate two textures" is also true of a schedule that allocates <c>min(n, 2)</c> by
    ///         accident and of one that allocates two <em>hundred</em> at two hundred ops. What is
    ///         asserted is that the number and the byte count are the <em>same</em> at 2, 3, 40 and
    ///         200 — a claim about the shape of the function, which no single length can make.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_chain_costs_two_pooled_textures_however_long_it_is() {
        var lengths = (int[])[2, 3, Forty, 200];
        var schedules = lengths.Select(length => TexturePoolSchedule.For(Chain(length))).ToArray();

        // The instrument first: a one-op plan pools one image, so the two that a longer chain needs
        // are a number this family actually moves to. Without this the equalities below would also
        // hold of a schedule that returned a constant.
        Assert.Equal(1, TexturePoolSchedule.For(Chain(1)).Allocations);

        foreach (var (length, schedule) in lengths.Zip(schedules)) {
            Assert.Equal(2, schedule.Allocations);
            Assert.Equal(schedules[0].Bytes, schedule.Bytes);

            // And every op in the chain got a slot: a schedule that quietly stopped assigning past
            // some length would satisfy both equalities above by doing nothing.
            for (var image = 1; image <= length; image++) {
                Assert.InRange(TexturePoolSchedule.For(Chain(length)).SlotOf[image], 0, 1);
            }
        }
    }

    /// <summary>
    ///     ⚠ A bake of forty dispatches costs one frame and forty dispatches, not forty of each.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the round-trip count, and it is what a millisecond budget is really
    ///         about.</b> <c>TexturePlanEvaluator.Run</c> opens one frame, records every dispatch
    ///         into one command list, submits once and drains once — so a forty-op bake pays one
    ///         device drain. An evaluator that ended the frame per op would draw the same picture
    ///         and pay forty, which at 4K is the difference between the 54 ms the measurement prints
    ///         and something no criterion could survive.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>FrameCount</c> is the counter because it is the one every backend
    ///         guarantees</b> — it "changes exactly once per frame", whichever of
    ///         <c>BeginFrame</c>/<c>EndFrame</c> a backend chooses to move it on — so what is read
    ///         here is a difference across one call and never an ordinal.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the counter is shown to move</b>, in the companion below. "One frame" said
    ///         of a device that never counts frames is the same green as "one frame" said of one
    ///         that does, and this suite's own rule is to ask what an assertion prints on the day it
    ///         is not measuring anything.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_forty_dispatch_bake_is_one_frame_and_one_dispatch_per_op() {
        using var device = new NullDevice(new() { Record = true });

        var source = Source(device);
        var plan = Chain(Forty);

        Assert.Empty(plan.Check());

        using var evaluator = new TexturePlanEvaluator(device);

        device.Recorder!.Clear();

        var before = device.FrameCount;

        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        Assert.Equal(before + 1, device.FrameCount);
        Assert.Equal(Forty, bake.Dispatches);
        Assert.Equal(Forty, device.Recorder.CountOf(RecordedCommandKind.Dispatch));

        // One variant, forty ops. A cache keyed on the op rather than on (kernel, format) runs the
        // Raven front end forty times for one picture — which is invisible in the output and, at the
        // 4K end of the measurement, is most of the wall clock.
        Assert.Equal(1, evaluator.Compilations);

        device.Destroy(source);
    }

    /// <summary>
    ///     ⚠ The frame counter this file rests on does move, and a CPU op is what moves it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instrument for the test above, and a second claim in its own right.</b> Doc 48
    ///         § 4.6's exception to § D3 is written down in <c>TexturePlanEvaluator.OnCpu</c>'s own
    ///         remarks — an op that is not a dispatch "closes that list, ends the frame, waits for
    ///         the device", and that is why <c>ITextureCpuOperation</c> is not the easy way to add a
    ///         node. Nothing measured it. Here the cost is exactly one extra frame per CPU op, which
    ///         makes the "one frame" above a measurement rather than a property of a device that
    ///         counts nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is stated as a bound rather than as a promise of cheapness.</b> A slice that
    ///         batched two adjacent CPU ops into one drain would make this fail while making the
    ///         engine better; the failure message says so, and doc 48 § 4.6 is where the argument
    ///         would be had.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_cpu_op_costs_one_further_frame_and_a_dispatch_costs_none() {
        using var device = new NullDevice(new() { Record = true });

        var source = Source(device);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                Levels(1, 0),
                new() { Kernel = "Transpose", Output = 2, Inputs = [1], Cpu = new TransposeRgba8() },
                Levels(3, 2)
            ],
            Outputs = [3]
        };

        Assert.Empty(plan.Check());

        using var evaluator = new TexturePlanEvaluator(device);

        var before = device.FrameCount;

        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

        Assert.Equal(2, bake.Dispatches);

        Assert.True(
            device.FrameCount == before + 2,
            $"A plan of two dispatches and one CPU op cost {device.FrameCount - before} frames rather than 2. "
            + "One frame is the whole GPU chain and each CPU op adds one drain — see TexturePlanEvaluator.OnCpu. "
            + "More than this is a per-op drain; fewer means CPU ops were batched, which would be an "
            + "improvement doc 48 § 4.6 should be amended for rather than a silent one."
        );

        device.Destroy(source);
    }
}
