// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>The accessibility settings an operating system will tell an application about.</summary>
/// <param name="ReduceMotion">
///     Whether the user has asked for less animation, or <c>null</c> where the platform cannot say.
/// </param>
/// <param name="HighContrast">
///     Whether the user has asked for a forced high-contrast palette, or <c>null</c> where the
///     platform cannot say.
/// </param>
/// <param name="TextScale">
///     How much larger than normal the user wants text, as a multiplier — <c>1.5</c> for fifty per
///     cent larger — or <c>null</c> where the platform cannot say.
/// </param>
/// <remarks>
///     <para>
///         <b>The platform half of <c>prefers-reduced-motion</c> and <c>forced-colors</c>, and the
///         reason it exists is that both queries already worked and neither had a source.</b>
///         <c>MediaPreferences</c> has carried these axes since the media features landed, and every
///         writer of it in the tree was a test — so an application shipped its animations to a user
///         who had switched them off and nothing anywhere reported it. That is the same shape of
///         hole <c>PlatformInput.ApplyColorScheme</c> was written to close one axis over.
///     </para>
///     <para>
///         ⚠ <b><c>null</c> is a real answer and is not <c>false</c>.</b> A headless run has no user
///         to have a preference, and a Linux session with no settings daemon has nowhere to keep
///         one. Flattening either to "no, they did not ask for reduced motion" is how an application
///         ends up animating at a user who has already said not to — the honest translation of
///         "unknown" is CSS's <c>no-preference</c>, and the difference is that a host can tell the
///         two apart and log the first.
///     </para>
///     <para>
///         ⚠ <b>Two nullable flags rather than two tri-state enums</b>, unlike
///         <see cref="SystemColorScheme" /> one axis over. A colour scheme has three genuine values
///         and its unknown is a fourth; these are yes-or-no settings whose third state is only ever
///         "nobody could be asked", which is exactly what a nullable means and is one type rather
///         than three.
///     </para>
///     <para>
///         ⚠ <b><see cref="TextScale" /> is a multiplier and not a font size, which is what lets one
///         number mean the same thing on three platforms.</b> Windows keeps a percentage from 100 to
///         225, GNOME a factor around one; both are a scale of whatever the application's own base
///         is, and neither is a point size. A host multiplies <c>UiDocument.RootFontSize</c> by it,
///         so an application that chose a fourteen-pixel root keeps its own proportions.
///     </para>
///     <para>
///         ⚠ <b>macOS has no source for it and that is the honest answer rather than a gap.</b>
///         Dynamic Type is an iOS API; the Mac has no system-wide text scale to read, so
///         <c>MacOSAccessibility</c> leaves this <c>null</c> — the same <c>null</c>-is-not-<c>no</c>
///         reading the two flags above already carry.
///     </para>
///     <para>
///         What is deliberately <i>not</i> here: the system accent colour and the platform's semantic
///         palette (AppKit's <c>labelColor</c> and friends). Both need a platform read this type
///         cannot make on its own terms — see <c>SystemPalette</c>, which is where they would land.
///     </para>
/// </remarks>
public readonly record struct SystemAccessibility(
    bool? ReduceMotion = null,
    bool? HighContrast = null,
    float? TextScale = null
) {
    /// <summary>What a platform that cannot read any of this reports.</summary>
    /// <remarks>
    ///     Named rather than left as <c>default</c> so that a platform saying "I have no source for
    ///     these" reads as a decision at its call site instead of as a field nobody filled in.
    /// </remarks>
    public static SystemAccessibility Unknown => default;
}
