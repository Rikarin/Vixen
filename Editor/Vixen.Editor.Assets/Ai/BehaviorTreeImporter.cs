// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Ai;

/// <summary>Settings for the importer that takes a behaviour tree.</summary>
[DataContract("BehaviorTreeImporter")]
public sealed record BehaviorTreeImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxbt</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The artefact is the tree's <i>data</i>, not a compiled template.</b> A
///         <see cref="BehaviorTreeTemplate" /> holds live decorator objects and action indices into a
///         registry a game builds at start-up, so there is nothing there to write bytes for. What
///         travels is <see cref="BehaviorTreeContent" />, and a game turns it into a template through
///         <see cref="BehaviorTreeContentCompiler" /> against its own registries — which is the same
///         two-step every asset with code on the other side of it takes.
///     </para>
///     <para>
///         ⚠ <b>What the importer adds over copying the file is the checking.</b> A tree whose keys
///         collide, whose composite has no children, whose parallel's first child is not a task, or
///         whose decorator observes nothing it reads, is a tree that fails at run time in a way that
///         reads as "the AI sometimes gets stuck" — so it fails the build instead. What it cannot
///         check is a task naming an action a game registers in code, and those are reported as
///         remarks rather than errors for <c>AnimationGraphCompiler</c>'s reason: laying out a tree
///         before the code exists is the ordinary order of work.
///     </para>
/// </remarks>
[Importer(BehaviorTreeContent.Extension)]
public sealed class BehaviorTreeImporter : AssetImporter<BehaviorTreeImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    public const string TreeType = "BehaviorTreeContent";

    static BehaviorTreeImporter() => MathScalars.Register();

    /// <inheritdoc />
    public override int Version => BehaviorTreeContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        BehaviorTreeImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);

            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        BehaviorTreeContent tree;

        try {
            tree = YamlSerializer.Parse<BehaviorTreeContent>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            context.Report(ImportSeverity.Error, exception.Message);

            return context.Finish();
        }

        if (tree.Version > BehaviorTreeContent.Current) {
            context.Report(
                ImportSeverity.Error,
                $"This tree is version {tree.Version} and this build reads {BehaviorTreeContent.Current}."
            );

            return context.Finish();
        }

        if (tree.Name.Length == 0) {
            tree.Name = Path.GetFileNameWithoutExtension(context.SourcePath.Value);
        }

        if (!Check(context, tree)) {
            return context.Finish();
        }

        context.Write(SubAssetId.Main, TreeType, Serializer.ToBytes(tree));

        return context.Finish();
    }

    static bool Check(ImportContext context, BehaviorTreeContent tree) {
        var problems = new List<BehaviorTreeDiagnostic>();
        var resolver = new BehaviorTreeResolver();
        var layout = tree.BuildLayout(problems);
        var asset = BehaviorTreeContentCompiler.Build(tree, resolver, layout, problems);

        if (!BehaviorTreeCompiler.TryCompile(asset, resolver.Actions, layout, out var compiled, out _)) {
            problems.AddRange(compiled);
        }

        var fatal = false;

        foreach (var problem in problems) {
            // ⚠ A name a game registers in code cannot be resolved here and is not an error. Every
            // other complaint is about the file itself and is.
            if (Unresolvable(problem.Message)) {
                context.Report(ImportSeverity.Warning, $"{problem.Node}: {problem.Message}");

                continue;
            }

            context.Report(ImportSeverity.Error, $"{problem.Node}: {problem.Message}");
            fatal = true;
        }

        return !fatal;
    }

    static bool Unresolvable(string message) =>
        message.Contains("is not a node this build knows", StringComparison.Ordinal)
        || message.Contains("No sensor called", StringComparison.Ordinal)
        || message.Contains("to splice in here", StringComparison.Ordinal);
}
