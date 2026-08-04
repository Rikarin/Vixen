// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Ai;

/// <summary>Settings for the importer that takes a utility set.</summary>
[DataContract("UtilitySetImporter")]
public sealed record UtilitySetImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxutility</c>.</summary>
/// <remarks>
///     <para>
///         The same two steps a <c>.vxbt</c> takes, and for the same reason: a <see cref="UtilitySet" />
///         holds live curve objects and action indices into a registry a game builds at start-up, so
///         what travels is the <i>data</i> and a game turns it into a set through
///         <see cref="UtilitySetContentCompiler" />.
///     </para>
///     <para>
///         ⚠ <b>What the importer adds over copying the file is the checking, and one check matters
///         more than the rest.</b> A consideration whose key does not exist scores zero, and under the
///         zero rule that vetoes its whole action — so a typo in a key name is an action that silently
///         never runs. That is precisely the failure a designer cannot see by looking at the set, and
///         it fails the build here instead. What cannot be checked is an input a game registers in
///         code, which is a remark for <c>BehaviorTreeImporter</c>'s reason.
///     </para>
/// </remarks>
[Importer(UtilitySetContent.Extension)]
public sealed class UtilitySetImporter : AssetImporter<UtilitySetImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    public const string SetType = "UtilitySetContent";

    static UtilitySetImporter() => MathScalars.Register();

    /// <inheritdoc />
    public override int Version => UtilitySetContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        UtilitySetImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);

            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        UtilitySetContent set;

        try {
            set = YamlSerializer.Parse<UtilitySetContent>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            context.Report(ImportSeverity.Error, exception.Message);

            return context.Finish();
        }

        if (set.Version > UtilitySetContent.Current) {
            context.Report(
                ImportSeverity.Error,
                $"This set is version {set.Version} and this build reads {UtilitySetContent.Current}."
            );

            return context.Finish();
        }

        if (set.Name.Length == 0) {
            set.Name = Path.GetFileNameWithoutExtension(context.SourcePath.Value);
        }

        if (!Check(context, set)) {
            return context.Finish();
        }

        context.Write(SubAssetId.Main, SetType, Serializer.ToBytes(set));

        return context.Finish();
    }

    static bool Check(ImportContext context, UtilitySetContent set) {
        UtilitySetContentCompiler.TryCompile(set, new BehaviorTreeResolver(), out var problems, out _);

        var fatal = false;

        foreach (var problem in problems) {
            if (Unresolvable(problem.Message)) {
                context.Report(ImportSeverity.Warning, $"{problem.Node}: {problem.Message}");

                continue;
            }

            context.Report(ImportSeverity.Error, $"{problem.Node}: {problem.Message}");
            fatal = true;
        }

        if (set.Actions.Count == 0) {
            context.Report(ImportSeverity.Error, "A set with no actions gives its agent nothing to do.");
            fatal = true;
        }

        return !fatal;
    }

    static bool Unresolvable(string message) =>
        message.Contains("is not a task this build knows", StringComparison.Ordinal)
        || message.Contains("No utility input called", StringComparison.Ordinal)
        || message.Contains("has no factory registered", StringComparison.Ordinal);
}
