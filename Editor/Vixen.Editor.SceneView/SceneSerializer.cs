// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>Turns a scene document into a file and back.</summary>
/// <remarks>
///     <para>
///         <b>The authoring format, not the runtime one.</b> A content build compiles a
///         <c>.vxscene</c> into whatever loads fastest; this is what a person opens, diffs and
///         resolves a merge in. That is why it is YAML through the same binder a material and a
///         settings asset go through, and why nothing about it is shaped for load speed.
///     </para>
///     <para>
///         <b>Reading is not the reverse of writing, and the asymmetry is the whole design.</b>
///         Writing walks the world and asks the document what each entity is called and what it is
///         named in a file. Reading creates entities in a world that has no idea what they used to
///         be, and hands each one the id the file gave it — so a second save writes the same ids, a
///         reference between entities survives a round trip, and a scene that has been opened and
///         saved is byte-identical to the one that went in.
///     </para>
///     <para>
///         ⚠ <b>A parent is created before anything that hangs from it.</b> The file nests children,
///         so a depth-first walk gets that for free; a flat file with parent ids would need two
///         passes, which is one of the reasons the format nests.
///     </para>
/// </remarks>
public static class SceneSerializer {
    /// <summary>
    ///     Teaches the binder how a vector reads before anything asks it to read one.
    /// </summary>
    /// <remarks>
    ///     A static constructor rather than a module initializer, so the process-wide converter table
    ///     changes when a scene is first read or written rather than when this assembly is merely
    ///     referenced — see <see cref="SceneScalars.Register" />.
    /// </remarks>
    static SceneSerializer() => SceneScalars.Register();

    /// <summary>The extension a scene is written as.</summary>
    /// <remarks>
    ///     Claimed by <c>SceneImporter</c>, which reads the same file through the same binder and
    ///     compiles it into the asset a player loads. This is the half that edits one.
    /// </remarks>
    public const string Extension = SceneFile.Extension;

    /// <summary>Reads a document into a file.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The file.</returns>
    public static SceneFile ToFile(SceneDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var file = new SceneFile { Name = document.Title.Peek() };

        foreach (var root in document.Roots) {
            file.Roots.Add(Capture(document, root));
        }

        return file;
    }

    /// <summary>Writes a document as YAML.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The text of the file.</returns>
    public static string ToYaml(SceneDocument document) => YamlSerializer.ToYaml(ToFile(document));

    /// <summary>Writes a document to a path, creating the directory if it is not there.</summary>
    /// <param name="document">The document.</param>
    /// <param name="path">Where to write it.</param>
    /// <remarks>
    ///     ⚠ <b>Written to a temporary file and moved into place.</b> A save interrupted halfway —
    ///     a full disk, a crash, a pulled cable — otherwise leaves a truncated scene where the work
    ///     was, and the file it destroyed is the one thing that cannot be rebuilt.
    /// </remarks>
    public static void Save(SceneDocument document, string path) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";

        File.WriteAllText(temporary, ToYaml(document));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Reads YAML into a file.</summary>
    /// <param name="yaml">The text.</param>
    /// <returns>The file.</returns>
    /// <exception cref="YamlException">The text is not a scene.</exception>
    /// <exception cref="NotSupportedException">The file is from a newer editor.</exception>
    /// <remarks>
    ///     The format's own reader, which is where the version check lives — an editor opening a
    ///     newer scene and a build compiling one are the same refusal for the same reason, so there
    ///     is one of it.
    /// </remarks>
    public static SceneFile FromYaml(string yaml) => SceneFile.FromYaml(yaml);

    /// <summary>Puts a file's entities into a document.</summary>
    /// <param name="document">The document to fill. Expected to be empty.</param>
    /// <param name="file">The file.</param>
    /// <returns>How many entities were created.</returns>
    /// <remarks>
    ///     ⚠ <b>The stack is cleared and the document marked clean afterwards.</b> Loading is not an
    ///     edit somebody made: a scene that opened with fifty undo steps already on it is one where
    ///     the first Ctrl+Z does something inexplicable.
    /// </remarks>
    public static int Load(SceneDocument document, SceneFile file) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(file);

        var created = 0;

        foreach (var root in file.Roots) {
            created += Restore(document, root, Entity.Null);
        }

        document.Stack.Clear();
        document.Stack.MarkClean();

        return created;
    }

    /// <summary>Reads a scene off disk into a document.</summary>
    /// <param name="document">The document to fill.</param>
    /// <param name="path">Where the file is.</param>
    /// <returns>How many entities were created, or zero when there is no file there.</returns>
    /// <remarks>
    ///     A missing file is not an error and means an empty scene, for the reason
    ///     <c>ProjectSettingsStore</c> gives about a missing settings file: a fresh checkout should
    ///     open rather than refuse to.
    /// </remarks>
    public static int Load(SceneDocument document, string path) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return File.Exists(path) ? Load(document, FromYaml(File.ReadAllText(path))) : 0;
    }

    static SceneEntityData Capture(SceneDocument document, Entity entity) {
        var local = document.World.Read<LocalTransform>(entity);

        var data = new SceneEntityData {
            Id = document.IdOf(entity),
            Name = document.NameOf(entity),
            Position = local.Position,
            Rotation = local.Rotation,
            Scale = local.Scale,

            Shape = MeshShapes.TryGet(document.World, entity, out var shape)
                ? MeshShapes.NameOf(shape)
                : string.Empty,

            Light = Lights.TryGet(document.World, entity, out var light) ? Written(light) : null
        };

        foreach (var binder in Carried(document.World, entity)) {
            data.Components.Add(binder.ValueOn(document.World, entity));
        }

        foreach (var child in Hierarchy.ChildrenOf(document.World, entity)) {
            data.Children.Add(Capture(document, child));
        }

        return data;
    }

    /// <summary>A light as the file holds it.</summary>
    static SceneLightData Written(Light light) =>
        new() {
            Kind = Lights.NameOf(light.Kind),
            Colour = light.Colour,
            Intensity = light.Intensity,
            Range = light.Range,
            Radius = light.Radius,
            InnerAngle = light.InnerAngle,
            OuterAngle = light.OuterAngle,
            HalfLength = light.HalfLength
        };

    /// <summary>A light as the world holds it.</summary>
    /// <remarks>
    ///     ⚠ <b>The kind is passed in already parsed</b> rather than read from the record, because
    ///     the caller has had to parse it to know whether there is a light here at all — and doing it
    ///     twice is how the two copies eventually disagree about what an unknown kind means.
    /// </remarks>
    static Light Read(SceneLightData data, LightKind kind) =>
        new() {
            Kind = kind,
            Colour = data.Colour,
            Intensity = data.Intensity,
            Range = data.Range,
            Radius = data.Radius,
            InnerAngle = data.InnerAngle,
            OuterAngle = data.OuterAngle,
            HalfLength = data.HalfLength
        };

    /// <summary>Which of an entity's components a scene file can hold, in name order.</summary>
    /// <remarks>
    ///     <para>
    ///         The registry is the filter, and everything else on the entity is left out on purpose:
    ///         the hierarchy links hold entity handles that mean nothing in another world, and a
    ///         component with no contract has no name to be written under. Both are the same rule the
    ///         compiled form applies, so what an author sees saved is what a build will compile.
    ///     </para>
    ///     <para>
    ///         Sorted by name, because a file that reordered an entity's components between saves
    ///         would be a diff with no edit behind it — the same argument this format makes about
    ///         sibling order.
    ///     </para>
    /// </remarks>
    static IEnumerable<ISceneComponentBinder> Carried(World world, Entity entity) {
        var carried = new List<ISceneComponentBinder>();

        foreach (var id in world.ArchetypeOf(entity).Signature.Ids) {
            if (SceneComponentRegistry.TryGet(ComponentRegistry.Get(id).Type, out var binder)) {
                carried.Add(binder);
            }
        }

        carried.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return carried;
    }

    /// <summary>Creates one file entity, and everything under it, inside a document.</summary>
    /// <param name="document">The document to create in.</param>
    /// <param name="data">The entity, as a file holds it.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <param name="sources">
    ///     Filled with each created entity and the id the <i>file</i> gave it, or
    ///     <see langword="null" /> to adopt those ids as the document's own.
    /// </param>
    /// <returns>The entity created for <paramref name="data" /> itself.</returns>
    /// <remarks>
    ///     <para>
    ///         <see cref="Load(SceneDocument,SceneFile)" /> is this with <paramref name="sources" />
    ///         null: reading a scene means the file's identities <i>are</i> the document's, which is
    ///         what makes a save, load and save cycle a no-op in the diff.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A prefab instance is the other case, and it must not adopt.</b> Two instances of
    ///         one prefab in one scene would claim the same ids, and every reference between entities
    ///         would then name whichever of them was reached last. Passing a map records where each
    ///         entity came from without giving it the template's identity — which is also exactly what
    ///         an override comparison needs.
    ///     </para>
    /// </remarks>
    public static Entity Instantiate(
        SceneDocument document,
        SceneEntityData data,
        Entity parent = default,
        IDictionary<Entity, EntityId>? sources = null
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(data);

        return Create(document, data, parent, sources);
    }

    static int Restore(SceneDocument document, SceneEntityData data, Entity parent) {
        var before = document.World.EntityCount;
        Create(document, data, parent, sources: null);

        return document.World.EntityCount - before;
    }

    static Entity Create(
        SceneDocument document,
        SceneEntityData data,
        Entity parent,
        IDictionary<Entity, EntityId>? sources
    ) {
        var local = new LocalTransform {
            Position = data.Position,

            // ⚠ A zero quaternion is the identity here, not a rotation. `default(Quaternion)` is
            // all-zero and produces a degenerate matrix; a file written by hand, or by an older
            // editor that did not write the field, would otherwise collapse the entity to nothing.
            Rotation = data.Rotation == default ? Quaternion.Identity : data.Rotation,

            // The same argument for scale, where the symptom is worse: a zero scale is an entity
            // that is present, selectable and invisible.
            Scale = data.Scale == default ? Vector3.One : data.Scale
        };

        var entity = document.Add(data.Name, local, parent);

        if (sources is null) {
            document.Adopt(entity, data.Id);
        } else if (!data.Id.IsNone) {
            sources[entity] = data.Id;
        }

        // ⚠ Attached rather than skipped when the name is unknown, and `TryParse` is what decides
        // which. A shape this editor has never heard of leaves the entity in place with no geometry;
        // the next save then writes an empty shape, which does lose the field — the alternative is
        // refusing to open the file at all, and doc 08's argument about unknown keys applies here too.
        //
        // ⚠ Its own field rather than one of the components below, and that is worth a second look
        // now that a scene can carry arbitrary components. `MeshShape` is the editor's, not the
        // runtime's — see its own remarks — so it has nothing to register with
        // `SceneComponentRegistry`, and a scene naming a type no build declares is exactly what the
        // loop below refuses. When the runtime grows a mesh component this field becomes one of them.
        if (MeshShapes.TryParse(data.Shape, out var shape)) {
            MeshShapes.Attach(document.World, entity, shape);
        }

        // ⚠ The same tolerance, and it matters more here: a light has seven numbers behind its name,
        // so an entity whose kind this editor does not recognise keeps its transform and its
        // children and loses only the lighting. Refusing the file would cost the scene.
        if (data.Light is { } written && Lights.TryParse(written.Kind, out var kind)) {
            Lights.Attach(document.World, entity, Read(written, kind));
        }

        foreach (var component in data.Components) {
            // ⚠ Refused rather than dropped. A component the binder bound and the registry does not
            // know is a type this build has and nothing declared a scene may carry; keeping the
            // entity without it would open the scene, look right, and delete the component from the
            // file on the next save — which is the failure the format's version check exists for,
            // arriving through a different door.
            if (!SceneComponentRegistry.TryGet(component.GetType(), out var binder)) {
                throw new SceneComponentException(
                    $"'{data.Name}' carries a {component.GetType().Name}, which nothing registered as a scene "
                    + "component. Call SceneComponentRegistry.Register for it where the game's components are "
                    + "registered; a scene cannot hold what a build cannot compile."
                );
            }

            binder.AddTo(document.World, entity, component);
        }

        // ⚠ Backwards, and the round-trip test is what holds this honest. `Hierarchy.Link` puts a
        // new child at the *head* of the intrusive list — O(1), which is the reason the list is
        // intrusive at all — so creating the file's children in order leaves the world holding them
        // reversed. A scene would then flip its sibling order on every open-and-save: not visibly
        // wrong, and enough to make every scene a merge conflict with itself.
        for (var index = data.Children.Count - 1; index >= 0; index--) {
            Create(document, data.Children[index], entity, sources);
        }

        return entity;
    }
}

/// <summary>Writes a scene to a path.</summary>
/// <remarks>
///     The implementation of <see cref="ISceneWriter" /> the editor uses. It is a separate type
///     rather than a method on the serializer because a document holds one and a document does not
///     know where it lives — the path is the shell's answer, decided when the scene was opened or
///     when a Save As dialog last ran.
/// </remarks>
/// <param name="path">Where to write it.</param>
public sealed class SceneFileWriter(string path) : ISceneWriter {
    /// <summary>Where the scene is written.</summary>
    public string Path { get; } = !string.IsNullOrEmpty(path)
        ? path
        : throw new ArgumentException("A scene writer needs a path to write to.", nameof(path));

    /// <inheritdoc />
    public void Write(SceneDocument document) => SceneSerializer.Save(document, Path);
}
