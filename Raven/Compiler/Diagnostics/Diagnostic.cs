using System.Globalization;

namespace Vixen.Raven.Diagnostics;

/// <summary>
/// A single reported problem: a <see cref="DiagnosticDescriptor"/> plus the
/// <see cref="Location"/> it applies to and the arguments that fill its message.
/// </summary>
public sealed class Diagnostic {
    readonly object[] arguments;

    Diagnostic(DiagnosticDescriptor descriptor, Location location, DiagnosticSeverity severity, object[] arguments) {
        Descriptor = descriptor;
        Location = location;
        Severity = severity;
        this.arguments = arguments;
    }

    public DiagnosticDescriptor Descriptor { get; }

    public Location Location { get; }

    public DiagnosticSeverity Severity { get; }

    public string Id => Descriptor.Id;

    public bool IsError => Severity == DiagnosticSeverity.Error;

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
