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

/// <summary>An editable mesh as a scene file carries it: four flat lists and nothing else.</summary>
/// <remarks>
///     <para>
///         <b>The file's shape rather than the kernel's.</b> <c>Vixen.Geometry</c>'s <c>EditMesh</c>
///         has spans, a lazily-rebuilt edge table and layers; none of that is a thing to write down.
///         What has to survive a save is the four facts everything else is derived from — where the
///         shared positions are, which positions each corner names, how many corners each face has,
///         and which group each face is in — and this is those.
///     </para>
///     <para>
///         ⚠ <b>Positions go through the registered <c>Vector3</c> converter, which writes at
///         round-trip precision.</b> Doc 24's P1 says the scene format is where this phase can go
///         wrong quietly: a vertex list written at whatever <c>float.ToString</c> gives makes every
///         scene a merge conflict with itself, because a file saved, opened and saved again would not
///         be the same bytes. The format already solved this for a transform; a mesh is the first
///         thing in a scene that is not a handful of scalars, and it gets the same answer for free by
///         being made of <c>Vector3</c>s.
///     </para>
///     <para>
///         ⚠ <b>Corner counts rather than start offsets.</b> A start offset is derivable from the
///         counts and is not derivable the other way round without trusting that the file is
///         consistent — so writing the counts means a hand-edited file that has lost a line is a
///         short mesh rather than one whose last face reads off the end of the corner list.
///     </para>
/// </remarks>
[DataContract("SceneMesh")]
public sealed class SceneMeshData {
    /// <summary>The shared positions.</summary>
    public List<Vector3> Positions { get; set; } = [];

    /// <summary>A position index per corner, in face order.</summary>
    public List<int> Corners { get; set; } = [];

    /// <summary>How many corners each face has.</summary>
    public List<int> Faces { get; set; } = [];

    /// <summary>Which group each face is in.</summary>
    /// <remarks>
    ///     Written even when every face is in group zero. A group is what a tool selects and what a
    ///     material is assigned to — see doc 24's D2 — so a file that omitted them when they happened
    ///     to be uniform would lose them the first time somebody grouped a wall and then flattened it.
    /// </remarks>
    public List<int> Groups { get; set; } = [];

    /// <summary>Which smoothing group each face is in, or empty when every edge is hard.</summary>
    /// <remarks>
    ///     ⚠ <b>Written only when something set one, unlike <see cref="Groups" />.</b> A group is what
    ///     a tool selects and is meaningful even when it is zero everywhere; a smoothing group of zero
    ///     is the <i>absence</i> of one, so a list of zeroes per face would be a line per face in every
    ///     block-out scene in the project saying nothing.
    /// </remarks>
    public List<int> Smoothing { get; set; } = [];

    /// <summary>A texture coordinate per corner, or empty when the mesh has never been mapped.</summary>
    /// <remarks>
    ///     ⚠ <b>The one thing here that is not derivable, which is why it has to be written.</b>
    ///     Positions, corners and face counts describe the mesh; a projection is a decision somebody
    ///     made about it, and doc 24's P5 makes several of them per face. It is also the largest thing
    ///     in the record — two numbers a corner — so a mesh nobody has mapped writes an empty list and
    ///     costs one line.
    /// </remarks>
    public List<Vector2> TexCoords { get; set; } = [];
}

/// <summary>A parametric block-out shape as a scene file carries it: a name and six numbers.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's D6, written down.</b> A shape keeps live parameters until somebody edits a face
///         of it, and these are those — so a corridor that should be a metre wider is one number in a
///         diff rather than a mesh rewritten from end to end.
///     </para>
///     <para>
///         ⚠ <b>An entity with these does not write its mesh, and that is the whole reason to have
///         them.</b> The geometry is a function of the parameters, so a file carrying both would carry
///         two answers to one question and would diff against itself the first time a generator's
///         arithmetic changed by a bit. What it costs is that a scene opened by an editor that has
///         never heard of the shape shows the entity with no geometry — the same trade
///         <see cref="SceneEntityData.Shape" /> already made, and the same reason it is a name rather
///         than a number.
///     </para>
///     <para>
///         ⚠ <b>Six fields for twelve shapes, and what each means is per kind.</b> The alternative is a
///         tagged variant per shape, which is a format that grows a case every time somebody adds a
///         stair — see <c>ShapeParameters</c>, which makes the same argument from the other side.
///     </para>
/// </remarks>
[DataContract("SceneShapeParameters")]
public sealed class SceneShapeData {
    /// <summary>Which shape, by name — <c>Box</c>, <c>Stairs</c>, <c>Arch</c> and the rest.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>How big it is across each axis, in world units.</summary>
    public Vector3 Size { get; set; }

    /// <summary>How many divisions around its axis.</summary>
    public int Sides { get; set; }

    /// <summary>How many along it.</summary>
    public int Steps { get; set; }

    /// <summary>How much solid material is left — the header above an opening.</summary>
    public float Thickness { get; set; }

    /// <summary>The ratio a hole through it is of the whole.</summary>
    public float Inner { get; set; }

    /// <summary>Renders it as its kind.</summary>
    /// <returns>The kind.</returns>
    public override string ToString() => Kind;
}

/// <summary>Which material one of an entity's face groups is drawn with.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P5 per-face material.</b> A pair rather than a map keyed by the group number,
///         because a YAML mapping with integer keys reads badly, diffs badly and binds differently
///         depending on how the number was quoted — a list of two-key entries is one line per
///         assignment and is unambiguous.
///     </para>
///     <para>
///         ⚠ <b>The reference text rather than a bare id</b>, for <see cref="SceneEntityData.Asset" />'s
///         reason: <c>ReferenceIndex</c> answers "what breaks if I delete this" by looking for
///         <c>vx:</c> followed by thirty-two hex digits, and an id serialised as a bare scalar is
///         invisible to it — which would let the editor offer to delete a material out from under a
///         level that uses it.
///     </para>
/// </remarks>
[DataContract("SceneFaceMaterial")]
public sealed class SceneFaceMaterialData {
    /// <summary>Which face group.</summary>
    public int Group { get; set; }

    /// <summary>Which material, in <c>vx:</c> form.</summary>
    public string Material { get; set; } = string.Empty;

    /// <summary>Renders it as its group.</summary>
    /// <returns>The group.</returns>
    public override string ToString() => Group.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>How a scene used to carry a shape. Read, and never written.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Legacy.</b> <c>PrimitiveShape</c> is <c>Vixen.Rendering</c>'s component now, so a
    ///         shape is an ordinary entry in <see cref="Components" /> and <c>SceneSerializer.Capture</c>
    ///         never fills this in again. It stays because every scene authored before that carries a
    ///         shape here, and the binder ignores keys it does not know — so removing the property would
    ///         not fail to open those files, it would open them and quietly drop the geometry. Reading it
    ///         is what makes the migration lossless; a file rewrites itself into the new form on its
    ///         first save.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written as an empty string rather than left out</b>, because <c>OmitDefaults</c> is a
    ///         property of the whole document and is deliberately off for this format. A newly saved
    ///         scene therefore carries <c>shape: ''</c> and <c>light: null</c> and means nothing by
    ///         either — dead weight until either the format drops a version or the mapper grows
    ///         member-level omission.
    ///     </para>
    ///     <para>
    ///         <b>The name and not the number, while it lasted.</b> A <c>PrimitiveKind</c> written as its
    ///         integer would have made the enum's declaration order part of the file format for ever — a
    ///         member inserted in the middle would turn every saved cube into a sphere, in a diff that
    ///         shows nothing wrong. A name this editor does not recognise is read as empty rather than
    ///         refused; see <c>PrimitiveShapes.TryParse</c>.
    ///     </para>
    /// </remarks>
    public string Shape { get; set; } = string.Empty;

    /// <summary>How a scene used to carry a light. Read, and never written.</summary>
    /// <remarks>
    ///     ⚠ <b>Legacy, for the reason <see cref="Shape" /> gives at length.</b> <c>Light</c> is
    ///     <c>Vixen.Rendering</c>'s component now and is written as an entry in
    ///     <see cref="Components" />; this is read so that every scene authored before that keeps its
    ///     lighting. It matters more here than for a shape — a light is seven numbers behind a name, and
    ///     an entity that lost them would be one somebody has to relight by eye.
    /// </remarks>
    public SceneLightData? Light { get; set; }

    /// <summary>The asset this entity is an instance of, in <c>vx:</c> form, or empty for none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Written as the reference text rather than as a bare id, and that is what makes it
    ///         findable.</b> <c>ReferenceIndex</c> answers "what breaks if I delete this" by scanning
    ///         every file for <c>vx:</c> followed by thirty-two hex digits — doc 08 chose that form
    ///         partly so a grep would work — and an <c>AssetId</c> serialised as a bare scalar, which
    ///         is what the binder would do with one, is invisible to it. A scene that referenced an
    ///         asset the index could not see is a scene the editor would offer to delete the asset out
    ///         from under.
    ///     </para>
    ///     <para>
    ///         Its own key rather than an entry in <see cref="Components" />, for the reason
    ///         <see cref="Light" /> gives: the component carrying it is the editor's, because the
    ///         runtime has nothing that holds an <c>AssetId</c> yet.
    ///     </para>
    /// </remarks>
    public string Asset { get; set; } = string.Empty;

    /// <summary>The editable geometry this entity carries, or <see langword="null" /> for none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Its own key rather than an entry in <see cref="Components" />, and doc 24's B3 is the
    ///         bargain being kept.</b> A component no build declares is what a content compile refuses,
    ///         and an <c>EditMesh</c> is not a component — it is a few thousand numbers that belong to
    ///         one entity in one scene. Blockout geometry is level data rather than a shared asset: a
    ///         designer who had to save six meshes to disk to try a corridor has been given the DCC
    ///         round-trip back under a different name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Flat lists rather than a nested structure, and that is what makes the file
    ///         readable and the diff small.</b> A face is a run of corners; writing each face as its
    ///         own mapping would triple the line count of every mesh and make a one-vertex move a diff
    ///         across the whole block. See <see cref="SceneMeshData" />.
    ///     </para>
    /// </remarks>
    public SceneMeshData? Mesh { get; set; }

    /// <summary>The live parameters its geometry is generated from, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Mutually exclusive with <see cref="Mesh" />, and the writer is what keeps that true.</b>
    ///     An entity with parameters has a mesh in the editor as well — that is what draws and what a
    ///     click selects — but it is derived, so writing it would put the same geometry in the file
    ///     twice with no rule for which one wins when they disagree. A hand-edited file carrying both
    ///     is read as parametric, because the parameters are the smaller and more deliberate statement.
    /// </remarks>
    public SceneShapeData? Parameters { get; set; }

    /// <summary>A material per face group, for the groups that name one.</summary>
    /// <remarks>
    ///     ⚠ <b>Survives a shape being regenerated, unlike everything else about its geometry.</b> The
    ///     assignment is against the group rather than against a face, and a generator puts the same
    ///     face in the same group whatever its parameters are — so a designer can dress a corridor and
    ///     still make it a metre wider.
    /// </remarks>
    public List<SceneFaceMaterialData> Materials { get; set; } = [];

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

/// <summary>One entity's light, as a scene file holds it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The kind is written as its name and not as its number</b>, the argument
///         <see cref="SceneEntityData.Shape" /> makes at length: <c>LightKind</c>'s values are shared
///         with the shader and are therefore fixed, but writing the integer would put that agreement
///         into every saved scene as well — so a renumbering that a future format migration could
///         otherwise handle in one place would silently turn every spot light in the project into a
///         point light instead.
///     </para>
///     <para>
///         <b>Where the light is and which way it faces are not here.</b> They are the entity's
///         transform, and a light that carried its own would be a second answer to a question the
///         file has already answered — the same rule that keeps <c>!LocalTransform</c> out of
///         <see cref="SceneEntityData.Components" />.
///     </para>
///     <para>
///         Angles are radians, matching the authored record they load into rather than the degrees an
///         inspector shows: one conversion at the edge where a person types, and none in the file.
///     </para>
/// </remarks>
[DataContract("SceneLight")]
public sealed class SceneLightData {
    /// <summary>Which kind of light — <c>Directional</c>, <c>Point</c>, <c>Spot</c>, <c>Rect</c> or <c>Tube</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Its colour, before intensity.</summary>
    public Color3 Colour { get; set; } = new(1f, 1f, 1f);

    /// <summary>How bright it is, as a multiplier on <see cref="Colour" />.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>The distance at which it reaches zero. Unused by a directional light.</summary>
    public float Range { get; set; }

    /// <summary>Its sphere radius, or a rectangle's half-height.</summary>
    public float Radius { get; set; }

    /// <summary>The inner cone half-angle in radians.</summary>
    public float InnerAngle { get; set; }

    /// <summary>The outer cone half-angle in radians.</summary>
    public float OuterAngle { get; set; }

    /// <summary>Half a tube's length, or half a rectangle's width.</summary>
    public float HalfLength { get; set; }

    /// <summary>Renders it as its kind.</summary>
    /// <returns>The kind.</returns>
    public override string ToString() => Kind;
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
