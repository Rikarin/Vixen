// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Binding;

/// <summary>
///     Refuses a call graph that comes back to where it started — <c>RVN2139</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Raven has never had recursion and had never said so.</b> Four places in this compiler
///         carry a visited set with a comment explaining that the language has none —
///         <c>CallGraph.InCallOrder</c>, <c>Lowerer.CollectStreamUses</c>,
///         <c>LibraryBuilder</c>'s propagation loop — so every pass behind the binder was written
///         against a rule nothing enforced. A recursive <c>func</c> therefore bound, lowered and
///         emitted, and what reported it was <c>spirv-val</c>:
///         <c>[VUID-StandaloneSpirv-None-04634] Entry points may not have a call graph with cycles</c>.
///     </para>
///     <para>
///         ⚠ <b>That is a driver-level error standing in for a source-level one, and it only appears
///         where a validator happens to be installed.</b> SPIR-V forbids recursion outright — there is
///         no call stack in the execution model for a call to return down — so a cycle is not
///         something a backend can lower badly, it is something the front end must refuse. Reported
///         here, before a single instruction is emitted, is the difference between an author reading
///         the names of their own two functions and an author reading a VUID number about a module
///         they did not write. Found by <c>Vixen.Fuzz</c>'s <c>raven</c> target, whose fifth oracle is
///         the validator; the input is <c>Corpus/raven/b3f413d871e6a766.bin</c>, one token away from
///         the shipped example — <c>func Weight(id: uint3): float =&gt; Weight(id) * scale.x</c>.
///     </para>
///     <para>
///         ⚠ <b>Not <c>RVN2005</c>, and the distinction is the same one <c>RVN2008</c> draws.</b> The
///         circular-definition guard is about resolution that does not terminate — a signature
///         reaching its own type — and it fires while a symbol is being built. A recursive
///         <em>body</em> resolves perfectly happily: both signatures are complete before either body
///         is bound, so nothing is ever re-entered and no guard has anything to say. It is only when
///         somebody asks for a program that the answer stops existing.
///     </para>
///     <para>
///         <b>The nodes are the functions a backend emits, which is what the rule is about.</b> That
///         is a member <em>and</em> a body kind — the pair <c>Lowerer</c> keys its own function table
///         on — because a property's getter and setter are two functions on one symbol, and a
///         constructor is a third kind of one. A cycle through an accessor
///         (<c>var P: int { get =&gt; F() }</c> with <c>func F(): int =&gt; P</c>) is the same defect
///         reached by a different spelling, and a check that only walked <c>func</c> would let it
///         past.
///     </para>
///     <para>
///         ⚠ <b>A property reference is an edge to the accessor that reference actually runs.</b>
///         Adding both would be an over-approximation that reports cycles which cannot happen: a
///         getter calling <c>F</c> while <c>F</c> only ever <em>writes</em> the property is not a
///         cycle, and refusing it would be a hard error on a legal shader. The read/write question is
///         answered from the assignment above the reference, which is where the bound tree keeps it.
///     </para>
///     <para>
///         The walk is iterative rather than recursive. A file whose functions form a chain thousands
///         long is the shape a fuzzer produces, and a stack overflow is the one failure the harness
///         that found this cannot report at all (<c>Core/Vixen.Fuzz/README.md</c>).
///     </para>
///     <para>
///         See [07](../../../docs/plan/07-raven-shader-pipeline.md) for the language, and
///         [18](../../../docs/plan/18-parser-migration.md) for the front end this runs behind.
///     </para>
/// </remarks>
static class RecursionCheck {
    /// <summary>One emitted function: a member and which of its bodies.</summary>
    readonly record struct Node(Symbol Member, BoundBodyKind Kind);

    /// <summary>A call, and the syntax that makes it.</summary>
    readonly record struct Edge(Node To, SyntaxNode Syntax);

    /// <summary>A node whose callees are being walked, and how far the walk has got.</summary>
    sealed class Frame(Node node, IEnumerator<Edge> callees) {
        public Node Node { get; } = node;
        public IEnumerator<Edge> Callees { get; } = callees;
    }

    /// <summary>Reports every cycle in the call graph the bodies describe.</summary>
    /// <param name="bodies">Every bound body in the compilation, in source order.</param>
    /// <param name="diagnostics">Where to report.</param>
    public static void Report(IReadOnlyList<BoundBody> bodies, DiagnosticBag diagnostics) {
        Dictionary<Node, List<Edge>> graph = [];
        List<Node> order = [];

        foreach (var body in bodies) {
            var node = new Node(Definition(body.Member), body.Kind);

            // A member with two bodies of the same kind cannot happen, but a second binding of the
            // same tree could add one; keeping the first is what makes this idempotent.
            if (!graph.TryAdd(node, Callees(body))) {
                continue;
            }

            order.Add(node);
        }

        HashSet<Node> reported = [];
        HashSet<Node> onPath = [];
        HashSet<Node> finished = [];

        foreach (var root in order) {
            if (finished.Contains(root)) {
                continue;
            }

            Stack<Frame> stack = new();
            List<Edge> path = [];

            stack.Push(new(root, graph[root].GetEnumerator()));
            onPath.Add(root);

            while (stack.Count > 0) {
                var frame = stack.Peek();

                if (!frame.Callees.MoveNext()) {
                    finished.Add(frame.Node);
                    onPath.Remove(frame.Node);
                    stack.Pop();

                    if (path.Count > 0) {
                        path.RemoveAt(path.Count - 1);
                    }

                    continue;
                }

                var edge = frame.Callees.Current;

                // A callee with no body of its own — an intrinsic, a library import, a call the
                // binder could not resolve — is a leaf rather than a hole: there is nothing behind it
                // for a cycle to run through.
                if (!graph.TryGetValue(edge.To, out var callees)) {
                    continue;
                }

                if (onPath.Contains(edge.To)) {
                    ReportCycle(path, edge, reported, diagnostics);
                    continue;
                }

                if (finished.Contains(edge.To)) {
                    continue;
                }

                onPath.Add(edge.To);
                path.Add(edge);
                stack.Push(new(edge.To, callees.GetEnumerator()));
            }
        }
    }

    /// <summary>Reports one cycle, unless a member of it has already been named in another.</summary>
    /// <remarks>
    ///     ⚠ Once per cycle rather than once per member, and once per <em>member</em> rather than
    ///     once per route: three functions calling each other in a ring are reachable as three
    ///     different routes from three different roots, and a compiler that printed the same defect
    ///     three times over would be teaching the reader to stop reading. The first route found wins,
    ///     which is source order because <paramref name="path" /> is walked in it.
    /// </remarks>
    static void ReportCycle(List<Edge> path, Edge closing, HashSet<Node> reported, DiagnosticBag diagnostics) {
        // The cycle is the tail of the path from wherever the closing call lands. It is not on the
        // path at all when it lands on the walk's own root, which carries no edge — the rest of the
        // path is the cycle then, and starting at nought is what says so.
        var from = path.FindIndex(edge => edge.To == closing.To);
        List<Node> members = [closing.To];

        for (var index = from + 1; index < path.Count; index++) {
            members.Add(path[index].To);
        }

        if (members.Any(reported.Contains)) {
            return;
        }

        foreach (var member in members) {
            reported.Add(member);
        }

        diagnostics.Add(
            SemanticDiagnostics.RecursiveCall,
            closing.Syntax.GetLocation(),
            Describe(closing.To),
            string.Join(" → ", members.Select(Describe).Append(Describe(closing.To)))
        );
    }

    /// <summary>Everything a body calls, in the order the calls are written.</summary>
    static List<Edge> Callees(BoundBody body) {
        List<Edge> edges = [];
        HashSet<BoundExpression> written = [];
        HashSet<BoundExpression> read = [];

        // The assignments first, because whether a property reference runs the getter or the setter
        // is decided by the node above it and the walk below sees each node on its own.
        foreach (var node in body.Body.DescendantsAndSelf()) {
            switch (node) {
                case BoundAssignmentExpression { Target: BoundPropertyExpression target } assignment:
                    written.Add(target);

                    // `P += 1` reads P and writes it back; a plain `P = 1` never runs the getter.
                    if (assignment.OperatorKind is not null) {
                        read.Add(target);
                    }

                    break;

                // `P++` is both, whichever side of the operand the tokens are on.
                case BoundUnaryExpression {
                    OperatorKind: UnaryOperatorKind.PreIncrement
                    or UnaryOperatorKind.PreDecrement
                    or UnaryOperatorKind.PostIncrement
                    or UnaryOperatorKind.PostDecrement,
                    Operand: BoundPropertyExpression operand
                }:
                    written.Add(operand);
                    read.Add(operand);
                    break;
            }
        }

        foreach (var node in body.Body.DescendantsAndSelf()) {
            switch (node) {
                case BoundInvocationExpression invocation:
                    edges.Add(new(NodeFor(invocation.Method), invocation.Syntax));
                    break;

                case BoundObjectCreationExpression { Constructor: { } constructor } creation:
                    edges.Add(new(new(Definition(constructor), BoundBodyKind.Constructor), creation.Syntax));
                    break;

                case BoundPropertyExpression property: {
                    var member = Definition(property.Property);

                    if (!written.Contains(property) || read.Contains(property)) {
                        edges.Add(new(new(member, BoundBodyKind.PropertyGetter), property.Syntax));
                    }

                    if (written.Contains(property)) {
                        edges.Add(new(new(member, BoundBodyKind.PropertySetter), property.Syntax));
                    }

                    break;
                }
            }
        }

        return edges;
    }

    /// <summary>The graph node a call resolves to.</summary>
    static Node NodeFor(MethodSymbol method) =>
        new(Definition(method), method.IsConstructor ? BoundBodyKind.Constructor : BoundBodyKind.Method);

    /// <summary>
    ///     The declaration behind a member, so a view of one is the same node as the one it views.
    /// </summary>
    /// <remarks>
    ///     The same unwrapping <c>Lowerer.FindBody</c> does, and for the same reason:
    ///     <c>Box&lt;float4&gt;.Get</c> and <c>Box&lt;int&gt;.Get</c> are one bound tree read through
    ///     two maps. It also makes <c>F&lt;T&gt;</c> calling <c>F&lt;Box&lt;T&gt;&gt;</c> a cycle,
    ///     which it is: monomorphising that has no fixed point to stop at, exactly as
    ///     <c>RVN2008</c>'s growing form has none.
    /// </remarks>
    static Symbol Definition(Symbol member) =>
        member switch {
            SubstitutedMethodSymbol method => method.OriginalDefinition,
            SubstitutedPropertySymbol property => property.OriginalDefinition,
            SubstitutedFieldSymbol field => field.OriginalDefinition,
            _ => member
        };

    /// <summary>One name in the route: the member, qualified by its type, and which body it is.</summary>
    static string Describe(Node node) {
        var owner = node.Member.ContainingSymbol is { } containing and not NamespaceSymbol
            ? $"{containing.Name}."
            : string.Empty;

        // A method, a constructor and a field initializer are each the only body their member has, so
        // the name alone says which one it is; a property has two and needs telling apart.
        return node.Kind switch {
            BoundBodyKind.PropertyGetter => $"{owner}{node.Member.Name}.get",
            BoundBodyKind.PropertySetter => $"{owner}{node.Member.Name}.set",
            _ => $"{owner}{node.Member.Name}"
        };
    }
}
