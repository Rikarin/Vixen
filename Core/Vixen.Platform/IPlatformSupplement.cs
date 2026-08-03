// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>The four services a per-OS assembly can do better than a portable one, in one value.</summary>
/// <remarks>
///     One record rather than four properties on <see cref="IPlatformSupplement" /> because the
///     supplement replaces some of them and keeps the rest, and passing the whole set in and out is
///     what lets it say so in one expression — <c>baseline with { Power = new MacOSPowerInfo(…) }</c>
///     — instead of four nullable factory methods whose <see langword="null" /> results the caller
///     has to recombine.
/// </remarks>
/// <param name="Clipboard">The clipboard.</param>
/// <param name="Dialogs">The pickers and message boxes.</param>
/// <param name="Power">Battery, thermal and power-mode state.</param>
/// <param name="Processors">Processor counts, classes and affinity.</param>
/// <param name="Capabilities">What the platform can do with these services in place.</param>
public readonly record struct PlatformServices(
    IClipboard Clipboard,
    INativeDialogs Dialogs,
    IPowerInfo Power,
    IProcessorTopology Processors,
    PlatformCapabilities Capabilities
);

/// <summary>What one operating system knows that a portable platform implementation does not.</summary>
/// <remarks>
///     <para>
///         <c>Vixen.Platform.Desktop</c> covers Windows, Linux and macOS through SDL, and SDL
///         genuinely covers most of it. What it cannot cover is the part where the three operating
///         systems have nothing in common: a file picker, an image on the clipboard, a thread pinned
///         to a core, how hot the machine is. Those live in <c>Vixen.Platform.Windows</c>,
///         <c>.Linux</c> and <c>.MacOS</c>, and this is how they get in — the portable
///         implementation builds the services it can, hands them over, and uses what comes back.
///     </para>
///     <para>
///         <b>Augmenting, not replacing.</b> A supplement is expected to keep most of what it is
///         given: macOS can answer <see cref="IPowerInfo.Thermal" /> and has no better answer than
///         SDL's for <see cref="IPowerInfo.BatteryLevel" />, so the sensible macOS implementation
///         wraps the baseline rather than reimplementing it. That is also why
///         <see cref="Augment" /> returns the capabilities: a supplement that supplies pickers has
///         earned <see cref="PlatformCapabilities.NativeDialogs" /> for the platform that hosts it,
///         and nothing else is in a position to decide that.
///     </para>
///     <para>
///         <b>Threading.</b> Constructed and disposed on the thread that owns the platform. The
///         services it returns follow whatever their own contracts say, which for the clipboard and
///         the dialogs is that same thread.
///     </para>
/// </remarks>
public interface IPlatformSupplement : IDisposable {
    /// <summary>A name for logs and crash reports: <c>"Windows"</c>, <c>"macOS"</c>.</summary>
    string Name { get; }

    /// <summary>Replaces what this operating system can do better, and keeps the rest.</summary>
    /// <param name="baseline">What the portable implementation built.</param>
    /// <returns>The services to use, and the capabilities they add up to.</returns>
    PlatformServices Augment(in PlatformServices baseline);
}
