// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets;

/// <summary>How much attention something an importer said needs.</summary>
public enum ImportSeverity {
    /// <summary>Worth knowing. The import succeeded.</summary>
    Information,

    /// <summary>Worth fixing. The import succeeded anyway.</summary>
    Warning,

    /// <summary>The import did not succeed.</summary>
    Error
}

/// <summary>Something an importer said about an asset.</summary>
/// <param name="Severity">How much attention it needs.</param>
/// <param name="Message">What it says, in a sentence a person can act on.</param>
/// <param name="Path">
///     Which asset it is about, project-relative, or empty when it is about the project as a whole.
/// </param>
/// <remarks>
///     <para>
///         Carried out of the import rather than logged from inside it, so that the editor can show
///         it against the asset in the inspector, the CLI can print it, and a build can fail on it —
///         three consumers that a call to a logger inside the importer would have served none of.
///     </para>
///     <para>
///         ⚠ <b><see cref="Path" /> is what turns a build-plan message into an entry an IDE can
///         open</b>, and it is last and defaulted because an importer never has to supply one: an
///         importer is already running <i>on</i> an asset, and <c>ImportExecutor</c> knows which.
///         What did not know was <see cref="Content.BuildPlanner" />, which walks every asset in the
///         project and used to name the one it was talking about only inside the sentence — so
///         <c>vixen --format msbuild</c> emitted its errors with no file, MSBuild attributed them to
///         the project, and the IDE's jump-to-file went nowhere. The sentence still names the asset,
///         because a person reading a log has no error list to click.
///     </para>
///     <para>
///         ⚠ <b>Empty is a real answer and not a missing one.</b> "Some assets name no group", "this
///         is a server build and nothing is marked <c>includeInServerBuild: false</c>", and the
///         collision in which several assets claim one address are all statements about the project
///         rather than about a file — and the last of those deliberately keeps its path empty even
///         though it holds several, because naming one of them would be deciding the very question
///         the message says cannot be decided.
///     </para>
/// </remarks>
public sealed record ImportDiagnostic(ImportSeverity Severity, string Message, string Path = "");

/// <summary>One thing an importer produced.</summary>
/// <param name="SubAsset">Which sub-asset it is, or <see cref="SubAssetId.Main" /> for the main object.</param>
/// <param name="Type">What kind of thing it is — <c>Texture</c>, <c>Mesh</c>.</param>
/// <param name="Content">Its bytes.</param>
public sealed record ImportedArtifact(SubAssetId SubAsset, string Type, ReadOnlyMemory<byte> Content);

/// <summary>What one import produced.</summary>
/// <param name="Artifacts">The artefacts.</param>
/// <param name="SubAssets">What the asset now declares it contains.</param>
/// <param name="Diagnostics">Everything the importer said.</param>
public sealed record ImportResult(
    IReadOnlyList<ImportedArtifact> Artifacts,
    IReadOnlyList<SubAssetEntry> SubAssets,
    IReadOnlyList<ImportDiagnostic> Diagnostics
) {
    /// <summary>Whether anything said it was an error.</summary>
    public bool Succeeded => !Diagnostics.Any(diagnostic => diagnostic.Severity == ImportSeverity.Error);

    /// <summary>An import that produced nothing and failed.</summary>
    /// <param name="message">Why.</param>
    /// <returns>The result.</returns>
    public static ImportResult Failed(string message) =>
        new([], [], [new(ImportSeverity.Error, message)]);
}
