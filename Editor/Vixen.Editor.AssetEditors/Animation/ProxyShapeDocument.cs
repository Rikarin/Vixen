// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>A body's proxy shapes, open for editing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Its own document rather than a tab of the model editor, and doc 34 says the
///         opposite.</b> A <c>.vxproxyshapes</c> is its own asset with its own GUID, its own importer
///         and its own place in the reference graph; a model editor that edited it would be one
///         document owning another asset's undo stack and dirty flag, and closing the model would
///         have to decide what happens to unsaved shapes. The plan's point — that this is not a
///         separate <em>tool</em> — is kept by the panel needing a rig to be useful at all, and by
///         everything it draws going through the same gizmos a viewport already has.
///     </para>
///     <para>
///         ⚠ <b>The rig is supplied, not loaded.</b> Posing a shape needs the skeleton it hangs off,
///         and this document cannot reach a model asset. Without one the shapes are still editable —
///         names, kinds, sizes, tags — and the three checks that need a pose simply say they need a
///         rig, which is honest and is what a test gets.
///     </para>
/// </remarks>
public sealed class ProxyShapeDocument : EditorDocument {
    /// <summary>What an authored shape set is written as.</summary>
    public const string Extension = ProxyShapeSetContent.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The set.</summary>
    public ProxyShapeSetContent Set { get; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>The rig the shapes hang off, or <see langword="null" /> if the host has none.</summary>
    public Skeleton? Rig { get; set; }

    /// <summary>The vocabulary the set names, or <see langword="null" /> if the host has none.</summary>
    public ShapeVocabulary? Vocabulary { get; set; }

    /// <summary>Clips to play while looking for shapes that never move, or empty.</summary>
    public IReadOnlyList<AnimationClip> Motion { get; set; } = [];

    /// <summary>Raised after anything changes the set.</summary>
    public event Action<ProxyShapeDocument>? Changed;

    /// <summary>Opens a shape set.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public ProxyShapeDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        try {
            var text = AssetFile.Read(path);

            Set = text.Trim().Length == 0 ? new() : YamlSerializer.Parse<ProxyShapeSetContent>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            Set = new();
            LoadError = exception.Message;
        }

        if (Set.Name.Length == 0) {
            Set.Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>Adds a shape, undoably.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>The shape, so a caller can select it.</returns>
    public ProxyShapeRecord Add(ProxyShapeRecord shape) {
        ArgumentNullException.ThrowIfNull(shape);

        Run("Add Proxy Shape", () => Set.Shapes = [.. Set.Shapes, shape], () => Set.Shapes = [.. Set.Shapes.Where(entry => !ReferenceEquals(entry, shape))]);

        return shape;
    }

    /// <summary>Removes a shape, undoably.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(ProxyShapeRecord shape) {
        ArgumentNullException.ThrowIfNull(shape);

        var index = Array.IndexOf(Set.Shapes, shape);

        if (index < 0) {
            return false;
        }

        var before = Set.Shapes;
        var after = before.Where(entry => !ReferenceEquals(entry, shape)).ToArray();

        Run("Remove Proxy Shape", () => Set.Shapes = after, () => Set.Shapes = before);

        return true;
    }

    /// <summary>Replaces a shape with an edited copy, undoably.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="label">What the undo entry is called.</param>
    /// <param name="edit">What to change.</param>
    /// <returns>The replacement, or the original when it was already that.</returns>
    /// <remarks>
    ///     ⚠ <b>A record, so an edit is a replacement and not a mutation.</b> That is what makes undo
    ///     one assignment instead of a field-by-field restore — and it is why every caller has to take
    ///     the returned instance rather than keeping the one it passed in.
    /// </remarks>
    public ProxyShapeRecord Edit(ProxyShapeRecord shape, string label, Func<ProxyShapeRecord, ProxyShapeRecord> edit) {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(edit);

        var replacement = edit(shape);

        if (EditCommand(shape, label, replacement) is not { } command) {
            return shape;
        }

        Stack.Execute(command);
        return replacement;
    }

    /// <summary>The command that replaces a shape, without running it.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="label">What the undo entry is called.</param>
    /// <param name="replacement">What it becomes.</param>
    /// <returns>The command, or <see langword="null" /> when nothing would change.</returns>
    /// <remarks>
    ///     <b>What a gizmo drag needs and <see cref="Edit" /> cannot give it.</b> A drag is recorded
    ///     by the viewport, which executes and seals in one place so that no target can forget to —
    ///     see <c>IGizmoTarget.Record</c>. So the entry has to be handed back rather than run here,
    ///     and <see cref="Edit" /> becomes the immediate caller of the same factory.
    /// </remarks>
    public IEditorCommand? EditCommand(ProxyShapeRecord shape, string label, ProxyShapeRecord replacement) {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(replacement);

        var index = Array.IndexOf(Set.Shapes, shape);

        if (index < 0 || replacement == shape) {
            return null;
        }

        return Entry(label, () => Set.Shapes[index] = replacement, () => Set.Shapes[index] = shape);
    }

    /// <summary>Adds the mirror image of a shape on the other side of the body.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>The mirrored shape, or <see langword="null" /> when there is nothing to mirror onto.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Across X, and the joint is found by swapping the side in its name.</b> A mirror
    ///         that kept the same joint would put the left palm on the right wrist — the shape would
    ///         be in the right place in the bind pose and in the wrong place the moment either arm
    ///         moved, which is the worst kind of wrong because it looks correct while nothing is
    ///         playing.
    ///     </para>
    ///     <para>
    ///         Refused rather than guessed when the name has no side in it: a shape called
    ///         <c>belly</c> has no other side, and inventing <c>belly_r</c> on the same joint would be
    ///         a duplicate that the audit then reports as an overlap.
    ///     </para>
    /// </remarks>
    public ProxyShapeRecord? Mirror(ProxyShapeRecord shape) {
        ArgumentNullException.ThrowIfNull(shape);

        if (Sided(shape.Name) is not { } name || Sided(shape.Joint) is not { } joint) {
            return null;
        }

        if (Set.Shapes.Any(entry => string.Equals(entry.Name, name, StringComparison.Ordinal))) {
            return null;
        }

        if (Rig is { } rig && rig.IndexOf(joint) < 0) {
            return null;
        }

        return Add(
            shape with {
                Name = name,
                Joint = joint,
                Position = new(-shape.Position.X, shape.Position.Y, shape.Position.Z),
                Rotation = new(shape.Rotation.X, -shape.Rotation.Y, -shape.Rotation.Z, shape.Rotation.W),
                Tags = [.. shape.Tags]
            }
        );
    }

    /// <summary>The same name with its side swapped, or <see langword="null" /> if it has none.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The other side's name.</returns>
    public static string? Sided(string name) {
        ArgumentNullException.ThrowIfNull(name);

        foreach (var (left, right) in (ReadOnlySpan<(string, string)>) [("_l", "_r"), ("-l", "-r"), ("left", "right"), ("Left", "Right"), ("_L", "_R")]) {
            if (name.EndsWith(left, StringComparison.Ordinal)) {
                return name[..^left.Length] + right;
            }

            if (name.EndsWith(right, StringComparison.Ordinal)) {
                return name[..^right.Length] + left;
            }

            if (name.Contains(left, StringComparison.Ordinal)) {
                return name.Replace(left, right, StringComparison.Ordinal);
            }

            if (name.Contains(right, StringComparison.Ordinal)) {
                return name.Replace(right, left, StringComparison.Ordinal);
            }
        }

        return null;
    }

    /// <summary>Marks the coarse set by generating one and taking its choices, undoably.</summary>
    /// <returns>How many shapes are in the coarse set afterwards.</returns>
    /// <remarks>
    ///     ⚠ <b>It sets the <c>coarse</c> flag rather than writing a second set.</b> D13's coarse set
    ///     is a subset chosen per region, and keeping it as a flag on the one list is what makes a
    ///     per-shape override an edit somebody can make and see — a generated second file would be
    ///     regenerated over the top of their override the next time anybody pressed this.
    /// </remarks>
    public int GenerateCoarse() {
        if (Rig is not { } rig || Set.Bake(rig) is not { } baked) {
            return Set.Shapes.Count(static shape => shape.Coarse);
        }

        var coarse = ProxyShapes.Coarsen(baked, rig, Symbol.Intern("region"));
        var chosen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var shape in coarse.Shapes) {
            chosen.Add(shape.Name.ToString());
        }

        var before = Set.Shapes;
        var after = before.Select(shape => shape with { Coarse = chosen.Contains(shape.Name) }).ToArray();

        Run("Generate Coarse Set", () => Set.Shapes = after, () => Set.Shapes = before);

        return chosen.Count;
    }

    /// <summary>Everything worth telling somebody about this set before they ship it.</summary>
    /// <param name="other">Another body's set to compare names against, or <see langword="null" />.</param>
    /// <returns>What it found.</returns>
    /// <remarks>
    ///     The three checks of D13, run together: shapes that never move, shapes deeply inside each
    ///     other, and — the one nobody can see by reading either file — a name in one body's set and
    ///     missing from another's.
    /// </remarks>
    public IReadOnlyList<ShapeValidation> Audit(ProxyShapeSetContent? other = null) {
        List<ShapeValidation> found = [];

        if (Rig is not { } rig) {
            found.Add(new(Symbol.None, "No rig is bound, so nothing that needs a pose could be checked."));
            return found;
        }

        if (Set.Bake(rig) is not { } baked) {
            found.Add(new(Symbol.None, "The set does not bake against this rig."));
            return found;
        }

        if (Vocabulary is { } vocabulary) {
            vocabulary.Validate(baked, found, Set.Class.Length > 0 ? Symbol.Intern(Set.Class) : default);
        }

        foreach (var entry in ProxyShapeAudit.Audit(baked, rig, Motion)) {
            found.Add(entry);
        }

        if (other?.Bake(rig) is { } compared) {
            foreach (var entry in ProxyShapeAudit.Compare(baked, compared)) {
                found.Add(entry);
            }
        }

        return found;
    }

    /// <summary>Changes one of the set's own fields, undoably.</summary>
    /// <param name="label">What the undo entry is called.</param>
    /// <param name="read">How to read the field.</param>
    /// <param name="write">How to write it.</param>
    /// <param name="value">What to write.</param>
    /// <remarks>
    ///     ⚠ <b>Named <c>SetField</c> because <see cref="Set" /> is already the set.</b> The three
    ///     fields this reaches — the rig, the vocabulary, the class — are all references to other
    ///     files, so an edit to one changes what the host resolves; <see cref="Changed" /> firing is
    ///     what tells it to look again.
    /// </remarks>
    public void SetField(string label, Func<ProxyShapeSetContent, string> read, Action<ProxyShapeSetContent, string> write, string value) {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(value);

        var previous = read(Set);

        if (string.Equals(previous, value, StringComparison.Ordinal)) {
            return;
        }

        Run("Edit " + label, () => write(Set, value), () => write(Set, previous));
    }

    void Run(string label, Action apply, Action revert) => Stack.Execute(Entry(label, apply, revert));

    IEditorCommand Entry(string label, Action apply, Action revert) =>
        new DelegateCommand(
            label,
            _ => {
                apply();
                Changed?.Invoke(this);
            },
            _ => {
                revert();
                Changed?.Invoke(this);
            }
        );

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, YamlSerializer.ToYaml(Set));
}
