// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Editor.Assets.Animation;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors;

/// <summary>The one thing doc 34's editors cannot do for themselves: reach another asset.</summary>
/// <remarks>
///     <para>
///         <b>Four documents, four hooks, one place.</b> A proxy shape set needs the rig it hangs
///         off, a move set needs the sets it overlays, a clip needs the scene it was marked up
///         against, and a harness plan needs everything it names by path. Each of them deliberately
///         refuses to go looking — a document that knew how a project is laid out would be a
///         document no test could open — so each declares the shape of the answer and somebody with
///         a project supplies it. This is that somebody.
///     </para>
///     <para>
///         ⚠ <b>The panels are inert without it, which is what makes this the last mile rather than
///         a nicety.</b> Unbound, the shape viewport draws nothing, Run says it has no project and
///         Propose Contacts says it has no scene — all three honest, all three useless. Every one of
///         those messages is a sentence about this class not having been constructed.
///     </para>
///     <para>
///         ⚠ <b>Rigs are read from the source file rather than from the built asset.</b> The
///         alternative is the catalog <c>EditorContent</c> mounts, which is the same data the game
///         gets — and which does not exist until the project has been imported once. A shape editor
///         that worked only after a successful content build would be unopenable in exactly the
///         situation somebody opens it: a body just dragged into the project. The source is read
///         through <see cref="ModelReader" /> with the asset's own import settings, so the joints
///         are the joints the import will produce.
///     </para>
/// </remarks>
/// <param name="project">The project everything is resolved against.</param>
internal sealed class AnimationBinder(EditorProject project) {
    /// <summary>Rigs already read, by path, with what the file looked like when they were read.</summary>
    /// <remarks>
    ///     ⚠ <b>Cached because reading one is Assimp opening a character, and stamped because a cache
    ///     nothing invalidates is a rig that never updates.</b> A miss is recorded as a null entry
    ///     rather than left out: the shape panel asks on every keystroke, and a body whose model has
    ///     not been exported yet would otherwise re-open the same missing file a hundred times a
    ///     second. Re-exporting the model changes the stamp, so the next ask reads it again.
    /// </remarks>
    readonly Dictionary<string, (DateTime Stamp, Skeleton? Rig)> rigs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Connects a freshly-opened document to the project it belongs to.</summary>
    /// <param name="document">Whatever was just opened.</param>
    /// <remarks>
    ///     ⚠ <b>Anything not doc 34's is left alone, deliberately.</b> This is called for every
    ///     document the editor opens, so the switch falling through is the normal case and not a
    ///     missing branch.
    /// </remarks>
    public void Bind(EditorDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        switch (document) {
            case ProxyShapeDocument shapes:
                // ⚠ Now and on every change, because the rig is a property rather than a callback and
                // the field naming it is one somebody edits in the panel. Both reads are a dictionary
                // lookup once the model has been opened once.
                Resolve(shapes);
                shapes.Changed += Resolve;

                break;

            case MoveSetDocument moves:
                moves.Resolve = MoveSet;

                break;

            case AnimationClipDocument clip:
                clip.Scene = Proposals;

                break;

            case HarnessDocument harness:
                harness.Resolve = Inputs;

                break;
        }
    }

    /// <summary>The skeleton a model's file declares, or <see langword="null" />.</summary>
    /// <param name="path">Where the model is, relative to the project root.</param>
    /// <returns>The rig, or <see langword="null" /> if there is no file, no skin, or no reading it.</returns>
    public Skeleton? Rig(string path) {
        if (Absolute(path) is not { } absolute || Stamp(absolute) is not { } stamp) {
            return null;
        }

        if (rigs.TryGetValue(absolute, out var cached) && cached.Stamp == stamp) {
            return cached.Rig;
        }

        var rig = Read(absolute);

        rigs[absolute] = (stamp, rig);

        return rig;
    }

    /// <summary>A shape set, read from its file.</summary>
    /// <param name="path">Where it is, relative to the project root.</param>
    /// <returns>The set, or <see langword="null" />.</returns>
    public ProxyShapeSetContent? Shapes(string path) => Parse<ProxyShapeSetContent>(path);

    /// <summary>A vocabulary, read from its file and baked.</summary>
    /// <param name="path">Where it is, relative to the project root.</param>
    /// <returns>The vocabulary, or <see langword="null" />.</returns>
    public ShapeVocabulary? Vocabulary(string path) => Parse<ShapeVocabularyContent>(path)?.Bake();

    /// <summary>A priority ladder, read from its file and baked.</summary>
    /// <param name="path">Where it is, relative to the project root.</param>
    /// <returns>The ladder, or <see langword="null" />.</returns>
    public PriorityLadder? Ladder(string path) => Parse<PriorityLadderContent>(path)?.Bake();

    /// <summary>A move set, read from its file.</summary>
    /// <param name="path">Where it is, relative to the project root.</param>
    /// <returns>The set, or <see langword="null" />.</returns>
    public MoveSetContent? MoveSet(string path) => Parse<MoveSetContent>(path);

    /// <summary>An authored clip, read from its file.</summary>
    /// <param name="path">Where it is, relative to the project root.</param>
    /// <returns>The clip, or <see langword="null" />.</returns>
    public AnimationClipAsset? Clip(string path) {
        if (Absolute(path) is not { } absolute || !File.Exists(absolute)) {
            return null;
        }

        try {
            return AnimationClipAsset.FromYaml(File.ReadAllText(absolute));
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException or IOException) {
            return null;
        }
    }

    /// <summary>The shape set that says it was authored against a rig, or empty.</summary>
    /// <param name="rig">The model's path, relative to the project root.</param>
    /// <returns>The set's path, or an empty string.</returns>
    /// <remarks>
    ///     ⚠ <b>The link is read off the set rather than off the model.</b> A body has one rig and a
    ///     rig may be worn by several bodies — a rig that listed its sets would be a file an artist
    ///     has to edit every time somebody adds a set, and re-exporting the model would overwrite it.
    ///     First match wins; two sets naming one rig is a project with a spare body in it, not a
    ///     question worth an error.
    /// </remarks>
    public string ShapesFor(string rig) {
        if (string.IsNullOrWhiteSpace(rig)) {
            return string.Empty;
        }

        foreach (var entry in project.Assets.Entries) {
            if (!entry.Path.EndsWith(ProxyShapeSetContent.Extension, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (Shapes(entry.Path) is { } set && string.Equals(set.Rig, rig, StringComparison.OrdinalIgnoreCase)) {
                return entry.Path;
            }
        }

        return string.Empty;
    }

    /// <summary>Which joints the proposal pass watches on a body.</summary>
    /// <param name="rig">The rig.</param>
    /// <param name="shapes">The body's own shapes.</param>
    /// <returns>One effector per shape worth watching.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The places somebody modelled are the places worth watching, and nothing else in
    ///         the project says which those are.</b> A proxy shape is a named point on the body that
    ///         an author cared enough to write down — a palm, a fingertip, a heel — which is exactly
    ///         what an effector is. Deriving them means the list is right by construction: adding a
    ///         palm to the set adds a palm to the pass.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A shape on the root is not one.</b> The root carries the body's own volume and, in
    ///         an augmented set, everything the scene put there — a prop watching for contacts with
    ///         the character is the pass run backwards.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The chain root is two joints up, which is a limb.</b> Wrist, elbow, shoulder is
    ///         the two-bone chain <c>ChainSolver</c> is written for; taking the immediate parent would
    ///         propose constraints that can only bend one joint, and taking the skeleton root would
    ///         propose ones that move the whole body to touch a mug.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<ProposalEffector> Effectors(Skeleton rig, ProxyShapeSet shapes) {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(shapes);

        List<ProposalEffector> found = [];

        foreach (var shape in shapes.Shapes) {
            if (shape.Joint <= 0 || shape.Joint >= rig.JointCount) {
                continue;
            }

            var offset = shape.Offset.Translation;

            if (found.Any(other => other.Joint == shape.Joint && other.Offset == offset)) {
                continue;
            }

            found.Add(new(shape.Joint, Limb(rig, shape.Joint), offset));
        }

        return found;
    }

    /// <summary>Binds a shape set's two references, both of which the file names by path.</summary>
    void Resolve(ProxyShapeDocument document) {
        document.Rig = Rig(document.Set.Rig);
        document.Vocabulary = Vocabulary(document.Set.Vocabulary);
    }

    /// <summary>What the proposal pass needs, from what the clip names.</summary>
    /// <remarks>
    ///     ⚠ <b>Every step can fail and the failure is <see langword="null" />, not a guess.</b> The
    ///     clip's own message says which of them it was — no context named, or a context that would
    ///     not read — and a pass run against the wrong body would produce confident nonsense, which
    ///     is the one output nobody can review.
    /// </remarks>
    ProposalInputs? Proposals(AnimationClipDocument document) {
        if (document.Clip.AuthoringContext.Length == 0 || Sequence(document.Clip.AuthoringContext) is not { } sequence) {
            return null;
        }

        var context = AuthoringContext.From(sequence);

        if (context.Subject is not { } subject || !project.Assets.TryGetByGuid(subject.Asset, out var model)) {
            return null;
        }

        if (Rig(model.Path) is not { } rig || Shapes(ShapesFor(model.Path)) is not { } body) {
            return null;
        }

        // ⚠ Measured at the middle of the clip rather than at its start. A scene's transform tracks
        // are keyed where something happens, and time zero is the frame before anybody has moved —
        // so a prop picked up a second in is, at zero, still on the table.
        var baked = body.Bake(rig);
        var augmented = context.Augment(baked, rig, document.Clip.Duration / 2f);

        // ⚠ No ladder, because a clip names none. Priorities matter when two goals compete for one
        // chain, which is a question for the solve and not for a pass that only measures distance —
        // and inventing "the project's one ladder" here would be the editor deciding something the
        // file it is about does not say.
        return new(rig, augmented, Effectors(rig, baked), ProposalSettings.Default);
    }

    /// <summary>A sequence, read from its file.</summary>
    SequenceAsset? Sequence(string path) {
        if (Absolute(path) is not { } absolute || !File.Exists(absolute)) {
            return null;
        }

        try {
            return SequenceAsset.FromYaml(File.ReadAllText(absolute));
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException or NotSupportedException or IOException) {
            return null;
        }
    }

    /// <summary>What a harness plan names, found.</summary>
    HarnessInputs? Inputs(HarnessPlanContent plan) {
        if (Clip(plan.Clip) is not { } clip || Rig(plan.Rig) is not { } rig) {
            return null;
        }

        return new(rig, clip.ToContent(), Shapes(plan.Shapes)?.Bake(rig), Ladder(plan.Priorities));
    }

    /// <summary>Reads a YAML asset, answering null for anything that is not there or will not bind.</summary>
    T? Parse<T>(string path) where T : class {
        if (Absolute(path) is not { } absolute || !File.Exists(absolute)) {
            return null;
        }

        try {
            var text = File.ReadAllText(absolute);

            return text.Trim().Length == 0 ? null : YamlSerializer.Parse<T>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException or IOException) {
            // ⚠ Swallowed rather than reported, because every caller already has somewhere better to
            // say it. The panels answer "'x' could not be loaded" naming the path somebody typed,
            // which is more use than a stack trace about a YAML node.
            return null;
        }
    }

    string? Absolute(string path) => string.IsNullOrWhiteSpace(path) ? null : project.Paths.Absolute(path);

    static DateTime? Stamp(string absolute) => File.Exists(absolute) ? File.GetLastWriteTimeUtc(absolute) : null;

    /// <summary>Opens a model and takes its skeleton.</summary>
    static Skeleton? Read(string absolute) {
        try {
            // ⚠ The asset's own settings, off its sidecar. Scale and axis conversion are import
            // settings, so a rig read with the defaults would be a different rig from the one the
            // game plays — the same shapes, in the wrong places, on a body a metre tall.
            var meta = File.Exists(AssetMetaFile.PathFor(absolute)) ? AssetMetaFile.ReadFile(AssetMetaFile.PathFor(absolute)) : null;
            var settings = meta?.Importer as ModelImportSettings ?? new ModelImportSettings();

            var read = ModelReader.Read(
                File.ReadAllBytes(absolute),
                Path.GetExtension(absolute),
                Path.GetFileNameWithoutExtension(absolute),
                settings
            );

            return read.Skeleton is { } data && Skeleton.TryCreate(data, out var rig, out _) ? rig : null;
        } catch (Exception exception) when (exception is ModelFormatException or IOException or YamlParseException or YamlBindingException) {
            return null;
        }
    }

    /// <summary>Two joints up, or as far up as there is.</summary>
    static int Limb(Skeleton rig, int joint) {
        var parent = rig.ParentOf(joint);

        if (parent < 0) {
            return joint;
        }

        var grandparent = rig.ParentOf(parent);

        return grandparent < 0 ? parent : grandparent;
    }
}
