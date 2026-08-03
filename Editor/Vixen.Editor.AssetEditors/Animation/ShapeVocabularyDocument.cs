// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>One member of a class, and the class it belongs to.</summary>
/// <remarks>
///     A member has no identity apart from its class — two classes may both require a
///     <c>right-palm</c> and mean different sizes — so anything that selects or edits one has to
///     carry both.
/// </remarks>
/// <param name="Class">The class.</param>
/// <param name="Member">The member.</param>
public sealed record VocabularyMember(ShapeClassRecord Class, ShapeClassMemberRecord Member);

/// <summary>A project's shape vocabulary, open for editing.</summary>
/// <remarks>
///     <para>
///         <b>The first file anybody makes and the one that had no editor at all.</b> A vocabulary is
///         what turns "somebody called it <c>palm-l</c> on this body and <c>left-palm</c> on that one"
///         from an invisible bug into an import error — and until this existed it could only be
///         written in a text editor outside Vixen, which is a poor first step for the file that every
///         later step is checked against.
///     </para>
///     <para>
///         ⚠ <b>Three lists rather than one.</b> A term is a name a shape may have, a tag is
///         something a shape may afford, and a class is a body plan that requires certain terms.
///         They are edited together because a class member naming an undeclared term is the mistake
///         the file exists to prevent, and it is only visible when both are in front of you.
///     </para>
/// </remarks>
public sealed class ShapeVocabularyDocument : EditorDocument {
    /// <summary>What a vocabulary is written as.</summary>
    public const string Extension = ShapeVocabularyContent.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The vocabulary.</summary>
    public ShapeVocabularyContent Vocabulary { get; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>Raised after anything changes it.</summary>
    public event Action<ShapeVocabularyDocument>? Changed;

    /// <summary>Opens a vocabulary.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public ShapeVocabularyDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        try {
            var text = AssetFile.Read(path);

            Vocabulary = text.Trim().Length == 0 ? new() : YamlSerializer.Parse<ShapeVocabularyContent>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            Vocabulary = new();
            LoadError = exception.Message;
        }

        if (Vocabulary.Name.Length == 0) {
            Vocabulary.Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>What is wrong with it, as the build would say it.</summary>
    /// <returns>The problems, worst first.</returns>
    public IReadOnlyList<VocabularyProblem> Problems() => Vocabulary.Problems();

    // ── Terms ────────────────────────────────────────────────────────────────

    /// <summary>Declares a name a shape may have, undoably.</summary>
    /// <param name="name">The name.</param>
    /// <param name="meaning">What a shape called that is.</param>
    /// <returns>The term.</returns>
    public ShapeTermRecord AddTerm(string name = "new-shape", string meaning = "") {
        var term = new ShapeTermRecord(name, meaning);

        Insert("Declare Shape Name", () => Vocabulary.Shapes, value => Vocabulary.Shapes = value, term);

        return term;
    }

    /// <summary>Removes a term, undoably.</summary>
    /// <param name="term">The term.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(ShapeTermRecord term) =>
        Delete("Remove Shape Name", () => Vocabulary.Shapes, value => Vocabulary.Shapes = value, term);

    /// <summary>Replaces a term with an edited copy, undoably.</summary>
    /// <param name="term">The term.</param>
    /// <param name="edit">What to change.</param>
    /// <returns>The replacement.</returns>
    public ShapeTermRecord Edit(ShapeTermRecord term, Func<ShapeTermRecord, ShapeTermRecord> edit) =>
        Replace("Edit Shape Name", () => Vocabulary.Shapes, value => Vocabulary.Shapes = value, term, edit);

    // ── Tags ─────────────────────────────────────────────────────────────────

    /// <summary>Declares a tag, undoably.</summary>
    /// <param name="tag">The tag, as <c>key=value</c>.</param>
    /// <param name="meaning">What carrying it entitles a constraint to assume.</param>
    /// <returns>The tag.</returns>
    public ShapeTagRecord AddTag(string tag = "affords=grip-surface", string meaning = "") {
        var record = new ShapeTagRecord(tag, meaning);

        Insert("Declare Tag", () => Vocabulary.Tags, value => Vocabulary.Tags = value, record);

        return record;
    }

    /// <summary>Removes a tag, undoably.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(ShapeTagRecord tag) =>
        Delete("Remove Tag", () => Vocabulary.Tags, value => Vocabulary.Tags = value, tag);

    /// <summary>Replaces a tag with an edited copy, undoably.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="edit">What to change.</param>
    /// <returns>The replacement.</returns>
    public ShapeTagRecord Edit(ShapeTagRecord tag, Func<ShapeTagRecord, ShapeTagRecord> edit) =>
        Replace("Edit Tag", () => Vocabulary.Tags, value => Vocabulary.Tags = value, tag, edit);

    // ── Classes ──────────────────────────────────────────────────────────────

    /// <summary>Declares a body plan, undoably.</summary>
    /// <param name="name">What the class is called.</param>
    /// <returns>The class.</returns>
    public ShapeClassRecord AddClass(string name = "humanoid") {
        var declared = new ShapeClassRecord(name, []);

        Insert("Declare Class", () => Vocabulary.Classes, value => Vocabulary.Classes = value, declared);

        return declared;
    }

    /// <summary>Removes a class, undoably.</summary>
    /// <param name="declared">The class.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(ShapeClassRecord declared) =>
        Delete("Remove Class", () => Vocabulary.Classes, value => Vocabulary.Classes = value, declared);

    /// <summary>Replaces a class with an edited copy, undoably.</summary>
    /// <param name="declared">The class.</param>
    /// <param name="edit">What to change.</param>
    /// <returns>The replacement.</returns>
    public ShapeClassRecord Edit(ShapeClassRecord declared, Func<ShapeClassRecord, ShapeClassRecord> edit) =>
        Replace("Edit Class", () => Vocabulary.Classes, value => Vocabulary.Classes = value, declared, edit);

    /// <summary>Adds a shape a class requires, undoably.</summary>
    /// <param name="declared">The class.</param>
    /// <param name="name">Which shape, by name.</param>
    /// <returns>The member, with the class it now belongs to.</returns>
    /// <remarks>
    ///     ⚠ <b>Defaults to the first term this vocabulary declares.</b> A member naming nothing is
    ///     the one mistake this file exists to catch, so the button that makes one does not make that
    ///     mistake on somebody's behalf.
    /// </remarks>
    public VocabularyMember AddMember(ShapeClassRecord declared, string? name = null) {
        ArgumentNullException.ThrowIfNull(declared);

        var member = new ShapeClassMemberRecord(
            name ?? (Vocabulary.Shapes.Length > 0 ? Vocabulary.Shapes[0].Name : "new-shape"),
            ShapeKind.Sphere,
            [],
            new(0.1f),
            Vixen.Core.Mathematics.Vector3.Zero,
            true
        );

        var replaced = Edit(declared, entry => entry with { Members = [.. entry.Members, member] });

        return new(replaced, member);
    }

    /// <summary>Removes a member from its class, undoably.</summary>
    /// <param name="member">The member and its class.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(VocabularyMember member) {
        ArgumentNullException.ThrowIfNull(member);

        return Array.IndexOf(member.Class.Members, member.Member) >= 0
            && Edit(
                member.Class,
                entry => entry with { Members = [.. entry.Members.Where(row => !ReferenceEquals(row, member.Member))] }
            ) is not null;
    }

    /// <summary>Replaces a member with an edited copy, undoably.</summary>
    /// <param name="member">The member and its class.</param>
    /// <param name="edit">What to change.</param>
    /// <returns>The replacement, with the class it now belongs to.</returns>
    public VocabularyMember Edit(VocabularyMember member, Func<ShapeClassMemberRecord, ShapeClassMemberRecord> edit) {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(edit);

        var index = Array.IndexOf(member.Class.Members, member.Member);

        if (index < 0) {
            return member;
        }

        var replacement = edit(member.Member);
        var members = member.Class.Members.ToArray();

        members[index] = replacement;

        return new(Edit(member.Class, entry => entry with { Members = members }), replacement);
    }

    // ── The three shapes every list operation takes ──────────────────────────

    void Insert<T>(string label, Func<T[]> read, Action<T[]> write, T added) {
        var before = read();

        Run(label, () => write([.. before, added]), () => write(before));
    }

    bool Delete<T>(string label, Func<T[]> read, Action<T[]> write, T removed) {
        var before = read();

        if (Array.IndexOf(before, removed) < 0) {
            return false;
        }

        var after = before.Where(entry => !ReferenceEquals(entry, removed)).ToArray();

        Run(label, () => write(after), () => write(before));

        return true;
    }

    /// <summary>
    ///     ⚠ <b>Records are replaced rather than mutated</b>, so an undo is one array assignment
    ///     instead of a field-by-field restore — which is why every caller has to take the returned
    ///     instance rather than keeping the one it passed in.
    /// </summary>
    T Replace<T>(string label, Func<T[]> read, Action<T[]> write, T target, Func<T, T> edit) where T : class {
        var before = read();
        var index = Array.IndexOf(before, target);
        var replacement = edit(target);

        if (index < 0 || replacement == target) {
            return target;
        }

        var after = before.ToArray();
        after[index] = replacement;

        Run(label, () => write(after), () => write(before));

        return replacement;
    }

    void Run(string label, Action apply, Action revert) {
        Stack.Execute(
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
            )
        );
    }

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, YamlSerializer.ToYaml(Vocabulary));
}
