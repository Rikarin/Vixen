// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>Identity of one entity inside a scene file, independent of any world's handles.</summary>
/// <remarks>
///     <para>
///         <b>An <c>Entity</c> is a slot and a version in one world, so it cannot be what a file
///         says.</b> Loading the same scene twice, or entering and leaving play mode, reissues every
///         handle — a file that stored them would name whatever landed in those slots. This is the
///         identity that survives, and it is what a reference from one entity to another, a prefab
///         override and a multi-user session all have to be expressed in.
///     </para>
///     <para>
///         ⚠ <b>A GUID rather than a number counted up from one, and the reason is git.</b> A local
///         counter gives a readable diff and one unreadable failure: two branches each add an entity,
///         each picks the next id, and the merge takes both hunks cleanly — leaving a file with two
///         entities claiming one id, which no tool anywhere reports. A GUID cannot collide, and the
///         noisier diff is the same trade
///         <see href="../../docs/plan/08-asset-pipeline-and-addressables.md">doc 08</see> already made
///         for assets.
///     </para>
///     <para>
///         Written as thirty-two lowercase hex digits with no dashes, exactly as
///         <see cref="AssetId" /> is, so the two read as the same kind of thing in a file.
///     </para>
/// </remarks>
/// <param name="Value">The raw identity.</param>
[DataContract]
public readonly record struct EntityId(Guid Value) : ISpanFormattable, ISpanParsable<EntityId> {
    /// <summary>Number of characters <see cref="ToString()" /> writes.</summary>
    public const int TextLength = 32;

    /// <summary>No entity.</summary>
    public static EntityId None => default;

    /// <summary>Whether this names nothing.</summary>
    public bool IsNone => Value == Guid.Empty;

    /// <summary>Mints a fresh identity.</summary>
    /// <returns>The id.</returns>
    public static EntityId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider
    ) => Value.TryFormat(destination, out charsWritten, "N");

    /// <inheritdoc />
    public static EntityId Parse(string s, IFormatProvider? provider = null) =>
        Parse((s ?? throw new ArgumentNullException(nameof(s))).AsSpan(), provider);

    /// <inheritdoc />
    public static EntityId Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result)
            ? result
            : throw new FormatException($"'{s}' is not a scene entity id: expected {TextLength} hex digits.");

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out EntityId result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out EntityId result) {
        if (Guid.TryParseExact(s, "N", out var value)) {
            result = new(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <inheritdoc cref="TryParse(string?, IFormatProvider?, out EntityId)" />
    public static bool TryParse([NotNullWhen(true)] string? s, out EntityId result) => TryParse(s, null, out result);
}

/// <summary>One entity in a scene file, and everything under it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Children are nested rather than each naming a parent, and that is a decision about
///         diffs.</b> A flat list with parent ids is simpler to stream and it makes moving a subtree
///         an edit to <i>n</i> lines scattered through the file; nesting makes it one moved block. A
///         scene is a file people merge by hand often enough that the readable form wins, which is
///         the same reason the whole authoring format is YAML rather than the binary the runtime
///         eventually loads.
///     </para>
///     <para>
///         The transform is the <i>local</i> one, because that is the authored value — a world matrix
///         is derived every frame from the hierarchy and storing it would be storing something the
///         next transform pass overwrites.
///     </para>
/// </remarks>
[DataContract("SceneEntity")]
public sealed class SceneEntityData {
    /// <summary>What names this entity, in this file and in every reference to it.</summary>
    public EntityId Id { get; set; }

    /// <summary>What it is called.</summary>
    public string Name { get; set; } = "Entity";

    /// <summary>Where it is, relative to its parent.</summary>
    public Vector3 Position { get; set; }

    /// <summary>How it is turned, relative to its parent.</summary>
    /// <remarks>
    ///     ⚠ <b>The default is the zero quaternion and not the identity</b>, because a property's
    ///     default is whatever a fresh instance has and a struct's is all-zero. Anything building one
    ///     of these by hand sets it; the reader treats a zero rotation as the identity rather than
    ///     collapsing the entity, which is what a scene written by an older editor would otherwise do.
    /// </remarks>
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>How big it is, relative to its parent.</summary>
    public Vector3 Scale { get; set; } = Vector3.One;

    /// <summary>What hangs from it, in order.</summary>
    /// <remarks>
    ///     ⚠ <b>Settable, which a collection property usually should not be.</b> The YAML binder
    ///     takes part only in members it can write — on <i>both</i> sides, and deliberately: a
    ///     get-only member would be written and then skipped on load, which is a key that appears in
    ///     a diff and vanishes with no edit behind it. A get-only list here is silently an empty
    ///     scene, which is how this was found.
    /// </remarks>
    public List<SceneEntityData> Children { get; set; } = [];

    /// <summary>Renders it as its name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;
}

/// <summary>A scene file.</summary>
/// <remarks>
///     <para>
///         The authoring format — what <c>Assets/**/*.vxscene</c> holds and what a person diffs.
///         The runtime does not read this: a content build compiles it, which is why nothing here is
///         shaped for fast loading and everything is shaped for being read by a human and merged by
///         git.
///     </para>
///     <para>
///         <b>The version is written and is not yet read.</b> A format with no version is one that
///         cannot be changed without guessing what it is looking at, and the cheapest moment to add
///         the field is before the first file exists. <see cref="SceneFile.Current" /> is what a
///         writer stamps; a reader that meets a higher one says so rather than binding half of it.
///     </para>
/// </remarks>
[DataContract("Scene")]
public sealed class SceneFile {
    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the scene is called.</summary>
    public string Name { get; set; } = "Scene";

    /// <summary>The entities with no parent, in order, each carrying its own subtree.</summary>
    /// <remarks>Settable for the reason <see cref="SceneEntityData.Children" /> gives.</remarks>
    public List<SceneEntityData> Roots { get; set; } = [];

    /// <summary>Every entity in the file, roots first and then depth-first under each.</summary>
    /// <remarks>
    ///     The order a reader creates them in, which is also the order that makes a parent exist
    ///     before anything that hangs from it.
    /// </remarks>
    public IEnumerable<SceneEntityData> All() {
        foreach (var root in Roots) {
            foreach (var entity in Walk(root)) {
                yield return entity;
            }
        }

        static IEnumerable<SceneEntityData> Walk(SceneEntityData entity) {
            yield return entity;

            foreach (var child in entity.Children) {
                foreach (var descendant in Walk(child)) {
                    yield return descendant;
                }
            }
        }
    }
}
