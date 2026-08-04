// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Ai;

/// <summary>Settings for the importer that takes a GOAP domain.</summary>
[DataContract("GoapDomainImporter")]
public sealed record GoapDomainImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxgoap</c>.</summary>
/// <remarks>
///     <para>
///         The same two steps every AI asset takes: what travels is the data, and a game turns it into
///         a <see cref="GoapDomain" /> — which builds the action graph — through
///         <see cref="GoapDomainContentCompiler" /> against its own registries.
///     </para>
///     <para>
///         ⚠ <b>What the importer adds is the two checks a designer cannot make by looking.</b> A
///         condition on a world key that does not exist never holds, so the action it gates never runs
///         and the goal it belongs to is never met — with nothing in the game to look at. And a goal
///         whose conditions no effect can serve is a goal the resolver will report as unreachable once
///         per re-plan, for ever. Both fail the build here.
///     </para>
/// </remarks>
[Importer(GoapDomainContent.Extension)]
public sealed class GoapDomainImporter : AssetImporter<GoapDomainImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    public const string DomainType = "GoapDomainContent";

    static GoapDomainImporter() => MathScalars.Register();

    /// <inheritdoc />
    public override int Version => GoapDomainContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        GoapDomainImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);

            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        GoapDomainContent domain;

        try {
            domain = YamlSerializer.Parse<GoapDomainContent>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            context.Report(ImportSeverity.Error, exception.Message);

            return context.Finish();
        }

        if (domain.Version > GoapDomainContent.Current) {
            context.Report(
                ImportSeverity.Error,
                $"This domain is version {domain.Version} and this build reads {GoapDomainContent.Current}."
            );

            return context.Finish();
        }

        if (domain.Name.Length == 0) {
            domain.Name = Path.GetFileNameWithoutExtension(context.SourcePath.Value);
        }

        if (!Check(context, domain)) {
            return context.Finish();
        }

        context.Write(SubAssetId.Main, DomainType, Serializer.ToBytes(domain));

        return context.Finish();
    }

    static bool Check(ImportContext context, GoapDomainContent content) {
        GoapDomainContentCompiler.TryCompile(content, new BehaviorTreeResolver(), out var problems, out var domain);

        var fatal = false;

        foreach (var problem in problems) {
            if (Unresolvable(problem.Message)) {
                context.Report(ImportSeverity.Warning, $"{problem.Node}: {problem.Message}");

                continue;
            }

            context.Report(ImportSeverity.Error, $"{problem.Node}: {problem.Message}");
            fatal = true;
        }

        if (content.Goals.Count == 0) {
            context.Report(ImportSeverity.Error, "A domain with no goals gives its agents nothing to want.");
            fatal = true;
        }

        return !Unservable(context, domain) && !fatal;
    }

    /// <summary>⚠ A goal nothing can serve is a resolve that fails once per re-plan, for ever.</summary>
    static bool Unservable(ImportContext context, GoapDomain? domain) {
        if (domain is null) {
            return false;
        }

        var found = new int[Math.Max(1, domain.Count)];
        var fatal = false;

        foreach (var goal in domain.Goals) {
            foreach (var condition in goal.Conditions) {
                if (domain.Servers(in condition, found) > 0) {
                    continue;
                }

                context.Report(
                    ImportSeverity.Error,
                    $"{goal.Name}: no action has an effect that could ever satisfy "
                    + $"'{domain.Keys.NameOf(condition.Key)} {condition.Comparison} {condition.Value}'."
                );

                fatal = true;
            }
        }

        return fatal;
    }

    static bool Unresolvable(string message) =>
        message.Contains("is not a task this build knows", StringComparison.Ordinal)
        || message.Contains("No world source called", StringComparison.Ordinal)
        || message.Contains("has no factory registered", StringComparison.Ordinal);
}
