// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Ai;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>A behaviour tree, open for editing.</summary>
/// <remarks>
///     <para>
///         The model and the compiler are <c>Vixen.Editor.Ai</c>'s and <c>Vixen.Ai</c>'s — neither
///         knows about a project, a document or a panel — and this is the YAML end of it and the undo
///         stack over it.
///     </para>
///     <para>
///         ⚠ <b>Every gesture is one snapshot in, one snapshot out.</b> The node graphs use
///         fine-grained inverses because a shader graph is thousands of nodes; a behaviour tree is
///         tens, and a snapshot is a few kilobytes of strings. What that buys is that a reparent, a
///         reorder and a key rename that rewrote forty references are all undoable by construction,
///         with no chance of an inverse that puts back four of the five things it changed. The
///         entries still merge, so dragging a number is one step rather than forty.
///     </para>
///     <para>
///         ⚠ <b>Compiled when it is asked for, not on every edit.</b> Adding a composite produces a
///         tree with a composite that has no children; adding a decorator produces one whose key is
///         briefly empty. Compiling on every change would fill the panel with complaints about a node
///         somebody is halfway through making — the rule <c>CompositorDocument</c> and
///         <c>AnimationGraphDocument</c> both state.
///     </para>
/// </remarks>
public sealed class BehaviorTreeDocument : EditorDocument {
    /// <summary>What a behaviour tree is written as.</summary>
    public const string Extension = BehaviorTreeContent.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The tree, and every operation over it.</summary>
    public BehaviorTreeModel Model { get; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>What the last <see cref="Compile" /> had to say.</summary>
    public IReadOnlyList<BehaviorTreeDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>What the last <see cref="Compile" /> produced, or <see langword="null" />.</summary>
    public BehaviorTreeTemplate? Template { get; private set; }

    /// <summary>Raised after anything changes the tree.</summary>
    public event Action<BehaviorTreeDocument>? Changed;

    /// <summary>Opens a tree.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public BehaviorTreeDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        var text = AssetFile.Read(path);
        var name = Path.GetFileNameWithoutExtension(path);

        if (text.Trim().Length == 0) {
            // ⚠ A new tree is a selector with one wait rather than nothing, because the compiler's
            // first complaint about an empty one is "the tree has no root" and the second is "a
            // composite with no children can never do anything" — a file that opened complaining
            // about itself would be one nobody trusts.
            Model = new(New(name));

            Model.Changed += _ => Changed?.Invoke(this);

            return;
        }

        BehaviorTreeContent content;

        try {
            content = YamlSerializer.Parse<BehaviorTreeContent>(text);

            if (content.Version > BehaviorTreeContent.Current) {
                throw new NotSupportedException(
                    $"This tree is version {content.Version} and this build reads {BehaviorTreeContent.Current}."
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

    /// <summary>The tree as it would be written.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(Model.Content);

    /// <summary>Compiles the tree, against whatever a caller can resolve.</summary>
    /// <param name="resolver">
    ///     Where sensors and subtrees are looked up, or null for a fresh one that resolves neither.
    /// </param>
    /// <returns>The template, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>A tree whose sensors are not registered still compiles and is still checked.</b> The
    ///     editor has no way to construct a game's sensor from a file, and refusing would make every
    ///     tree in a project unopenable until its code existed — so each unresolved name is a
    ///     diagnostic and the topology, the key references and the parallel's shape are all checked
    ///     anyway. That is the half of a tree an author can actually get wrong.
    /// </remarks>
    public BehaviorTreeTemplate? Compile(BehaviorTreeResolver? resolver = null) {
        var problems = new List<BehaviorTreeDiagnostic>();
        var lookup = resolver ?? new BehaviorTreeResolver();
        var layout = Model.Content.BuildLayout(problems);
        var asset = BehaviorTreeContentCompiler.Build(Model.Content, lookup, layout, problems);

        if (!BehaviorTreeCompiler.TryCompile(asset, lookup.Actions, layout, out var compiled, out var template)) {
            problems.AddRange(compiled);
        }

        Diagnostics = problems;
        Template = template;

        return template;
    }

    /// <summary>Runs a gesture as one undo entry.</summary>
    /// <param name="name">What it is called in the history.</param>
    /// <param name="gesture">What it does to the model.</param>
    /// <param name="mergeKey">
    ///     A key that makes two consecutive entries one, or null for an entry of its own. What stops
    ///     a dragged number from being forty steps.
    /// </param>
    public void Edit(string name, Action<BehaviorTreeModel> gesture, string? mergeKey = null) {
        ArgumentNullException.ThrowIfNull(gesture);

        var before = Model.Snapshot();

        gesture(Model);

        var after = Model.Snapshot();

        Stack.Execute(new SnapshotCommand(name, this, before, after, mergeKey));
    }

    /// <summary>Lays the tree out top-down, as one undo entry.</summary>
    public void Layout() => Edit("Lay Out Tree", model => BehaviorTreeLayout.Apply(model));

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());

    static BehaviorTreeContent New(string name) {
        var content = new BehaviorTreeContent { Name = name };
        var schema = BehaviorNodeSchema.Default;

        schema.TryGet("Selector", out var selector);
        schema.TryGet("Wait", out var wait);

        content.Root = BehaviorTreeModel.Make(selector!, "Root");
        content.Root.Children.Add(BehaviorTreeModel.Make(wait!));

        return content;
    }

    /// <summary>One gesture, as the two documents either side of it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The snapshots are already taken when this is constructed, and <c>Do</c> only
    ///         installs one.</b> A command's <c>Do</c> runs again on every redo, so a command that
    ///         re-applied a gesture would have to be able to re-run a reparent whose target had since
    ///         been deleted by an undo. Two pictures cannot be wrong about that.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <i>first</i> <c>Do</c> installs nothing, and that is not an optimisation.</b>
    ///         The gesture has already run against the live tree, so installing a copy of it would
    ///         change nothing about the document's value and everything about its <i>identity</i> —
    ///         every <see cref="BehaviorNodeContent" /> a caller was holding would point into an
    ///         orphaned tree, silently, and the next edit through it would go nowhere. That is a trap
    ///         with no symptom, and it cost this file a hung test to find. Undo and redo still swap
    ///         whole trees, which is exactly when a view re-resolves its selection anyway.
    ///     </para>
    ///     <para>
    ///         And each swap installs a <i>copy</i>, so the two snapshots this entry holds stay
    ///         pristine however many times the stack is walked over them.
    ///     </para>
    /// </remarks>
    sealed class SnapshotCommand(
        string name,
        BehaviorTreeDocument document,
        BehaviorTreeContent before,
        BehaviorTreeContent after,
        string? mergeKey
    ) : IEditorCommand {
        bool applied;

        /// <inheritdoc />
        public string Name => name;

        /// <summary>What two entries have to agree on before they become one.</summary>
        public string? MergeKey => mergeKey;

        /// <summary>The picture this entry started from, which a merge below it takes over.</summary>
        public BehaviorTreeContent Before => before;

        /// <inheritdoc />
        public void Do(EditorContext context) {
            if (!applied) {
                applied = true;

                return;
            }

            document.Model.Replace(BehaviorTreeModel.Copy(after));
        }

        /// <inheritdoc />
        public void Undo(EditorContext context) => document.Model.Replace(BehaviorTreeModel.Copy(before));

        /// <inheritdoc />
        public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
            merged = null;

            return previous is SnapshotCommand earlier
                && mergeKey is not null
                && string.Equals(earlier.MergeKey, mergeKey, StringComparison.Ordinal)
                && Replace(earlier, out merged);
        }

        bool Replace(SnapshotCommand earlier, out IEditorCommand? merged) {
            // The merged entry undoes to where the *first* of the two started, which is what makes a
            // dragged number one step back to where it was rather than one step back to halfway. It
            // is born already applied, because the state it describes is the state on screen.
            merged = new SnapshotCommand(name, document, earlier.Before, after, mergeKey) { applied = true };

            return true;
        }
    }
}
