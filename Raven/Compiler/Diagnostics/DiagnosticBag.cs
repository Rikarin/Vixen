using System.Collections;

namespace Vixen.Raven.Diagnostics;

/// <summary>
/// A mutable accumulator of <see cref="Diagnostic"/>s produced during a compilation
/// phase. Enumerable and cheaply mergeable; call <see cref="ToArray"/> for an
/// immutable snapshot.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic> {
    readonly List<Diagnostic> diagnostics = [];

    public bool IsEmpty => diagnostics.Count == 0;

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

    public void Add(Diagnostic diagnostic) => diagnostics.Add(diagnostic);

    public void Add(DiagnosticDescriptor descriptor, Location location, params object[] arguments) =>
        diagnostics.Add(Diagnostic.Create(descriptor, location, arguments));

    public void AddRange(IEnumerable<Diagnostic> items) => diagnostics.AddRange(items);

    public Diagnostic[] ToArray() => diagnostics.ToArray();

    public IEnumerator<Diagnostic> GetEnumerator() => diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
