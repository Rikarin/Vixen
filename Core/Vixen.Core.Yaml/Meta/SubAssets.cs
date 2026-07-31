// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Hashing;
using System.Text;

namespace Vixen.Core.Yaml.Meta;

/// <summary>Two sub-assets of one asset that derived the same id.</summary>
public sealed class SubAssetCollisionException(SubAssetId id, string first, string second)
    : Exception(
        $"'{first}' and '{second}' both derive sub-asset id {id}. Rename one of them: the id comes from the "
        + "name, so two things with names that hash alike would be indistinguishable to every reference in the "
        + "project."
    ) {
    /// <summary>The id they both wanted.</summary>
    public SubAssetId Id { get; } = id;

    /// <summary>One of them.</summary>
    public string First { get; } = first;

    /// <summary>The other.</summary>
    public string Second { get; } = second;
}

/// <summary>Where a sub-asset's id comes from.</summary>
/// <remarks>
///     <para>
///         From what the sub-asset <i>is</i> — the importer, the kind, the name — and never from
///         where it landed in the source file. That is the one piece of Unity's behaviour worth
///         keeping while discarding its representation. Unity's <c>fileID</c> values are internal
///         magic numbers, so re-exporting an FBX whose mesh order changed renumbers everything and
///         silently breaks every reference; "my prefab lost its mesh after an artist re-exported" is
///         one of the most common failures in real projects, and it is avoidable by construction.
///     </para>
///     <para>
///         XxHash32, because the id has to be identical on every machine and every run. A
///         <c>string.GetHashCode()</c> is randomised per process and would give each developer a
///         different project.
///     </para>
/// </remarks>
public static class SubAssets {
    /// <summary>Derives a sub-asset's id.</summary>
    /// <param name="importerType">The importer's contract name — <c>ModelImporter</c>.</param>
    /// <param name="kind">What kind of thing it is — <c>Mesh</c>.</param>
    /// <param name="name">What it is called — <c>Hero_Mesh</c>.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     The three parts are separated by a byte that cannot occur in any of them, so that
    ///     <c>("Model", "Mesh", "Hero")</c> and <c>("Model", "MeshHero", "")</c> are different
    ///     inputs rather than the same one.
    /// </remarks>
    public static SubAssetId Derive(string importerType, string kind, string name) {
        ArgumentNullException.ThrowIfNull(importerType);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);

        var hash = new XxHash32();
        Feed(hash, importerType);
        Feed(hash, kind);
        Feed(hash, name);

        var value = BitConverter.ToUInt32(hash.GetCurrentHash());

        // Zero is the main object, so a sub-asset that hashes to it takes the next value along
        // rather than becoming indistinguishable from the asset itself.
        return new(value == 0 ? 1u : value);
    }

    /// <summary>Checks that no two entries claim one id.</summary>
    /// <param name="entries">The entries.</param>
    /// <exception cref="SubAssetCollisionException">Two of them do.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A backstop rather than the thing that catches the ordinary case.</b> Two meshes
    ///         with one name is what a <c>.glb</c> from anywhere but your own DCC tool looks like, and
    ///         this used to fail the whole asset with "rename one of them" — which nothing in the
    ///         editor could do. <c>ImportContext.DeclareSubAsset</c> now suffixes the second one and
    ///         warns, so an importer going through it cannot reach here.
    ///     </para>
    ///     <para>
    ///         What is left for this to catch is two <i>different</i> names that hash alike, which is
    ///         a genuine one-in-four-billion and has no answer but renaming, and an importer that
    ///         built its entries by hand instead of declaring them.
    ///     </para>
    /// </remarks>
    public static void EnsureDistinct(IEnumerable<SubAssetEntry> entries) {
        ArgumentNullException.ThrowIfNull(entries);
        var seen = new Dictionary<SubAssetId, string>();

        foreach (var entry in entries) {
            if (!seen.TryAdd(entry.Id, entry.Name)) {
                throw new SubAssetCollisionException(entry.Id, seen[entry.Id], entry.Name);
            }
        }
    }

    static void Feed(XxHash32 hash, string part) {
        hash.Append(Encoding.UTF8.GetBytes(part));
        hash.Append([0]);
    }
}
