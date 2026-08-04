// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>An environment query, open for editing: two lists, in order.</summary>
/// <remarks>
///     ⚠ <b>A list and not a graph</b> — doc 37 § D14. Unreal draws EQS on a graph canvas, and what is
///     on that canvas is a root with a fixed list of children and no wiring decisions anywhere. So the
///     document is what the thing is: generators, then tests, in the order they run.
/// </remarks>
public sealed class QueryDocument : EditorDocument {
    /// <summary>What an environment query is written as.</summary>
    public const string Extension = QueryContent.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The query as a file holds it.</summary>
    public QueryContent Content { get; private set; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>What the last <see cref="Compile" /> had to say.</summary>
    public IReadOnlyList<BehaviorTreeDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>What the last <see cref="Compile" /> produced, or <see langword="null" />.</summary>
    public EnvironmentQuery? Query { get; private set; }

    /// <summary>The points the last <see cref="Preview" /> generated and scored.</summary>
    public QueryResults Results { get; } = new() { Detailed = true };

    /// <summary>Raised after anything changes.</summary>
    public event Action<QueryDocument>? Changed;

    /// <summary>Opens a query.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public QueryDocument(EditorProject project, AssetId asset, string path)
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
            Content = YamlSerializer.Parse<QueryContent>(text);

            if (Content.Version > QueryContent.Current) {
                throw new NotSupportedException(
                    $"This query is version {Content.Version} and this build reads {QueryContent.Current}."
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

    /// <summary>The query as it would be written.</summary>
    /// <returns>The YAML.</returns>
    public string ToYaml() => YamlSerializer.ToYaml(Content);

    /// <summary>Compiles the query.</summary>
    /// <param name="resolver">Where registered generators and tests are looked up, or null for a fresh one.</param>
    /// <returns>The query, or null.</returns>
    public EnvironmentQuery? Compile(BehaviorTreeResolver? resolver = null) {
        QueryContentCompiler.TryCompile(
            Content,
            resolver ?? new BehaviorTreeResolver(),
            out var problems,
            out var query
        );

        Diagnostics = problems;
        Query = query;

        return query;
    }

    /// <summary>
    ///     Runs the query from a point and keeps every scored candidate, which is what the preview
    ///     draws.
    /// </summary>
    /// <param name="world">A world to run against — the edited scene, or an empty one.</param>
    /// <param name="querier">Where the imagined agent is standing.</param>
    /// <param name="context">What the query is about, or null for none.</param>
    /// <param name="resolver">Where registered generators and tests are looked up, or null for a fresh one.</param>
    /// <returns>Whether anything survived.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Unreal's testing pawn, minus the pawn.</b> The preview runs from the editor's own
    ///     selection rather than from a special actor somebody has to remember to place — which is
    ///     what makes "why is this query picking that corner" a question an author answers in the
    ///     editor rather than by launching the game.
    /// </remarks>
    public bool Preview(
        World world,
        Vector3 querier,
        Vector3? context = null,
        BehaviorTreeResolver? resolver = null
    ) {
        ArgumentNullException.ThrowIfNull(world);

        var query = Compile(resolver);

        if (query is null) {
            Results.Clear();

            return false;
        }

        var agent = new AgentContext(world, Entity.Null, new(BlackboardLayout.Empty), null, GameTime.Zero, 0);
        var origin = context is { } about
            ? new QueryOrigin(querier, about, true)
            : new QueryOrigin(querier, Vector3.Zero);

        return query.Run(in agent, in origin, Results);
    }

    /// <summary>Runs a gesture as one undo entry.</summary>
    /// <param name="name">What it is called in the history.</param>
    /// <param name="gesture">What it does to the lists.</param>
    /// <param name="mergeKey">A key that makes two consecutive entries one, or null for its own entry.</param>
    public void Edit(string name, Action<QueryContent> gesture, string? mergeKey = null) {
        ArgumentNullException.ThrowIfNull(gesture);

        var before = Copy(Content);

        gesture(Content);

        var after = Copy(Content);

        Stack.Execute(new SnapshotCommand(name, this, before, after, mergeKey));
        Changed?.Invoke(this);
    }

    /// <summary>Moves a test up or down the list.</summary>
    /// <param name="index">Which test.</param>
    /// <param name="delta">How far, positive for later.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>
    ///     ⚠ <b>The one gesture this editor has that a utility set's does not, and it is the one that
    ///     matters.</b> A filtering test rejects a point and everything below it is skipped, so moving
    ///     a trace below a distance filter is the difference between four hundred raycasts and forty.
    ///     The runtime honours the order exactly; reordering here is how an author pays or saves.
    /// </remarks>
    public bool MoveTest(int index, int delta) {
        var target = index + delta;

        if ((uint)index >= (uint)Content.Tests.Count || (uint)target >= (uint)Content.Tests.Count || delta == 0) {
            return false;
        }

        Edit(
            "Reorder test",
            content => {
                var test = content.Tests[index];

                content.Tests.RemoveAt(index);
                content.Tests.Insert(target, test);
            }
        );

        return true;
    }

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToYaml());

    /// <summary>A deep copy of a query, for an undo entry.</summary>
    /// <param name="content">The query.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> is null.</exception>
    public static QueryContent Copy(QueryContent content) {
        ArgumentNullException.ThrowIfNull(content);

        return new() {
            Version = content.Version,
            Name = content.Name,
            Generators = [
                .. content.Generators.Select(
                    generator => new QueryGeneratorContent {
                        Kind = generator.Kind,
                        Source = generator.Source,
                        Extent = generator.Extent,
                        Inner = generator.Inner,
                        Rings = generator.Rings,
                        Points = generator.Points,
                        Degrees = generator.Degrees,
                        AroundQuerier = generator.AroundQuerier
                    }
                )
            ],
            Tests = [.. content.Tests.Select(Copy)]
        };
    }

    static QueryTestContent Copy(QueryTestContent test) => new() {
        Kind = test.Kind,
        Source = test.Source,
        Purpose = test.Purpose,
        FromContext = test.FromContext,
        Minimum = test.Minimum,
        Maximum = test.Maximum,
        Floor = test.Floor,
        Ceiling = test.Ceiling,
        Weight = test.Weight,
        Curve = test.Curve,
        Slope = test.Slope,
        Exponent = test.Exponent,
        Shift = test.Shift,
        Centre = test.Centre,
        Keys = [
            .. test.Keys.Select(
                key => new UtilityCurveKeyContent {
                    Time = key.Time,
                    Value = key.Value,
                    InTangent = key.InTangent,
                    OutTangent = key.OutTangent,
                    Mode = key.Mode
                }
            )
        ]
    };

    /// <summary>
    ///     A new query: a grid around the agent, kept on the navigable near half. ⚠ A file whose first
    ///     diagnostic is about its own emptiness is one nobody trusts.
    /// </summary>
    static QueryContent New(string name) {
        var content = new QueryContent { Name = name };

        content.Generators.Add(new() { Kind = QueryGeneratorKind.Grid, Extent = 10f, Inner = 1f });
        content.Tests.Add(
            new() {
                Kind = QueryTestKind.Distance,
                Purpose = QueryTestPurpose.Both,
                Maximum = 10f,
                Ceiling = 10f,
                Curve = ResponseCurveKind.Linear,
                Slope = -1f,
                Shift = 1f
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
        QueryDocument document,
        QueryContent before,
        QueryContent after,
        string? mergeKey
    ) : IEditorCommand {
        bool applied;

        /// <inheritdoc />
        public string Name => name;

        /// <summary>What two entries have to agree on before they become one.</summary>
        public string? MergeKey => mergeKey;

        /// <summary>The picture this entry started from, which a merge below it takes over.</summary>
        public QueryContent Before => before;

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
