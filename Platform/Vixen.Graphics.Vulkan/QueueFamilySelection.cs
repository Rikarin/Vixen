// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Silk.NET.Vulkan;

namespace Vixen.Graphics.Vulkan;

/// <summary>One queue family, reduced to what choosing between them needs.</summary>
/// <param name="Index">Its index, which is what <c>vkGetDeviceQueue</c> wants.</param>
/// <param name="Flags">What it can do.</param>
/// <param name="QueueCount">How many queues it has.</param>
/// <param name="CanPresent">Whether it can present to the surface we care about.</param>
readonly record struct QueueFamilyCandidate(uint Index, QueueFlags Flags, uint QueueCount, bool CanPresent);

/// <summary>Which family each kind of work goes to.</summary>
/// <param name="Graphics">Draws.</param>
/// <param name="Compute">Dispatches.</param>
/// <param name="Transfer">Copies.</param>
/// <param name="Present">Presents. Equal to <paramref name="Graphics" /> on almost all hardware.</param>
readonly record struct QueueFamilyPlan(uint Graphics, uint Compute, uint Transfer, uint Present) {
    /// <summary>Whether compute runs on a family of its own.</summary>
    public bool HasAsyncCompute => Compute != Graphics;

    /// <summary>Whether transfer runs on a family of its own.</summary>
    public bool HasAsyncTransfer => Transfer != Graphics;

    /// <summary>Whether presenting needs a queue the graphics work is not already on.</summary>
    /// <remarks>
    ///     Where this is true, a swapchain image has to be handed between families — either by making
    ///     it <c>Concurrent</c>, which costs bandwidth on tiled hardware, or by an explicit ownership
    ///     transfer. Worth knowing at device creation rather than discovering in the present path.
    /// </remarks>
    public bool NeedsSeparatePresent => Present != Graphics;

    /// <summary>Every distinct family, which is what <c>vkCreateDevice</c> takes.</summary>
    /// <remarks>
    ///     Distinct is the point: asking for the same family twice is a validation error, and on
    ///     hardware with one universal family all four of these are the same number.
    /// </remarks>
    public IEnumerable<uint> DistinctFamilies() {
        var seen = new HashSet<uint> { Graphics };
        yield return Graphics;

        foreach (var family in (uint[]) [Compute, Transfer, Present]) {
            if (seen.Add(family)) {
                yield return family;
            }
        }
    }
}

/// <summary>Choosing which queue family does what.</summary>
/// <remarks>
///     <para>
///         Pure policy over a list of plain records, for the same reason
///         <see cref="AdapterSelection" /> is: the interesting cases are hardware we do not have in
///         front of us. A discrete AMD card exposes a universal family, a compute-only family and a
///         transfer-only DMA family; Apple silicon through MoltenVK exposes exactly one family that
///         does everything; lavapipe exposes one; and a handful of drivers put present on a family
///         that cannot draw. All four shapes are tested here, on a machine with no Vulkan.
///     </para>
///     <para>
///         The preference throughout is <em>dedicated over capable</em>. A family that advertises
///         compute and not graphics is a hardware queue that runs alongside the graphics one; the
///         universal family also advertises compute, but scheduling compute there just interleaves it
///         with the draws it was supposed to overlap.
///     </para>
/// </remarks>
static class QueueFamilySelection {
    /// <summary>Assigns each kind of work to a family.</summary>
    /// <param name="families">Everything the physical device reported, in index order.</param>
    /// <param name="presentRequired">Whether a present queue is needed — false for offscreen work.</param>
    /// <param name="plan">Who does what.</param>
    /// <param name="reason">Why no plan was possible, when none was.</param>
    public static bool TryPlan(
        ReadOnlySpan<QueueFamilyCandidate> families,
        bool presentRequired,
        out QueueFamilyPlan plan,
        [NotNullWhen(false)] out string? reason
    ) {
        plan = default;

        // Graphics, preferring one that can also present so that the swapchain never needs an
        // ownership transfer. Falling back to any graphics family is correct and slower, not wrong.
        var graphics = FirstIndex(families, f => Has(f, QueueFlags.GraphicsBit) && f.CanPresent);
        graphics ??= FirstIndex(families, f => Has(f, QueueFlags.GraphicsBit));

        if (graphics is null) {
            reason = families.IsEmpty
                ? "The device reported no queue families at all."
                : $"None of the device's {families.Length} queue families can draw.";

            return false;
        }

        var present = graphics;

        if (presentRequired && !families[(int)graphics.Value].CanPresent) {
            present = FirstIndex(families, f => f.CanPresent);

            if (present is null) {
                reason = "No queue family on this device can present to the surface.";
                return false;
            }
        }

        // Dedicated compute: advertises compute, does not advertise graphics.
        var compute = FirstIndex(families, f => Has(f, QueueFlags.ComputeBit) && !Has(f, QueueFlags.GraphicsBit))
            ?? graphics;

        // Dedicated transfer: the DMA engine. Vulkan lets a driver leave TransferBit off a family
        // that advertises graphics or compute, because both imply it — so a family advertising
        // transfer and nothing else is exactly the copy engine, and asking for TransferBit alone
        // would miss drivers that set it everywhere.
        var transfer = FirstIndex(
            families,
            f => Has(f, QueueFlags.TransferBit)
                && !Has(f, QueueFlags.GraphicsBit)
                && !Has(f, QueueFlags.ComputeBit)
        ) ?? graphics;

        plan = new(graphics.Value, compute.Value, transfer.Value, present.Value);
        reason = null;
        return true;
    }

    static bool Has(in QueueFamilyCandidate family, QueueFlags flag) =>
        family.QueueCount > 0 && (family.Flags & flag) != 0;

    static uint? FirstIndex(
        ReadOnlySpan<QueueFamilyCandidate> families,
        Func<QueueFamilyCandidate, bool> predicate
    ) {
        for (var index = 0; index < families.Length; index++) {
            if (families[index].QueueCount > 0 && predicate(families[index])) {
                return families[index].Index;
            }
        }

        return null;
    }
}
