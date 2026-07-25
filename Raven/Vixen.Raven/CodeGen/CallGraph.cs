using Vixen.Raven.IR;

namespace Vixen.Raven.CodeGen;

/// <summary>
///     Which functions an entry point can actually reach. Every backend needs this:
///     a translation unit is one stage, so emitting another stage's functions would
///     be dead code — and dead code that refers to the wrong stage's built-ins.
/// </summary>
public static class CallGraph {
    /// <summary>The functions called anywhere inside a statement, nested ones included.</summary>
    public static IEnumerable<IrFunction> Calls(IrStatement statement) {
        switch (statement) {
            case IrCallInstruction call:
                yield return call.Function;
                break;

            case IrBlock block: {
                foreach (var call in block.Statements.SelectMany(Calls)) {
                    yield return call;
                }

                break;
            }

            case IrIfStatement conditional: {
                foreach (var call in Calls(conditional.Then)) {
                    yield return call;
                }

                if (conditional.Else is { } otherwise) {
                    foreach (var call in Calls(otherwise)) {
                        yield return call;
                    }
                }

                break;
            }

            case IrLoopStatement loop: {
                IrBlock?[] parts = [loop.Condition, loop.Body, loop.Continue];

                foreach (var call in parts.Where(p => p is not null).SelectMany(p => Calls(p!))) {
                    yield return call;
                }

                break;
            }
        }
    }

    /// <summary>Everything reachable from an entry point, itself included.</summary>
    public static HashSet<IrFunction> Reachable(IrFunction entry) {
        HashSet<IrFunction> reached = [];
        Queue<IrFunction> pending = new();
        pending.Enqueue(entry);

        while (pending.Count > 0) {
            var function = pending.Dequeue();
            if (!reached.Add(function)) {
                continue;
            }

            foreach (var call in Calls(function.Body)) {
                pending.Enqueue(call);
            }
        }

        return reached;
    }

    /// <summary>
    ///     Reachable functions with every callee before its callers. Targets that
    ///     cannot forward-reference a function need this; the language has no
    ///     recursion, but the visited set keeps a cycle from looping forever.
    /// </summary>
    public static IReadOnlyList<IrFunction> InCallOrder(IrFunction entry) {
        List<IrFunction> ordered = [];
        HashSet<IrFunction> visited = [];

        void Visit(IrFunction function) {
            if (!visited.Add(function)) {
                return;
            }

            foreach (var callee in Calls(function.Body)) {
                Visit(callee);
            }

            ordered.Add(function);
        }

        Visit(entry);
        return ordered;
    }
}
