// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Ai;

/// <summary>Settings for the importer that takes an environment query.</summary>
[DataContract("QueryImporter")]
public sealed record QueryImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxquery</c>.</summary>
/// <remarks>
///     <para>
///         The same two steps a <c>.vxutility</c> takes, and for the same reason: an
///         <see cref="EnvironmentQuery" /> holds live generator and test objects — a physics world, a
///         navmesh — so what travels is the <i>data</i> and a game turns it into a query through
///         <see cref="QueryContentCompiler" />.
///     </para>
///     <para>
///         ⚠ <b>Two checks that a designer cannot make by looking at the file.</b> A query with no
///         generators has nothing to score and answers "nowhere" for ever; and a query with no
///         <i>scoring</i> test ranks every surviving point equally, so it always returns whichever
///         point the generator happened to make first — which looks like the query working and is a
///         coin toss. Both fail the build here.
///     </para>
///     <para>
///         ⚠ <b>A registered generator or test that this build has never heard of is a warning and not
///         an error</b>, for <c>BehaviorTreeImporter</c>'s reason: the importer compiles against an
///         empty resolver, and a game registers its own trace and its own cover test in code at
///         start-up. Failing the build on those would fail it on every query worth writing.
///     </para>
/// </remarks>
[Importer(QueryContent.Extension)]
public sealed class QueryImporter : AssetImporter<QueryImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    public const string QueryType = "QueryContent";

    static QueryImporter() => MathScalars.Register();

    /// <inheritdoc />
    public override int Version => QueryContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        QueryImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);

            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        QueryContent query;

        try {
            query = YamlSerializer.Parse<QueryContent>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            context.Report(ImportSeverity.Error, exception.Message);

            return context.Finish();
        }

        if (query.Version > QueryContent.Current) {
            context.Report(
                ImportSeverity.Error,
                $"This query is version {query.Version} and this build reads {QueryContent.Current}."
            );

            return context.Finish();
        }

        if (query.Name.Length == 0) {
            query.Name = Path.GetFileNameWithoutExtension(context.SourcePath.Value);
        }

        if (!Check(context, query)) {
            return context.Finish();
        }

        context.Write(SubAssetId.Main, QueryType, Serializer.ToBytes(query));

        return context.Finish();
    }

    static bool Check(ImportContext context, QueryContent query) {
        QueryContentCompiler.TryCompile(query, new BehaviorTreeResolver(), out var problems, out _);

        var fatal = false;

        foreach (var problem in problems) {
            if (Unresolvable(problem.Message)) {
                context.Report(ImportSeverity.Warning, $"{problem.Node}: {problem.Message}");

                continue;
            }

            context.Report(ImportSeverity.Error, $"{problem.Node}: {problem.Message}");
            fatal = true;
        }

        if (query.Generators.Count == 0) {
            context.Report(ImportSeverity.Error, "A query with no generators has nothing to score.");
            fatal = true;
        }

        if (query.Tests.Count > 0 && !query.Tests.Exists(test => test.Purpose != QueryTestPurpose.Filter)) {
            context.Report(
                ImportSeverity.Error,
                "Every test in this query only filters, so nothing ranks the points that survive and "
                + "the answer is whichever one the generator happened to make first."
            );

            fatal = true;
        }

        return !fatal;
    }

    static bool Unresolvable(string message) =>
        message.Contains("No query generator called", StringComparison.Ordinal)
        || message.Contains("No query test called", StringComparison.Ordinal);
}
