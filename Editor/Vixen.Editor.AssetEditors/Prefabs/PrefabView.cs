// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.AssetEditors.Prefabs;

/// <summary>A prefab, open in isolation: a banner saying so, and the subtree it holds.</summary>
/// <remarks>
///     <para>
///         The panel is <c>PrefabView.vxml</c>; this file is the accessibility modifier and the record
///         its banner is made of. The emitter's partial carries no modifier, and the type is public.
///     </para>
///     <para>
///         <b>The banner is the feature, not decoration.</b> The failure an isolated prefab mode
///         exists to prevent is somebody editing a prefab while believing they are editing a level —
///         and every editor that has this mode marks it, because the two look identical otherwise.
///         The banner also carries the one thing that is true here and nowhere else: this document
///         must have exactly one root, and it says so the moment it does not.
///     </para>
///     <para>
///         ⚠ <b>The root count is checked as the structure changes and refused at the save.</b>
///         Refusing the edit that made a second root would mean an author cannot create an entity
///         before parenting it. So the banner turns into a complaint, and
///         <see cref="PrefabFileWriter" /> is what actually refuses — which is the moment work would
///         otherwise be lost.
///     </para>
/// </remarks>
public sealed partial class PrefabView;

/// <summary>What the prefab banner is saying, and whether it is a complaint.</summary>
/// <param name="Message">The sentence under the title.</param>
/// <param name="IsValid">Whether the prefab has the one root it needs.</param>
/// <remarks>
///     ⚠ <b>A snapshot rather than two signals or a counter.</b> Both fields come from one reading of
///     the root count, and the value is what changes — so assigning it is the notification, and a
///     <c>Signal&lt;int&gt;</c> bumped to force a re-read would be standing in for a value change that
///     is perfectly expressible.
/// </remarks>
internal readonly record struct PrefabBanner(string Message, bool IsValid) {
    /// <summary>Before a prefab has been shown.</summary>
    public static PrefabBanner Empty { get; } = new(string.Empty, true);
}
