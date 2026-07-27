// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Binding;

/// <summary>
///     Definite assignment and reachability over one bound body.
/// </summary>
/// <remarks>
///     <para>
///         Two questions the rest of the compiler could not answer, and both have the same shape:
///         what is true on <em>every</em> path to a point. Constant folding already removes a branch
///         whose condition is known — that is what makes a <c>[Permutation]</c> key pay for itself —
///         but it says nothing about a value that is only written on one side of an <c>if</c>.
///     </para>
///     <para>
///         <strong>Why it matters more here than on a CPU.</strong> Reading an unassigned local on a
///         GPU is not an exception and not a zero: it is whatever was in the register, which differs
///         between drivers, between invocations, and between debug and release. That is the shape of
///         bug that reproduces on one machine and nowhere else, so it is an error rather than a
///         warning — the same reasoning that made a missing workgroup size <c>RVN2104</c>.
///     </para>
///     <para>
///         <strong>Sound, and deliberately incomplete.</strong> The analysis only reports what it can
///         prove: a read is refused when <em>no</em> path to it assigns the local. A local assigned
///         in one arm of an <c>if</c> is not definitely assigned after it, which is the same rule C#
///         applies and the reason a loop body's assignments do not count after the loop — the body
///         may run zero times. It does not track <em>partial</em> initialisation: writing
///         <c>r.origin</c> counts as assigning <c>r</c>, because proving otherwise needs per-field
///         state and the false positives would land on code that is fine.
///     </para>
/// </remarks>
static class FlowAnalysis {
    /// <summary>What a statement does to the flow that follows it.</summary>
    enum Exit {
        /// <summary>Control reaches the next statement.</summary>
        FallsThrough,

        /// <summary>Control leaves the function.</summary>
        Returns,

        /// <summary>
        ///     Control leaves the whole invocation: a <c>discard</c>, which no caller resumes from.
        /// </summary>
        /// <remarks>
        ///     Kept apart from <see cref="Returns" /> only so a diagnostic can name what it was that
        ///     made the code after it dead. Everywhere a decision is made, the two behave alike —
        ///     see <see cref="Leaves" />.
        /// </remarks>
        Discards,

        /// <summary>Control leaves the enclosing loop or switch section.</summary>
        Breaks,

        /// <summary>Control jumps to the enclosing loop's next iteration.</summary>
        Continues
    }

    /// <summary>Whether an exit means the rest of the function is not reached from here.</summary>
    static bool Leaves(Exit exit) => exit is Exit.Returns or Exit.Discards;

    /// <summary>
    ///     Checks one body and reports what it finds.
    /// </summary>
    /// <param name="body">The bound body, already type-checked.</param>
    /// <param name="diagnostics">Where to report.</param>
    public static void Analyse(BoundBody body, DiagnosticBag diagnostics) {
        var state = new State(diagnostics);

        // Every parameter arrives assigned, including a setter's synthesized `value`.
        foreach (var parameter in body.Parameters) {
            state.Assigned.Add(parameter);
        }

        var exit = state.Walk(body.Body);

        // A value-returning function whose end is reachable returns whatever the target left in the
        // register — the same undefined value an unassigned local holds, and undetectable at run
        // time. A constructor is exempt: it hands back the value it built, which lowering supplies.
        if (exit == Exit.FallsThrough
            && !body.ReturnType.IsVoid
            && !body.ReturnType.IsErrorType
            && body.Kind != BoundBodyKind.Constructor) {
            diagnostics.Add(
                SemanticDiagnostics.NotAllPathsReturn,
                body.Body.Syntax.GetLocation(),
                body.Member.Name
            );
        }
    }

    /// <summary>The walk's mutable state: what is assigned, and where to report.</summary>
    sealed class State(DiagnosticBag diagnostics) {
        /// <summary>
        ///     Locals and parameters known to hold a value at the current point.
        /// </summary>
        /// <remarks>
        ///     Symbols rather than names, so a local shadowing another in a nested block is a
        ///     different entry. A local is added when it is assigned <em>or</em> when it is reported
        ///     as unassigned, which is what keeps one mistake from producing a diagnostic per use.
        /// </remarks>
        public HashSet<Symbol> Assigned { get; } = [];

        public Exit Walk(BoundStatement statement) {
            switch (statement) {
                case BoundBlockStatement block:
                    return WalkBlock(block.Statements);

                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer is { } initializer) {
                        Read(initializer);
                        Assigned.Add(declaration.Local);
                    }

                    return Exit.FallsThrough;

                case BoundExpressionStatement expression:
                    Read(expression.Expression);
                    return Exit.FallsThrough;

                case BoundIfStatement conditional:
                    return WalkIf(conditional);

                case BoundWhileStatement loop:
                    Read(loop.Condition);
                    WalkMaybe(loop.Body);
                    return Exit.FallsThrough;

                // The body runs before the first test, so what it assigns *is* assigned after.
                case BoundRepeatStatement loop: {
                    var exit = Walk(loop.Body);
                    Read(loop.Condition);
                    return Leaves(exit) ? exit : Exit.FallsThrough;
                }

                case BoundForStatement loop:
                    Read(loop.Sequence);
                    Assigned.Add(loop.IterationVariable);
                    WalkMaybe(loop.Body);
                    return Exit.FallsThrough;

                case BoundSwitchStatement @switch:
                    return WalkSwitch(@switch);

                case BoundReturnStatement @return:
                    if (@return.Expression is { } value) {
                        Read(value);
                    }

                    return Exit.Returns;

                case BoundBreakStatement:
                    return Exit.Breaks;

                case BoundContinueStatement:
                    return Exit.Continues;

                case BoundDiscardStatement:
                    return Exit.Discards;

                default:
                    return Exit.FallsThrough;
            }
        }

        Exit WalkBlock(IReadOnlyList<BoundStatement> statements) {
            var exit = Exit.FallsThrough;
            var said = false;

            foreach (var statement in statements) {
                if (exit != Exit.FallsThrough) {
                    // Once per run rather than once per statement: a block of dead code is one
                    // mistake. An empty statement — which a blank line binds to — is not it.
                    if (!said && statement is not BoundNoOpStatement) {
                        said = true;
                        diagnostics.Add(
                            SemanticDiagnostics.UnreachableStatement,
                            statement.Syntax.GetLocation(),
                            exit switch {
                                Exit.Returns => "a 'return'",
                                Exit.Discards => "a 'discard'",
                                Exit.Breaks => "a 'break'",
                                _ => "a 'continue'"
                            }
                        );
                    }

                    // Walked anyway, so a mistake *inside* unreachable code is still reported —
                    // it is code the author believes runs.
                    Walk(statement);
                    continue;
                }

                exit = Walk(statement);
            }

            return exit;
        }

        /// <summary>
        ///     An <c>if</c>: both arms run from the same entry state, and only what both assign is
        ///     assigned after.
        /// </summary>
        /// <remarks>
        ///     An arm that cannot fall through contributes nothing to the intersection but also
        ///     removes nothing — <c>if (c) { return } x = 1</c> reaches the assignment only one way,
        ///     so <c>x</c> is assigned after the whole statement.
        /// </remarks>
        Exit WalkIf(BoundIfStatement conditional) {
            Read(conditional.Condition);

            var entry = Snapshot();
            var thenExit = Walk(conditional.Consequence);
            var thenState = Snapshot();

            Restore(entry);

            if (conditional.Alternative is not { } alternative) {
                // No else arm: the empty path assigns nothing, so nothing survives except what was
                // already assigned before.
                return Exit.FallsThrough;
            }

            var elseExit = Walk(alternative);

            Merge(thenState, thenExit, elseExit);
            return Combine(thenExit, elseExit);
        }

        /// <summary>
        ///     A <c>switch</c>: every section runs from the same entry state, and the statement as a
        ///     whole is one more path unless a <c>default</c> covers the values no label names.
        /// </summary>
        Exit WalkSwitch(BoundSwitchStatement @switch) {
            Read(@switch.GoverningExpression);

            var entry = Snapshot();
            HashSet<Symbol>? common = null;
            var hasDefault = @switch.Sections.Any(s => s.IsDefault);
            var everySectionLeaves = @switch.Sections.Count > 0;
            var anySectionReturns = false;

            foreach (var section in @switch.Sections) {
                Restore(entry);

                foreach (var label in section.Labels) {
                    Read(label);
                }

                var sectionExit = WalkBlock(section.Statements);
                if (Leaves(sectionExit)) {
                    anySectionReturns |= sectionExit == Exit.Returns;
                    continue;
                }

                // A section that leaves by `break` — or by running out — reaches the code after the
                // switch, so it is one of the paths the intersection is over.
                everySectionLeaves = false;
                common = common is null ? Snapshot() : Intersect(common, Snapshot());
            }

            Restore(entry);

            // Without a `default` the governing value may match nothing, so falling straight past
            // the statement is a path of its own and it assigns nothing.
            if (hasDefault && common is not null) {
                foreach (var symbol in common) {
                    Assigned.Add(symbol);
                }
            }

            if (!hasDefault || !everySectionLeaves) {
                return Exit.FallsThrough;
            }

            return anySectionReturns ? Exit.Returns : Exit.Discards;
        }

        /// <summary>Walks a body that may not run, keeping only what was assigned before it.</summary>
        void WalkMaybe(BoundStatement body) {
            var entry = Snapshot();
            Walk(body);
            Restore(entry);
        }

        /// <summary>Records every read in an expression, reporting the ones that cannot be defined.</summary>
        /// <remarks>
        ///     An explicit walk rather than a descendant sweep, because three shapes have to stop
        ///     it: an assignment's target is written, an index inside that target is still read, and
        ///     an <c>inout</c> argument is treated as written. A sweep sees every leaf and cannot
        ///     tell those apart.
        /// </remarks>
        void Read(BoundExpression expression) {
            switch (expression) {
                case BoundLocalExpression local:
                    Require(local.Local, local);
                    return;

                case BoundParameterExpression parameter:
                    Require(parameter.Parameter, parameter);
                    return;

                case BoundAssignmentExpression assignment:
                    Read(assignment.Value);

                    // A compound assignment reads the target first; a plain one does not.
                    if (assignment.OperatorKind is not null) {
                        Read(assignment.Target);
                    } else {
                        ReadIndices(assignment.Target);
                    }

                    Assign(assignment.Target);
                    return;

                case BoundInvocationExpression invocation:
                    ReadArguments(invocation);
                    return;

                default:
                    foreach (var child in expression.Children.OfType<BoundExpression>()) {
                        Read(child);
                    }

                    return;
            }
        }

        /// <summary>
        ///     Reads a call's arguments, treating an <c>inout</c> one as written rather than read.
        /// </summary>
        /// <remarks>
        ///     <c>inout</c> is copy-in/copy-out, so strictly the argument is read as well — but
        ///     filling a value is what it is <em>for</em>: Raven has no <c>out</c>, and
        ///     <c>MaterialSurface</c>'s whole contract is a feature accumulating into a surface the
        ///     caller declared. Requiring the caller to zero it first would be a diagnostic on
        ///     correct code, which is the false positive this analysis is most careful to avoid.
        /// </remarks>
        void ReadArguments(BoundInvocationExpression invocation) {
            if (invocation.Receiver is { } receiver) {
                Read(receiver);
            }

            var parameters = invocation.Method.Parameters;

            for (var i = 0; i < invocation.Arguments.Count; i++) {
                var argument = invocation.Arguments[i];

                if (i < parameters.Count && parameters[i].RefKind == RefKind.InOut) {
                    ReadIndices(argument);
                    Assign(argument);
                    continue;
                }

                Read(argument);
            }
        }

        /// <summary>
        ///     Reads only the indices along an access chain, not the storage it lands in.
        /// </summary>
        /// <remarks>
        ///     <c>a[i].b = x</c> reads <c>i</c> and writes <c>a</c>. Writing <em>through</em> a
        ///     local is how a struct is filled in a language with no constructor requirement, so
        ///     requiring the root to be assigned first would refuse the ordinary
        ///     <c>var r: Ray; r.origin = …</c>.
        /// </remarks>
        void ReadIndices(BoundExpression target) {
            switch (target) {
                case BoundFieldExpression { Receiver: { } receiver }:
                    ReadIndices(receiver);
                    return;

                case BoundArrayAccessExpression access:
                    ReadIndices(access.Receiver);

                    foreach (var index in access.Indices) {
                        Read(index);
                    }

                    return;
            }
        }

        void Assign(BoundExpression target) {
            if (Root(target) is BoundLocalExpression local) {
                Assigned.Add(local.Local);
            }
        }

        void Require(Symbol symbol, BoundExpression at) {
            if (Assigned.Add(symbol)) {
                // Added rather than merely tested, so one unassigned local is one diagnostic
                // however many times it is read.
                diagnostics.Add(
                    SemanticDiagnostics.UseOfUnassignedLocal,
                    at.Syntax.GetLocation(),
                    symbol.Name
                );
            }
        }

        /// <summary>The innermost expression an access chain starts from.</summary>
        static BoundExpression Root(BoundExpression expression) =>
            expression switch {
                BoundFieldExpression { Receiver: { } receiver } => Root(receiver),
                BoundArrayAccessExpression access => Root(access.Receiver),
                _ => expression
            };

        HashSet<Symbol> Snapshot() => [.. Assigned];

        void Restore(HashSet<Symbol> state) {
            Assigned.Clear();
            foreach (var symbol in state) {
                Assigned.Add(symbol);
            }
        }

        /// <summary>
        ///     Merges the then-arm's state into the current (else-arm) one: the intersection,
        ///     unless one arm cannot fall through, in which case the other one alone decides.
        /// </summary>
        void Merge(HashSet<Symbol> thenState, Exit thenExit, Exit elseExit) {
            if (thenExit != Exit.FallsThrough) {
                return;
            }

            if (elseExit != Exit.FallsThrough) {
                Restore(thenState);
                return;
            }

            Restore(Intersect(thenState, Assigned));
        }

        static HashSet<Symbol> Intersect(HashSet<Symbol> left, HashSet<Symbol> right) {
            var result = new HashSet<Symbol>(left);
            result.IntersectWith(right);
            return result;
        }

        /// <summary>How a whole <c>if</c> exits, given how each arm does.</summary>
        static Exit Combine(Exit left, Exit right) =>
            left == right ? left
            : left == Exit.FallsThrough || right == Exit.FallsThrough ? Exit.FallsThrough
            // Two different jumps: neither reaches the next statement, and leaving the function is
            // the one that matters to the caller — a `break` still leaves the loop reachable from
            // elsewhere. `return` outranks `discard` only so a message names what the caller can act
            // on; the two are the same claim about reachability.
            : left == Exit.Returns || right == Exit.Returns ? Exit.Returns
            : Leaves(left) || Leaves(right) ? Exit.Discards
            : Exit.Breaks;
    }
}
