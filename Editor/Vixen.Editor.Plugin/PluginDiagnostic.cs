// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>How much a plugin diagnostic matters.</summary>
public enum PluginSeverity : byte {
    /// <summary>Something the user should know. The plugin is running anyway.</summary>
    Warning,

    /// <summary>The plugin is not running, and this says why.</summary>
    Error
}

/// <summary>Something the loader has to say about one plugin.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="PluginId">Which plugin, or the folder's name when the manifest had no usable id.</param>
/// <param name="Message">What happened, in the words a plugin author needs to fix it.</param>
/// <remarks>
///     <para>
///         <b>Returned rather than logged, and rather than thrown.</b> Thrown, the first broken
///         plugin in a folder of six stops the editor from starting; logged, it is a line in a file
///         nobody reads while the user wonders why their toolbar is missing a button. As data it can
///         be a notification, a row in a plugin panel, and a test's assertion — which is what the
///         importers' <c>ImportResult</c> and the settings store's <c>UnknownKeys</c> already do.
///     </para>
/// </remarks>
public sealed record PluginDiagnostic(PluginSeverity Severity, string PluginId, string Message) {
    /// <inheritdoc />
    public override string ToString() => $"{Severity.ToString().ToUpperInvariant()} {PluginId}: {Message}";
}
