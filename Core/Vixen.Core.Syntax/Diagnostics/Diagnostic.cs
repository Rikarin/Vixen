// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Core.Syntax.Diagnostics;

/// <summary>
///     A single reported problem: a <see cref="DiagnosticDescriptor" /> plus the
///     <see cref="Location" /> it applies to and the arguments that fill its message.
/// </summary>
public sealed class Diagnostic {
    readonly object[] arguments;

    /// <summary>The rule this diagnostic instantiates.</summary>
    public DiagnosticDescriptor Descriptor { get; }

    /// <summary>Where in source the problem is, or <see cref="Location.None" /> if nowhere.</summary>
    public Location Location { get; }

    /// <summary>Effective severity, taken from the descriptor at creation time.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>The descriptor's stable id, e.g. <c>RVN2002</c>.</summary>
    public string Id => Descriptor.Id;

    /// <summary>Whether this diagnostic should fail the compilation.</summary>
    public bool IsError => Severity == DiagnosticSeverity.Error;

    Diagnostic(DiagnosticDescriptor descriptor, Location location, DiagnosticSeverity severity, object[] arguments) {
        Descriptor = descriptor;
        Location = location;
        Severity = severity;
        this.arguments = arguments;
    }

    /// <summary>Creates a diagnostic at the descriptor's default severity.</summary>
    public static Diagnostic Create(DiagnosticDescriptor descriptor, Location location, params object[] arguments) =>
        new(descriptor, location ?? Location.None, descriptor.DefaultSeverity, arguments ?? []);

    /// <summary>The descriptor's message template filled with this diagnostic's arguments.</summary>
    public string GetMessage() =>
        arguments.Length == 0
            ? Descriptor.MessageFormat
            : string.Format(CultureInfo.CurrentCulture, Descriptor.MessageFormat, arguments);

    /// <summary>Roslyn-style <c>path(line,col): severity ID: message</c>.</summary>
    public override string ToString() {
        var severity = Severity.ToString().ToLowerInvariant();
        var prefix = Location.IsNone ? string.Empty : $"{Location}: ";
        return $"{prefix}{severity} {Id}: {GetMessage()}";
    }
}
