// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>What appearance the operating system is set to.</summary>
/// <remarks>
///     <para>
///         The platform half of <c>@media (prefers-color-scheme: …)</c>. It is deliberately three
///         values rather than two: <see cref="Unknown" /> is the honest answer on a platform that
///         has no such setting, on one whose setting could not be read, and on a headless run — and
///         it is a different answer from <see cref="Light" />, which is what a system actually set
///         to light says.
///     </para>
///     <para>
///         ⚠ <b>Collapsing <see cref="Unknown" /> into <see cref="Light" /> is the mistake this
///         enum exists to prevent.</b> A stylesheet asks two questions —
///         <c>(prefers-color-scheme: dark)</c> and <c>(prefers-color-scheme: light)</c> — and CSS
///         says both are false when there is no preference. A platform that reported light where it
///         meant "I do not know" would make the second one true, which is a stylesheet taking a
///         branch nobody asked for rather than falling through to its own default.
///     </para>
/// </remarks>
public enum SystemColorScheme : byte {
    /// <summary>The platform has no appearance setting, or it could not be read.</summary>
    Unknown = 0,

    /// <summary>The system is set to a light appearance.</summary>
    Light = 1,

    /// <summary>The system is set to a dark appearance.</summary>
    Dark = 2
}
