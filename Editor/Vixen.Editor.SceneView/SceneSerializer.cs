// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Engine.Transforms;

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
    ///     Already claimed by <c>NativeFormatImporter</c>, which scans one for asset references
    ///     without knowing what is inside it. This is what is inside it.
    /// </remarks>
    public const string Extension = ".vxscene";

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
    public static SceneFile FromYaml(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        var file = YamlSerializer.Parse<SceneFile>(yaml);

        // ⚠ Refused rather than bound as far as it goes. A newer format may have moved what a field
        // means, and a scene half-read is a scene that will be saved back with the other half gone —
        // which is the one failure mode a version field exists to prevent.
        if (file.Version > SceneFile.Current) {
            throw new NotSupportedException(
                $"This scene is version {file.Version} and this editor reads {SceneFile.Current}. "
                + "Opening it would bind the parts it recognises and drop the rest on the next save."
            );
        }

        return file;
    }

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
                : string.Empty
        };

        foreach (var child in Hierarchy.ChildrenOf(document.World, entity)) {
            data.Children.Add(Capture(document, child));
        }

        return data;
    }

    static int Restore(SceneDocument document, SceneEntityData data, Entity parent) {
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
        document.Adopt(entity, data.Id);

        // ⚠ Attached rather than skipped when the name is unknown, and `TryParse` is what decides
        // which. A shape this editor has never heard of leaves the entity in place with no geometry;
        // the next save then writes an empty shape, which does lose the field — the alternative is
        // refusing to open the file at all, and doc 08's argument about unknown keys applies here too.
        if (MeshShapes.TryParse(data.Shape, out var shape)) {
            MeshShapes.Attach(document.World, entity, shape);
        }

        var created = 1;

        // ⚠ Backwards, and the round-trip test is what holds this honest. `Hierarchy.Link` puts a
        // new child at the *head* of the intrusive list — O(1), which is the reason the list is
        // intrusive at all — so creating the file's children in order leaves the world holding them
        // reversed. A scene would then flip its sibling order on every open-and-save: not visibly
        // wrong, and enough to make every scene a merge conflict with itself.
        for (var index = data.Children.Count - 1; index >= 0; index--) {
            created += Restore(document, data.Children[index], entity);
        }

        return created;
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
