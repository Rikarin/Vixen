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
/// <remarks>
///     Carried out of the import rather than logged from inside it, so that the editor can show it
///     against the asset in the inspector, the CLI can print it, and a build can fail on it — three
///     consumers that a call to a logger inside the importer would have served none of.
/// </remarks>
public sealed record ImportDiagnostic(ImportSeverity Severity, string Message);

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
