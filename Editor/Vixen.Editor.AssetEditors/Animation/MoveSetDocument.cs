// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>A movement vocabulary, open for editing: a table of moves and a list of rules.</summary>
/// <remarks>
///     <para>
///         <b>A table, because a move set is a table.</b> Everything a person does to one — adding a
///         row, changing a facet, retiming an entry, reordering a rule — is one entry on the undo
///         stack, the same shape <c>AnimationClipDocument</c> takes.
///     </para>
///     <para>
///         ⚠ <b>Facets are edited as text and parsed here, not as a pair of drop-downs.</b> The
///         vocabulary is a project's own and grows while somebody is working; a control that could
///         only offer the values already in use would make adding the first <c>style=limp</c>
///         impossible from inside the editor. The one key that is not a project's business —
///         <c>role</c> — is checked at import, where the whole set can be seen at once.
///     </para>
/// </remarks>
public sealed class MoveSetDocument : EditorDocument {
    /// <summary>What an authored move set is written as.</summary>
    public const string Extension = MoveSetContent.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The set.</summary>
    public MoveSetContent Set { get; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>Raised after anything changes the set.</summary>
    public event Action<MoveSetDocument>? Changed;

    /// <summary>Opens a move set.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public MoveSetDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        try {
            var text = AssetFile.Read(path);

            Set = text.Trim().Length == 0 ? new() : YamlSerializer.Parse<MoveSetContent>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            Set = new();
            LoadError = exception.Message;
        }

        if (Set.Name.Length == 0) {
            Set.Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>Adds a row, undoably.</summary>
    /// <param name="entry">The row.</param>
    /// <returns>The row, so a caller can select it.</returns>
    public MoveEntryRecord Add(MoveEntryRecord entry) {
        ArgumentNullException.ThrowIfNull(entry);

        Run("Add Move", () => Set.Entries.Add(entry), () => Set.Entries.Remove(entry));

        return entry;
    }

    /// <summary>Removes a row, undoably.</summary>
    /// <param name="entry">The row.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(MoveEntryRecord entry) {
        ArgumentNullException.ThrowIfNull(entry);

        var index = Set.Entries.IndexOf(entry);

        if (index < 0) {
            return false;
        }

        Run("Remove Move", () => Set.Entries.RemoveAt(index), () => Set.Entries.Insert(index, entry));

        return true;
    }

    /// <summary>Changes one of a row's numbers or its name, undoably.</summary>
    /// <typeparam name="T">What kind of value.</typeparam>
    /// <param name="entry">The row.</param>
    /// <param name="label">What the undo entry is called.</param>
    /// <param name="read">How to read the field.</param>
    /// <param name="write">How to write it.</param>
    /// <param name="value">What to write.</param>
    public void SetField<T>(
        MoveEntryRecord entry,
        string label,
        Func<MoveEntryRecord, T> read,
        Action<MoveEntryRecord, T> write,
        T value
    ) {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        var previous = read(entry);

        if (EqualityComparer<T>.Default.Equals(previous, value)) {
            return;
        }

        Run(label, () => write(entry, value), () => write(entry, previous));
    }

    /// <summary>Replaces a row's facets from a written list, undoably.</summary>
    /// <param name="entry">The row.</param>
    /// <param name="text">The facets, as <c>key=value</c> separated by spaces or commas.</param>
    /// <returns>Whether the text parsed.</returns>
    /// <remarks>
    ///     ⚠ <b>All or nothing.</b> A line that half-parses would leave a row carrying some of what
    ///     somebody typed and none of the rest, with no indication which — so a malformed pair is
    ///     refused and the field snaps back to what the row still says.
    /// </remarks>
    public bool SetFacets(MoveEntryRecord entry, string text) {
        ArgumentNullException.ThrowIfNull(entry);

        if (Parse(text) is not { } parsed) {
            return false;
        }

        var previous = entry.Facets;

        Run("Edit Facets", () => entry.Facets = parsed, () => entry.Facets = previous);

        return true;
    }

    /// <summary>Reads a written facet list, or <see langword="null" /> if any pair is malformed.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The facets.</returns>
    public static List<FacetRecord>? Parse(string? text) {
        List<FacetRecord> parsed = [];

        foreach (var word in (text ?? string.Empty).Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries)) {
            var split = word.IndexOf('=', StringComparison.Ordinal);

            if (split <= 0 || split == word.Length - 1) {
                return null;
            }

            parsed.Add(new() { Key = word[..split], Value = word[(split + 1)..] });
        }

        return parsed;
    }

    /// <summary>A row's facets, written the way <see cref="SetFacets" /> reads them.</summary>
    /// <param name="entry">The row.</param>
    /// <returns>The text.</returns>
    public static string Describe(MoveEntryRecord entry) {
        ArgumentNullException.ThrowIfNull(entry);

        return string.Join(' ', entry.Facets.Select(static facet => $"{facet.Key}={facet.Value}"));
    }

    /// <summary>Adds a transition rule at the end, undoably.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The rule.</returns>
    public TransitionRuleRecord AddRule(TransitionRuleRecord rule) {
        ArgumentNullException.ThrowIfNull(rule);

        Run("Add Transition Rule", () => Set.Rules.Add(rule), () => Set.Rules.Remove(rule));

        return rule;
    }

    /// <summary>Removes a transition rule, undoably.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveRule(TransitionRuleRecord rule) {
        ArgumentNullException.ThrowIfNull(rule);

        var index = Set.Rules.IndexOf(rule);

        if (index < 0) {
            return false;
        }

        Run("Remove Transition Rule", () => Set.Rules.RemoveAt(index), () => Set.Rules.Insert(index, rule));

        return true;
    }

    /// <summary>Moves a rule up or down the list, undoably.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="by">How far, signed.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>
    ///     ⚠ <b>Order is the whole of what a rule list means.</b> First match wins, so moving a rule
    ///     one place is a semantic edit and not a tidy-up — which is why it is an undoable command
    ///     rather than a drag the view performs on the list.
    /// </remarks>
    public bool MoveRule(TransitionRuleRecord rule, int by) {
        ArgumentNullException.ThrowIfNull(rule);

        var from = Set.Rules.IndexOf(rule);
        var to = from + by;

        if (from < 0 || to < 0 || to >= Set.Rules.Count || by == 0) {
            return false;
        }

        Run(
            "Reorder Transition Rule",
            () => {
                Set.Rules.RemoveAt(from);
                Set.Rules.Insert(to, rule);
            },
            () => {
                Set.Rules.RemoveAt(to);
                Set.Rules.Insert(from, rule);
            }
        );

        return true;
    }

    /// <summary>How a name in <see cref="MoveSetContent.Bases" /> is turned into a set.</summary>
    /// <remarks>
    ///     ⚠ <b>Supplied by the host, because this document has no way to reach another asset.</b> An
    ///     overlay is what makes an injured set three clips rather than a hundred, and a table that
    ///     could not show the rows underneath would make an author guess which of their edits was
    ///     having any effect. Left unset, the base rows are simply not shown and the panel says so —
    ///     which is honest, and is what a test gets.
    /// </remarks>
    public Func<string, MoveSetContent?>? Resolve { get; set; }

    /// <summary>The base sets this one overlays, in order, as far as they could be resolved.</summary>
    /// <returns>Each base's address and its content.</returns>
    public IReadOnlyList<(string Address, MoveSetContent Content)> Underlay() {
        List<(string, MoveSetContent)> found = [];

        if (Resolve is not { } resolve) {
            return found;
        }

        foreach (var address in Set.Bases) {
            if (resolve(address) is { } content) {
                found.Add((address, content));
            }
        }

        return found;
    }

    /// <summary>The set as the selector sees it, with nothing loaded.</summary>
    /// <returns>The set.</returns>
    /// <remarks>
    ///     Rebuilt per call rather than cached: a table being edited invalidates it on every
    ///     keystroke, and a set of a few hundred rows costs a sort. A cache here would be a cache to
    ///     get wrong.
    /// </remarks>
    public MoveSet Preview() => Set.Preview([.. Underlay().Select(static entry => entry.Content.Preview())]);

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
    protected override void SaveCore() => AssetFile.Write(AssetPath, YamlSerializer.ToYaml(Set));
}
