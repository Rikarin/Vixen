// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Ai;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>A utility set, open for editing.</summary>
/// <remarks>
///     The same arrangement <see cref="BehaviorTreeDocument" /> has — the model is
///     <c>Vixen.Editor.Ai</c>'s, the compiler is <c>Vixen.Ai</c>'s, and this is the YAML end and the
///     undo stack over it. Every gesture is one snapshot in, one snapshot out, and the entries merge
///     so that dragging a curve parameter is one step rather than forty.
/// </remarks>
public sealed class UtilitySetDocument : EditorDocument {
    /// <summary>What a utility set is written as.</summary>
    public const string Extension = UtilitySetContent.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The set, and every operation over it.</summary>
    public UtilitySetModel Model { get; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>What the last <see cref="Compile" /> had to say.</summary>
    public IReadOnlyList<BehaviorTreeDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>What the last <see cref="Compile" /> produced, or <see langword="null" />.</summary>
    public UtilitySet? Set { get; private set; }

    /// <summary>Raised after anything changes the set.</summary>
    public event Action<UtilitySetDocument>? Changed;

    /// <summary>Opens a set.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public UtilitySetDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        var text = AssetFile.Read(path);
        var name = Path.GetFileNameWithoutExtension(path);

        if (text.Trim().Length == 0) {
            // ⚠ A new set is one action with one axis rather than nothing. An empty one compiles to an
            // agent with nothing to do, and a file whose first diagnostic is about its own emptiness
            // is a file nobody trusts.
            Model = new(New(name));
            Model.Changed += _ => Changed?.Invoke(this);

            return;
        }

        UtilitySetContent content;

        try {
            content = YamlSerializer.Parse<UtilitySetContent>(text);

            if (content.Version > UtilitySetContent.Current) {
                throw new NotSupportedException(
                    $"This set is version {content.Version} and this build reads {UtilitySetContent.Current}."
                );
            }
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            content = New(name);
            LoadError = exception.Message;
        }

        if (content.Name.Length == 0) {
            content.Name = name;
        }

        Model = new(content);
        Model.Changed += _ => Changed?.Invoke(this);
    }

    /// <summary>The set as it would be written.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(Model.Content);

    /// <summary>Compiles the set, against whatever a caller can resolve.</summary>
    /// <param name="resolver">Where tasks and inputs are looked up, or null for a fresh one.</param>
    /// <returns>The set, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>A set whose inputs are not registered still compiles and is still checked.</b> The
    ///     editor cannot construct a game's input from a file, and refusing would make every set in a
    ///     project unopenable until its code existed — so each unresolved name is a diagnostic and the
    ///     key references, which are the half an author can actually get wrong, are all checked.
    /// </remarks>
    public UtilitySet? Compile(BehaviorTreeResolver? resolver = null) {
        UtilitySetContentCompiler.TryCompile(
            Model.Content,
            resolver ?? new BehaviorTreeResolver(),
            out var problems,
            out var set
        );

        Diagnostics = problems;
        Set = set;

        return set;
    }

    /// <summary>Runs a gesture as one undo entry.</summary>
    /// <param name="name">What it is called in the history.</param>
    /// <param name="gesture">What it does to the model.</param>
    /// <param name="mergeKey">A key that makes two consecutive entries one, or null for its own entry.</param>
    public void Edit(string name, Action<UtilitySetModel> gesture, string? mergeKey = null) {
        ArgumentNullException.ThrowIfNull(gesture);

        var before = Model.Snapshot();

        gesture(Model);

        var after = Model.Snapshot();

        Stack.Execute(new SnapshotCommand(name, this, before, after, mergeKey));
    }

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());

    static UtilitySetContent New(string name) {
        var content = new UtilitySetContent { Name = name };
        var action = new UtilityActionContent { Name = "Idle", Task = "Wait", Fields = { ["Seconds"] = "1" } };

        action.Considerations.Add(new() { Name = "always", Input = UtilityInputKind.Blackboard });
        content.Actions.Add(action);

        return content;
    }

    /// <summary>One gesture, as the two documents either side of it.</summary>
    /// <remarks>
    ///     ⚠ <b>The first <c>Do</c> installs nothing</b>, for the reason
    ///     <see cref="BehaviorTreeDocument" />'s says at length: the gesture has already run against
    ///     the live set, so installing a copy would change nothing about the document's value and
    ///     everything about its <i>identity</i> — every action a caller was holding would point into
    ///     an orphaned set.
    /// </remarks>
    sealed class SnapshotCommand(
        string name,
        UtilitySetDocument document,
        UtilitySetContent before,
        UtilitySetContent after,
        string? mergeKey
    ) : IEditorCommand {
        bool applied;

        /// <inheritdoc />
        public string Name => name;

        /// <summary>What two entries have to agree on before they become one.</summary>
        public string? MergeKey => mergeKey;

        /// <summary>The picture this entry started from, which a merge below it takes over.</summary>
        public UtilitySetContent Before => before;

        /// <inheritdoc />
        public void Do(EditorContext context) {
            if (!applied) {
                applied = true;

                return;
            }

            document.Model.Replace(UtilitySetModel.Copy(after));
        }

        /// <inheritdoc />
        public void Undo(EditorContext context) => document.Model.Replace(UtilitySetModel.Copy(before));

        /// <inheritdoc />
        public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
            merged = null;

            if (previous is not SnapshotCommand earlier
                || mergeKey is null
                || !string.Equals(earlier.MergeKey, mergeKey, StringComparison.Ordinal)) {
                return false;
            }

            merged = new SnapshotCommand(name, document, earlier.Before, after, mergeKey) { applied = true };

            return true;
        }
    }
}
