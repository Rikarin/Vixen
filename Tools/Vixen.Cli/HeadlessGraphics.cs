// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Vulkan;

namespace Vixen.Cli;

/// <summary>A GPU for a verb that has to run compute, or a sentence saying there is not one.</summary>
/// <remarks>
///     <para>
///         <b>The decision in <a href="https://github.com/Rikarin/Vixen/issues/1020">#1020</a> is
///         this file and not the bake it serves.</b> Every other verb in this CLI is CPU work —
///         <c>MeshMapRunner</c>'s raster, <c>ShaderBuildRunner</c>'s compile — and evaluating a
///         texture graph is the first that dispatches. A command-line tool asked for a picture on a
///         machine with no adapter has exactly two honest answers, and only one of them is this one.
///     </para>
///     <para>
///         ⚠ <b>There is no fallback, and the absence is the whole point.</b> A headless run that
///         falls through to <c>Vixen.Graphics.Null</c> writes a black PNG, prints a table of healthy
///         counters and exits 0 — the failure this repository's own working notes name as the one
///         that keeps being shipped, and the one <c>GraphicsHost</c> refuses for
///         <c>--vixen-offscreen</c> and <c>--vixen-capture</c> for the same reason. A bake that wrote
///         black maps and a valid <c>.vxmat</c> over an artist's material would be worse than either:
///         a build script cannot tell it from a good run, and the material it replaced is gone.
///     </para>
///     <para>
///         ⚠ <b>So this asks Vulkan and nothing else, rather than walking a preference list.</b>
///         <c>GraphicsHost</c> is the list, and it lives in <c>Tools/Vixen.App</c> with a window
///         system, three more backends and a platform layer behind it — none of which a bake needs,
///         and one of which is the device that draws nothing. Naming one backend is what makes "no
///         fallback" a fact about this assembly's references rather than a flag somebody can pass.
///         <c>CliGraphicsBackendTests</c> is what holds that: it reads this assembly's own
///         this one and refuses <c>Vixen.Graphics.Null</c> among them.
///     </para>
///     <para>
///         <b>Opened per verb and disposed with it.</b> A CLI process runs one command, and a device
///         held past the bake is a device held while the tool writes files.
///     </para>
/// </remarks>
static class HeadlessGraphics {
    /// <summary>Opens a device with no surface, or says why there is none.</summary>
    /// <param name="device">The device, when one opened. The caller owns it.</param>
    /// <param name="reason">What to tell somebody who has no adapter, when none did.</param>
    /// <returns><see langword="true" /> if a device opened.</returns>
    /// <remarks>
    ///     ⚠ <b>The reason is the driver's own and a sentence about what to do next</b>, because the
    ///     person reading it is running a build script on a container image somebody else wrote —
    ///     "no Vulkan device" alone sends them to look at their graph.
    /// </remarks>
    public static bool TryOpen(out IGraphicsDevice? device, out string? reason) {
        // ⚠ `new()` and not a surface: a bake presents to nothing, and asking for surface extensions
        // is what makes a headless container fail for a reason that has nothing to do with compute.
        if (VulkanDevice.TryCreate(new(), out var opened, out var refusal)) {
            device = opened;
            reason = null;

            return true;
        }

        device = null;

        reason = $"there is no GPU here: {refusal} Evaluating a texture graph is compute on a device, "
            + "and this verb refuses to run it on the one that draws nothing — a bake that fell back "
            + "would write black maps and a material that looks valid. A CI image needs a Vulkan "
            + "driver (lavapipe is enough); bake the maps elsewhere and pass --from to write the "
            + "material from them.";

        return false;
    }

    /// <summary>What to record as the adapter, which is the device's own name and never a claim.</summary>
    /// <param name="device">The device that ran the bake.</param>
    /// <returns>The adapter name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Why <c>--adapter</c> is refused beside <c>--graph</c>.</b> That option exists for the
    ///     folder bake, where nothing here ran the maps and the name is somebody's note about a
    ///     machine that did. A graph bake ran them, on this device, so a typed name would be a
    ///     provenance block that disagrees with the run that produced it — and § D4 does not compare
    ///     the field, so nothing would ever catch it.
    /// </remarks>
    public static string Adapter(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        return device.Adapter.Name;
    }
}
