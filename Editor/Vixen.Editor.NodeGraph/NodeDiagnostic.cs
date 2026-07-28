// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.NodeGraph;

/// <summary>How much a diagnostic matters.</summary>
public enum NodeSeverity {
    /// <summary>Worth saying. The graph still compiles.</summary>
    Warning,

    /// <summary>The graph does not compile.</summary>
    Error
}

/// <summary>Something the compiler has to say, about a place an author can see.</summary>
/// <param name="Id">A stable code, so a test names one without matching prose.</param>
/// <param name="Message">What is wrong, as a person would say it.</param>
/// <param name="Node">Which node it is about.</param>
/// <param name="Port">Which of its ports, when it is about one.</param>
/// <param name="Severity">How much it matters.</param>
/// <remarks>
///     <b>It carries a node and a port because that is the whole point.</b> A generated shader that
///     fails to compile reports a line and a column in text the author never wrote; doc 07 calls for
///     those to be mapped back to node ports, and a diagnostic that cannot name one cannot be mapped.
///     Every diagnostic raised here names the node it came from, and the ones about a connection name
///     the port too.
/// </remarks>
public readonly record struct NodeDiagnostic(
    string Id,
    string Message,
    NodeId Node,
    string Port = "",
    NodeSeverity Severity = NodeSeverity.Error
) {
    /// <inheritdoc />
    public override string ToString() =>
        $"{Severity.ToString().ToUpperInvariant()} {Id}: {Message} ({Node}{(Port.Length > 0 ? "." + Port : "")})";
}

/// <summary>What compiling a graph produced.</summary>
/// <typeparam name="T">The artefact the graph compiles to.</typeparam>
/// <param name="Artefact">It, or the type's default when the graph did not compile.</param>
/// <param name="Diagnostics">Everything the compiler had to say, in the order it said it.</param>
public sealed record NodeGraphCompilation<T>(T? Artefact, ImmutableArray<NodeDiagnostic> Diagnostics) {
    /// <summary>Whether an artefact was produced.</summary>
    public bool Succeeded => Artefact is not null && !HasErrors;

    /// <summary>Whether anything went wrong.</summary>
    public bool HasErrors {
        get {
            foreach (var diagnostic in Diagnostics) {
                if (diagnostic.Severity == NodeSeverity.Error) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The artefact, or a stated failure naming what went wrong.</summary>
    /// <returns>The artefact.</returns>
    /// <exception cref="InvalidOperationException">The graph did not compile.</exception>
    /// <remarks>
    ///     For a test and for a caller that has already decided a failure is not survivable. Anything
    ///     showing a graph to a person wants <see cref="Diagnostics" /> instead, because the whole
    ///     value of a diagnostic here is the node it points at.
    /// </remarks>
    public T Value =>
        Succeeded
            ? Artefact!
            : throw new InvalidOperationException(
                "The graph did not compile:" + Environment.NewLine
                + string.Join(Environment.NewLine, Diagnostics)
            );
}
