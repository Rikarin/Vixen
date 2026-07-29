// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>A plugin cannot do what it was asked to do.</summary>
/// <remarks>
///     Thrown by the contract — by <see cref="PluginServices.Require{T}" />, and by a plugin that
///     wants to refuse to activate — and caught by <see cref="PluginHost" />, which turns it into a
///     <see cref="PluginDiagnostic" /> against that plugin and rolls its registrations back. It is
///     never thrown <i>out of</i> the host: one plugin refusing to start is not a reason for the
///     editor not to.
/// </remarks>
public sealed class PluginException : Exception {
    /// <summary>Creates the exception.</summary>
    public PluginException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public PluginException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What caused it.</param>
    public PluginException(string message, Exception innerException) : base(message, innerException) { }
}
