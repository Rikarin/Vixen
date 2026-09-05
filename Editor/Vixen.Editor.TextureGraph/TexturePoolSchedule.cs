// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>One texture the evaluator actually allocates, which one or more plan images share.</summary>
/// <param name="Format">What it stores.</param>
/// <param name="Width">Its width in texels.</param>
/// <param name="Height">Its height in texels.</param>
public readonly record struct TexturePoolSlot(TextureFormat Format, int Width, int Height);

/// <summary>
///     Which physical texture each of a plan's images lives in, worked out from the op order alone.
/// </summary>
/// <remarks>
///     <para>
///         <b>Liveness is the op order, and the plan already fixes it.</b> An image is written by
///         exactly one op — <see cref="TexturePlan.Validate" /> refuses a plan where that is not
///         true — so it is live from that op until the last op that reads it, and dead afterwards
///         unless the plan names it an output. Nothing here analyses a graph, because by the time a
///         plan exists there is no graph left to analyse.
///     </para>
///     <para>
///         ⚠ <b>The number this exists to bound is <see cref="Allocations" />, and a chain that
///         allocates one texture per op is the failure it is looking for.</b> Forty ops threaded
///         through two live images must allocate two textures and not forty; at 2K that is the
///         difference between 32 MB and 640 MB, and the version that allocates forty works perfectly
///         on a small plan and takes the machine down on a real one.
///     </para>
///     <para>
///         <b>A slot is reused only by an image of the same format and the same size.</b> Aliasing
///         across shapes is what a transient resource allocator does with a memory heap; this is a
///         list of textures, and a texture is not reinterpretable. So <see cref="Allocations" /> is
///         at least the peak number of live images and may be more when a plan works at several
///         resolutions — which is the honest number, since those are textures that exist.
///     </para>
///     <para>
///         <b>Computed with no device</b>, which is what lets the bound be asserted on any machine.
///         The assertion this whole type exists for is in a test that opens nothing.
///     </para>
/// </remarks>
public sealed class TexturePoolSchedule {
    TexturePoolSchedule() { }

    /// <summary>The textures to create, in the order they are first needed.</summary>
    public ImmutableArray<TexturePoolSlot> Slots { get; private init; }

    /// <summary>
    ///     Which slot each image lives in, by image index; <c>-1</c> for an image the caller supplies.
    /// </summary>
    public ImmutableArray<int> SlotOf { get; private init; }

    /// <summary>How many textures the evaluation creates.</summary>
    public int Allocations => Slots.Length;

    /// <summary>The most images that are live at any one moment.</summary>
    /// <remarks>
    ///     Reported beside <see cref="Allocations" /> because the two answer different questions. This
    ///     one is a property of the plan and is what a scheduler could ever hope to reach; the other
    ///     is what was actually created, and is larger exactly when a plan mixes resolutions.
    /// </remarks>
    public int Peak { get; private init; }

    /// <summary>How many bytes the created textures take, at one mip and one layer each.</summary>
    public long Bytes {
        get {
            long total = 0;

            foreach (var slot in Slots) {
                total += (long)slot.Width * slot.Height * TextureFormats.BytesPerTexel(slot.Format);
            }

            return total;
        }
    }

    /// <summary>Works out the pooling for a plan.</summary>
    /// <param name="plan">The plan. Must be sound — see <see cref="TexturePlan.Validate" />.</param>
    /// <returns>The schedule.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> is null.</exception>
    public static TexturePoolSchedule For(TexturePlan plan) {
        ArgumentNullException.ThrowIfNull(plan);

        var lastRead = new int[plan.Images.Length];

        Array.Fill(lastRead, -1);

        for (var index = 0; index < plan.Ops.Length; index++) {
            foreach (var input in plan.Ops[index].Inputs) {
                if (input >= 0 && input < lastRead.Length) {
                    lastRead[input] = index;
                }
            }
        }

        var kept = new bool[plan.Images.Length];

        foreach (var output in plan.Outputs) {
            if (output >= 0 && output < kept.Length) {
                kept[output] = true;
            }
        }

        var slotOf = new int[plan.Images.Length];

        Array.Fill(slotOf, -1);

        var slots = ImmutableArray.CreateBuilder<TexturePoolSlot>();
        var alive = new bool[plan.Images.Length];
        List<int> free = [];
        var live = 0;
        var peak = 0;

        for (var index = 0; index < plan.Ops.Length; index++) {
            var op = plan.Ops[index];

            // ⚠ The output is taken before the inputs are given back, and swapping the two is the
            // classic form of this bug: an op whose input dies on the same dispatch would otherwise
            // hand its texture straight to the op's own output, and the kernel would read the image
            // it is writing.
            var image = op.Output;
            var size = plan.SizeOf(image);
            var wanted = new TexturePoolSlot(plan.Images[image].Format, size.X, size.Y);
            var reused = -1;

            for (var candidate = 0; candidate < free.Count; candidate++) {
                if (slots[free[candidate]] == wanted) {
                    reused = free[candidate];
                    free.RemoveAt(candidate);

                    break;
                }
            }

            if (reused < 0) {
                reused = slots.Count;
                slots.Add(wanted);
            }

            slotOf[image] = reused;
            alive[image] = true;
            live++;
            peak = Math.Max(peak, live);

            // Everything whose last reader was this op, and the op's own output when nothing ever
            // reads it and the plan does not keep it — a dead write is still a dispatch, and its
            // texture is free the moment it has run.
            for (var candidate = 0; candidate < plan.Images.Length; candidate++) {
                if (!alive[candidate] || kept[candidate] || plan.Images[candidate].External) {
                    continue;
                }

                if (lastRead[candidate] > index) {
                    continue;
                }

                alive[candidate] = false;
                free.Add(slotOf[candidate]);
                live--;
            }
        }

        return new() { Slots = slots.ToImmutable(), SlotOf = [.. slotOf], Peak = peak };
    }
}
