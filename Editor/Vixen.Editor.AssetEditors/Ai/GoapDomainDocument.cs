// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>A GOAP domain, open for editing: three tables, and a graph derived from them.</summary>
/// <remarks>
///     ⚠ <b>The tables are edited and the graph is not</b> — doc 37 § Part 5. The edges of a GOAP
///     graph are computed from which effects satisfy which conditions, so drawing them by hand would
///     be authoring the same fact twice and the two copies would disagree the first time somebody
///     changed a condition.
/// </remarks>
public sealed class GoapDomainDocument : EditorDocument {
    /// <summary>What a GOAP domain is written as.</summary>
    public const string Extension = GoapDomainContent.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The domain as a file holds it.</summary>
    public GoapDomainContent Content { get; private set; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>What the last <see cref="Compile" /> had to say.</summary>
    public IReadOnlyList<BehaviorTreeDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>What the last <see cref="Compile" /> produced, or <see langword="null" />.</summary>
    public GoapDomain? Domain { get; private set; }

    /// <summary>Raised after anything changes.</summary>
    public event Action<GoapDomainDocument>? Changed;

    /// <summary>Opens a domain.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public GoapDomainDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        var text = AssetFile.Read(path);
        var name = Path.GetFileNameWithoutExtension(path);

        if (text.Trim().Length == 0) {
            Content = New(name);

            return;
        }

        try {
            Content = YamlSerializer.Parse<GoapDomainContent>(text);

            if (Content.Version > GoapDomainContent.Current) {
                throw new NotSupportedException(
                    $"This domain is version {Content.Version} and this build reads {GoapDomainContent.Current}."
                );
            }
        } catch (Exception exception) when (exception is YamlBindingException
            or YamlParseException or NotSupportedException) {
            Content = New(name);
            LoadError = exception.Message;
        }

        if (Content.Name.Length == 0) {
            Content.Name = name;
        }
    }

    /// <summary>The domain as it would be written.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(Content);

    /// <summary>Compiles the domain, which is what builds the graph the viewer draws.</summary>
    /// <param name="resolver">Where tasks and world sources are looked up, or null for a fresh one.</param>
    /// <returns>The domain, or null.</returns>
    public GoapDomain? Compile(BehaviorTreeResolver? resolver = null) {
        GoapDomainContentCompiler.TryCompile(
            Content,
            resolver ?? new BehaviorTreeResolver(),
            out var problems,
            out var domain
        );

        Diagnostics = problems;
        Domain = domain;

        return domain;
    }

    /// <summary>Runs a gesture as one undo entry.</summary>
    /// <param name="name">What it is called in the history.</param>
    /// <param name="gesture">What it does to the tables.</param>
    /// <param name="mergeKey">A key that makes two consecutive entries one, or null for its own entry.</param>
    public void Edit(string name, Action<GoapDomainContent> gesture, string? mergeKey = null) {
        ArgumentNullException.ThrowIfNull(gesture);

        var before = Copy(Content);

        gesture(Content);

        var after = Copy(Content);

        Stack.Execute(new SnapshotCommand(name, this, before, after, mergeKey));
        Changed?.Invoke(this);
    }

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());

    /// <summary>A deep copy of a domain, for an undo entry.</summary>
    /// <param name="content">The domain.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> is null.</exception>
    public static GoapDomainContent Copy(GoapDomainContent content) {
        ArgumentNullException.ThrowIfNull(content);

        return new() {
            Version = content.Version,
            Name = content.Name,
            NodeBudget = content.NodeBudget,
            DepthLimit = content.DepthLimit,
            Blackboard = [.. content.Blackboard.Select(key => new BehaviorKeyContent { Name = key.Name, Type = key.Type })],
            Keys = [
                .. content.Keys.Select(
                    key => new GoapKeyContent {
                        Name = key.Name,
                        Source = key.Source,
                        From = key.From,
                        Value = key.Value
                    }
                )
            ],
            Actions = [.. content.Actions.Select(Copy)],
            Goals = [
                .. content.Goals.Select(
                    goal => new GoapGoalContent {
                        Name = goal.Name,
                        Priority = goal.Priority,
                        Conditions = [.. goal.Conditions.Select(Copy)]
                    }
                )
            ]
        };
    }

    static GoapActionContent Copy(GoapActionContent action) => new() {
        Name = action.Name,
        Task = action.Task,
        Fields = new(action.Fields, StringComparer.Ordinal),
        Cost = action.Cost,
        Target = action.Target,
        StoppingDistance = action.StoppingDistance,
        Move = action.Move,
        Conditions = [.. action.Conditions.Select(Copy)],
        Effects = [
            .. action.Effects.Select(effect => new GoapEffectContent { Key = effect.Key, Increases = effect.Increases })
        ]
    };

    static GoapConditionContent Copy(GoapConditionContent condition) => new() {
        Key = condition.Key,
        Comparison = condition.Comparison,
        Value = condition.Value
    };

    /// <summary>
    ///     A new domain: one world key, one action and one goal it serves. ⚠ A file whose first
    ///     diagnostic is about its own emptiness is one nobody trusts.
    /// </summary>
    static GoapDomainContent New(string name) {
        var content = new GoapDomainContent { Name = name };

        content.Blackboard.Add(new() { Name = "progress", Type = BlackboardValueType.Int });
        content.Keys.Add(new() { Name = "progress", Source = GoapSourceKind.Blackboard, From = "progress" });
        content.Actions.Add(
            new() {
                Name = "Work",
                Task = "Wait",
                Fields = { ["Seconds"] = "1" },
                Effects = { new() { Key = "progress", Increases = true } }
            }
        );

        content.Goals.Add(
            new() {
                Name = "Finished",
                Conditions = { new() { Key = "progress", Comparison = GoapComparison.Greater, Value = 0 } }
            }
        );

        return content;
    }

    /// <summary>One gesture, as the two documents either side of it.</summary>
    /// <remarks>
    ///     ⚠ <b>The first <c>Do</c> installs nothing</b>, for the reason
    ///     <see cref="BehaviorTreeDocument" />'s says at length.
    /// </remarks>
    sealed class SnapshotCommand(
        string name,
        GoapDomainDocument document,
        GoapDomainContent before,
        GoapDomainContent after,
        string? mergeKey
    ) : IEditorCommand {
        bool applied;

        /// <inheritdoc />
        public string Name => name;

        /// <summary>What two entries have to agree on before they become one.</summary>
        public string? MergeKey => mergeKey;

        /// <summary>The picture this entry started from, which a merge below it takes over.</summary>
        public GoapDomainContent Before => before;

        /// <inheritdoc />
        public void Do(EditorContext context) {
            if (!applied) {
                applied = true;

                return;
            }

            document.Content = Copy(after);
            document.Changed?.Invoke(document);
        }

        /// <inheritdoc />
        public void Undo(EditorContext context) {
            document.Content = Copy(before);
            document.Changed?.Invoke(document);
        }

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
