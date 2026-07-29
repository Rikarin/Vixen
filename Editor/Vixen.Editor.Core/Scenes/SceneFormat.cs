// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;

namespace Vixen.Editor.Core.Scenes;

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

    /// <summary>What it carries besides its transform, each tagged with what it is.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Boxed values with a type tag, which is how the YAML dialect already does
    ///         polymorphism</b> — <c>importer: !TextureImporter</c> in a <c>.meta</c> is the same
    ///         mechanism. A component is written as <c>- !Camera</c> and the keys under it, so the
    ///         file names the component the same way the compiled scene does and the same way the
    ///         binary serializer does: by its <c>[DataContract]</c> alias.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An entry with no tag is an error, and the message is about <c>object</c>.</b>
    ///         The binder resolves a tag against the type registry and has nothing to fall back on
    ///         when there is none, so a hand-written entry that forgot its <c>!</c> is reported as
    ///         "Object has no descriptor" against the path of the entry. That is the one rough edge
    ///         of declaring the member this way, and it is worth it: the alternative is every
    ///         component in the engine implementing a marker interface to be authorable.
    ///     </para>
    ///     <para>
    ///         <b>The transform is not in here.</b> <see cref="Position" />, <see cref="Rotation" />
    ///         and <see cref="Scale" /> are the authored transform, so a <c>!LocalTransform</c> entry
    ///         would be a second answer to a question the file has already answered — the compiler
    ///         refuses one rather than picking.
    ///     </para>
    /// </remarks>
    public List<object> Components { get; set; } = [];

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
///         <b>The version is written and is read.</b> A format with no version is one that cannot be
///         changed without guessing what it is looking at, and the cheapest moment to add the field
///         is before the first file exists. <see cref="SceneFile.Current" /> is what a writer stamps;
///         a reader that meets a higher one says so rather than binding half of it.
///     </para>
///     <para>
///         <b>It lives here rather than beside the viewport because two things read it</b>: the panel
///         that edits a scene, and the importer that compiles one into the asset a player loads.
///         Neither should have to reference the other, and a second binding of one format is a second
///         thing to keep in step — which is how a file comes to mean one thing when it is saved and
///         another when it is built.
///     </para>
/// </remarks>
[DataContract("Scene")]
public sealed class SceneFile {
    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>What a scene is written as.</summary>
    public const string Extension = ".vxscene";

    /// <summary>What a prefab is written as — the same format, with one root.</summary>
    public const string PrefabExtension = ".vxprefab";

    /// <summary>
    ///     Teaches the binder how a vector reads before anything asks it to read one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A static constructor rather than a module initializer</b>, so the process-wide
    ///     converter table changes when a scene is first read or written rather than when this
    ///     assembly is merely referenced — see <see cref="SceneScalars.Register" />. It is on this
    ///     type because the binder constructs one before it binds anything under it, so there is no
    ///     path into the format that does not go through here first.
    /// </remarks>
    static SceneFile() => SceneScalars.Register();

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the scene is called.</summary>
    public string Name { get; set; } = "Scene";

    /// <summary>The entities with no parent, in order, each carrying its own subtree.</summary>
    /// <remarks>Settable for the reason <see cref="SceneEntityData.Children" /> gives.</remarks>
    public List<SceneEntityData> Roots { get; set; } = [];

    /// <summary>Reads YAML into a file.</summary>
    /// <param name="yaml">The text.</param>
    /// <returns>The file.</returns>
    /// <exception cref="YamlParseException">The text is not YAML.</exception>
    /// <exception cref="YamlBindingException">The document is not a scene.</exception>
    /// <exception cref="NotSupportedException">The file is from a newer editor.</exception>
    /// <remarks>
    ///     ⚠ <b>A newer file is refused rather than bound as far as it goes.</b> A newer format may
    ///     have moved what a field means, and a scene half-read is a scene that will be saved back
    ///     with the other half gone — which is the one failure mode a version field exists to
    ///     prevent. A build refuses it for the same reason from the other side: compiling half a
    ///     level is worse than not compiling it.
    /// </remarks>
    public static SceneFile FromYaml(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        var file = YamlSerializer.Parse<SceneFile>(yaml);

        return file.Version <= Current
            ? file
            : throw new NotSupportedException(
                $"This scene is version {file.Version} and this build reads {Current}. Reading it would bind "
                + "the parts it recognises and drop the rest."
            );
    }

    /// <summary>Writes it as YAML.</summary>
    /// <returns>The text, ending in a newline.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(this);

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
