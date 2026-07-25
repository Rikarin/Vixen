using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.IR;

/// <summary>
///     Checks that a module is internally consistent before a backend ever sees it:
///     values are defined once and only used where they are in scope, types line up
///     on every instruction, access chains are well formed, and control flow is sane.
///     A backend can then assume the IR is valid instead of re-checking it.
/// </summary>
public static class IrVerifier {
    /// <summary>Verifies a module, reporting <c>RVN3010</c> for each problem found.</summary>
    /// <returns>True when the module is well formed.</returns>
    public static bool Verify(IrModule module, DiagnosticBag diagnostics) {
        var before = diagnostics.ToArray().Length;

        foreach (var structType in module.Structs) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in structType.Fields) {
                if (!names.Add(field.Name)) {
                    Report(diagnostics, $"struct '{structType.Name}' declares '{field.Name}' more than once");
                }
            }
        }

        foreach (var function in module.AllFunctions) {
            new FunctionVerifier(function, diagnostics).Verify();
        }

        foreach (var shader in module.Shaders) {
            VerifyShader(shader, diagnostics);
        }

        return diagnostics.ToArray().Length == before;
    }

    static void VerifyShader(IrShader shader, DiagnosticBag diagnostics) {
        var slots = new HashSet<(IrBindingKind, int)>();

        foreach (var binding in shader.Bindings) {
            if (!slots.Add((binding.Kind, binding.Slot))) {
                Report(
                    diagnostics,
                    $"shader '{shader.Name}' reuses {binding.Kind} slot {binding.Slot} for '{binding.Name}'"
                );
            }
        }

        var stages = new HashSet<ShaderStage>();

        foreach (var entryPoint in shader.EntryPoints) {
            if (!stages.Add(entryPoint.Stage)) {
                Report(diagnostics, $"shader '{shader.Name}' has two {entryPoint.Stage} entry points");
            }

            if (!shader.Functions.Contains(entryPoint.Function)) {
                Report(
                    diagnostics,
                    $"entry point '{entryPoint.Function.Name}' is not a function of shader '{shader.Name}'"
                );
            }

            if (entryPoint.Inputs.Count != entryPoint.Function.Parameters.Count) {
                Report(
                    diagnostics,
                    $"entry point '{entryPoint.Function.Name}' declares {entryPoint.Inputs.Count} inputs "
                    + $"but takes {entryPoint.Function.Parameters.Count} parameters"
                );
            }
        }
    }

    static void Report(DiagnosticBag diagnostics, string message) =>
        diagnostics.Add(LoweringDiagnostics.MalformedIr, Location.None, message);

    /// <summary>Verifies one function; scoping rules make this a walk with a stack.</summary>
    sealed class FunctionVerifier(IrFunction function, DiagnosticBag diagnostics) {
        readonly HashSet<int> defined = [];
        readonly HashSet<int> everDefined = [];
        int loopDepth;

        public void Verify() {
            VerifyBlock(function.Body);

            if (!function.ReturnType.IsVoid && !AlwaysReturns(function.Body)) {
                Report($"function '{function.Name}' can finish without returning a {function.ReturnType.Name}");
            }
        }

        void VerifyBlock(IrBlock block) {
            // Values defined inside a block go out of scope when it closes —
            // structured control flow has no cross-branch dominance.
            List<int> introduced = [];

            foreach (var statement in block.Statements) {
                VerifyStatement(statement, introduced);
            }

            foreach (var id in introduced) {
                defined.Remove(id);
            }
        }

        void VerifyStatement(IrStatement statement, List<int> introduced) {
            switch (statement) {
                case IrBlock nested:
                    VerifyBlock(nested);
                    break;

                case IrInstruction instruction:
                    VerifyInstruction(instruction, introduced);
                    break;

                case IrIfStatement conditional:
                    RequireDefined(conditional.Condition);
                    RequireType(conditional.Condition, IrScalarType.Bool, "'if' condition");
                    VerifyBlock(conditional.Then);

                    if (conditional.Else is { } otherwise) {
                        VerifyBlock(otherwise);
                    }

                    break;

                case IrLoopStatement loop: {
                    // The condition's values stay live long enough to test them.
                    List<int> conditionValues = [];
                    foreach (var nested in loop.Condition.Statements) {
                        VerifyStatement(nested, conditionValues);
                    }

                    RequireDefined(loop.ConditionValue);
                    RequireType(loop.ConditionValue, IrScalarType.Bool, "loop condition");

                    loopDepth++;
                    VerifyBlock(loop.Body);

                    if (loop.Continue is { } step) {
                        VerifyBlock(step);
                    }

                    loopDepth--;

                    foreach (var id in conditionValues) {
                        defined.Remove(id);
                    }

                    break;
                }

                case IrReturnStatement @return:
                    if (@return.Value is { } value) {
                        RequireDefined(value);
                        RequireType(value, function.ReturnType, $"return from '{function.Name}'");
                    } else if (!function.ReturnType.IsVoid) {
                        Report($"'{function.Name}' returns {function.ReturnType.Name} but a return has no value");
                    }

                    break;

                case IrBreakStatement or IrContinueStatement:
                    if (loopDepth == 0) {
                        Report($"'{(statement is IrBreakStatement ? "break" : "continue")}' outside a loop");
                    }

                    break;
            }
        }

        void VerifyInstruction(IrInstruction instruction, List<int> introduced) {
            foreach (var operand in instruction.Operands) {
                RequireDefined(operand);
            }

            switch (instruction) {
                case IrLoadInstruction load:
                    VerifyPlace(load.Place);
                    RequireSame(load.Result.Type, load.Place.Type, "load result");
                    break;

                case IrStoreInstruction store:
                    VerifyPlace(store.Place);
                    RequireSame(store.Place.Type, store.Value.Type, "store");
                    break;

                case IrBinaryInstruction binary:
                    VerifyBinary(binary);
                    break;

                case IrUnaryInstruction unary:
                    RequireSame(unary.Result.Type, unary.Operand.Type, $"'{unary.Op}'");
                    break;

                case IrConvertInstruction convert:
                    if (convert.Result.Type.Equals(convert.Operand.Type)) {
                        Report($"{convert.Result} converts {convert.Operand.Type.Name} to itself");
                    }

                    break;

                case IrSelectInstruction select:
                    RequireType(select.Condition, IrScalarType.Bool, "'select' condition");
                    RequireSame(select.Result.Type, select.WhenTrue.Type, "'select' branches");
                    RequireSame(select.Result.Type, select.WhenFalse.Type, "'select' branches");
                    break;

                case IrCallInstruction call:
                    VerifyCall(call);
                    break;

                case IrConstructInstruction construct:
                    VerifyConstruct(construct);
                    break;

                case IrExtractInstruction extract: {
                    var type = extract.Source.Type;
                    foreach (var access in extract.Chain) {
                        type = access.ResultType(type);
                    }

                    RequireSame(extract.Result.Type, type, "'extract' result");
                    break;
                }
            }

            if (instruction.Result is not { } result) {
                return;
            }

            if (!everDefined.Add(result.Id)) {
                Report($"%{result.Id} is defined more than once in '{function.Name}'");
            }

            defined.Add(result.Id);
            introduced.Add(result.Id);
        }

        void VerifyBinary(IrBinaryInstruction binary) {
            switch (binary.Op) {
                case IrBinaryOp.MatrixMultiply:
                    // Shapes were checked when the operator was resolved; here
                    // only the component types have to agree.
                    if (!ReferenceEquals(binary.Left.Type.ComponentType, binary.Right.Type.ComponentType)) {
                        Report($"{binary.Result} multiplies {binary.Left.Type.Name} by {binary.Right.Type.Name}");
                    }

                    break;

                case IrBinaryOp.Equal
                    or IrBinaryOp.NotEqual
                    or IrBinaryOp.LessThan
                    or IrBinaryOp.LessThanOrEqual
                    or IrBinaryOp.GreaterThan
                    or IrBinaryOp.GreaterThanOrEqual:
                    RequireSame(binary.Left.Type, binary.Right.Type, $"'{binary.Op}' operands");

                    if (binary.Result.Type.ComponentType != IrScalarType.Bool) {
                        Report($"{binary.Result} compares but does not produce a boolean");
                    }

                    break;

                case IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.UnsignedShiftRight:
                    RequireSame(binary.Result.Type, binary.Left.Type, $"'{binary.Op}' result");
                    break;

                default:
                    RequireSame(binary.Left.Type, binary.Right.Type, $"'{binary.Op}' operands");
                    RequireSame(binary.Result.Type, binary.Left.Type, $"'{binary.Op}' result");
                    break;
            }
        }

        void VerifyCall(IrCallInstruction call) {
            if (call.Arguments.Count != call.Function.Parameters.Count) {
                Report(
                    $"call to '{call.Function.Name}' passes {call.Arguments.Count} arguments "
                    + $"but it takes {call.Function.Parameters.Count}"
                );
                return;
            }

            for (var i = 0; i < call.Arguments.Count; i++) {
                RequireSame(
                    call.Function.Parameters[i].Type,
                    call.Arguments[i].Type,
                    $"argument {i} of '{call.Function.Name}'"
                );
            }

            if (call.Result is { } result) {
                RequireSame(result.Type, call.Function.ReturnType, $"result of '{call.Function.Name}'");
            } else if (!call.Function.ReturnType.IsVoid) {
                Report($"call to '{call.Function.Name}' discards its {call.Function.ReturnType.Name} result");
            }
        }

        void VerifyConstruct(IrConstructInstruction construct) {
            switch (construct.Result.Type) {
                case IrVectorType vector: {
                    var lanes = construct.Arguments.Sum(a => a.Type switch {
                            IrVectorType part => part.Size,
                            _ => 1
                        }
                    );

                    if (lanes != vector.Size) {
                        Report($"{construct.Result} builds a {vector.Name} from {lanes} components");
                    }

                    break;
                }

                case IrStructType structType when construct.Arguments.Count != structType.Fields.Count:
                    Report(
                        $"{construct.Result} builds '{structType.Name}' from {construct.Arguments.Count} values "
                        + $"but it has {structType.Fields.Count} fields"
                    );
                    break;

                case IrArrayType array: {
                    foreach (var argument in construct.Arguments) {
                        RequireSame(array.Element, argument.Type, "array element");
                    }

                    break;
                }
            }
        }

        void VerifyPlace(IrPlace place) {
            var type = place.Root.Type;

            foreach (var access in place.Chain) {
                var next = access.ResultType(type);

                if (next.IsVoid && !type.IsVoid) {
                    Report($"access '{access}' is not valid on {type.Name} in '{function.Name}'");
                    return;
                }

                type = next;
            }
        }

        void RequireDefined(IrValue value) {
            if (!defined.Contains(value.Id)) {
                Report($"{value} is used in '{function.Name}' before it is defined, or outside its scope");
            }
        }

        void RequireType(IrValue value, IrType expected, string what) {
            if (!value.Type.Equals(expected)) {
                Report($"{what} is {value.Type.Name}, expected {expected.Name}");
            }
        }

        void RequireSame(IrType left, IrType right, string what) {
            if (!left.Equals(right)) {
                Report($"{what}: {left.Name} does not match {right.Name}");
            }
        }

        /// <summary>Whether control definitely leaves the block via a return.</summary>
        static bool AlwaysReturns(IrBlock block) {
            foreach (var statement in block.Statements) {
                switch (statement) {
                    case IrReturnStatement:
                        return true;
                    case IrIfStatement { Else: { } otherwise } conditional
                        when AlwaysReturns(conditional.Then) && AlwaysReturns(otherwise):
                        return true;
                    case IrBlock nested when AlwaysReturns(nested):
                        return true;
                }
            }

            return false;
        }

        void Report(string message) => IrVerifier.Report(diagnostics, message);
    }
}
