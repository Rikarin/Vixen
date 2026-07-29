// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>What one pass of loading did.</summary>
/// <param name="Activated">The plugins that are running, in the order they were activated.</param>
/// <param name="Failed">The ones that are not, whatever the reason.</param>
/// <param name="Diagnostics">Every reason, from discovery and from loading alike.</param>
/// <remarks>
///     The editor turns this into one notification and a line in the console; a build server treats
///     any error in it as a failure; a test asserts on it. The same shape as
///     <c>Vixen.Editor.Assets</c>'s import results, and for the same reason: a load pass that
///     communicated by throwing could report exactly one thing.
/// </remarks>
public sealed record PluginReport(
    IReadOnlyList<LoadedPlugin> Activated,
    IReadOnlyList<LoadedPlugin> Failed,
    IReadOnlyList<PluginDiagnostic> Diagnostics
) {
    /// <summary>A report for a pass that had nothing to do.</summary>
    public static PluginReport Empty { get; } = new([], [], []);

    /// <summary>Whether anything went wrong.</summary>
    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == PluginSeverity.Error);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Activated.Count} activated, {Failed.Count} failed, {Diagnostics.Count} diagnostics";
}
