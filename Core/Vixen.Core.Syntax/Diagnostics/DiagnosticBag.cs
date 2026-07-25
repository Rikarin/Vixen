using System.Collections;

namespace Vixen.Core.Syntax.Diagnostics;

/// <summary>
///     A mutable accumulator of <see cref="Diagnostic" />s produced during a compilation
///     phase. Enumerable and cheaply mergeable; call <see cref="ToArray" /> for an
///     immutable snapshot.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic> {
    readonly List<Diagnostic> diagnostics = [];

    /// <summary>Whether nothing has been reported.</summary>
    public bool IsEmpty => diagnostics.Count == 0;

    /// <summary>Whether any reported diagnostic has error severity.</summary>
    public bool HasErrors {
        get {
            foreach (var d in diagnostics) {
                if (d.IsError) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Reports an already-built diagnostic.</summary>
    public void Add(Diagnostic diagnostic) => diagnostics.Add(diagnostic);

    /// <summary>Builds and reports a diagnostic from a descriptor and message arguments.</summary>
    public void Add(DiagnosticDescriptor descriptor, Location location, params object[] arguments) =>
        diagnostics.Add(Diagnostic.Create(descriptor, location, arguments));

    /// <summary>Reports every diagnostic in <paramref name="items" />, preserving order.</summary>
    public void AddRange(IEnumerable<Diagnostic> items) => diagnostics.AddRange(items);

    /// <summary>An immutable snapshot of what has been reported so far.</summary>
    public Diagnostic[] ToArray() => diagnostics.ToArray();

    /// <summary>Enumerates the reported diagnostics in the order they were added.</summary>
    public IEnumerator<Diagnostic> GetEnumerator() => diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
