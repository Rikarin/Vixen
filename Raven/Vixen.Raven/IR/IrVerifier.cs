// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;

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

        // A shared binding is one resource named by however many features declared it, so two
        // declarations of one name that disagree about kind or set are not one resource at all —
        // and collapsing them to whichever came first would compile a feature against something it
        // did not declare.
        foreach (var group in shader.Bindings.Where(b => b.IsShared).GroupBy(b => b.Name, StringComparer.Ordinal)) {
            var shapes = group.Select(b => (b.Kind, b.Set)).Distinct().ToArray();

            if (shapes.Length > 1) {
                diagnostics.Add(
                    LoweringDiagnostics.SharedBindingsDisagree,
                    Location.None,
                    group.Key,
                    shader.Name,
                    string.Join(", ", shapes.Select(shape => $"{shape.Kind} in {shape.Set}"))
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

            VerifyWorkgroupSize(entryPoint, diagnostics);
        }
    }

    /// <summary>
    ///     A compute entry point carries a usable workgroup size and no other stage carries one.
    /// </summary>
    /// <remarks>
    ///     Checked here as well as in the binder because both backends read it unconditionally: a
    ///     compute stage that reached lowering without one would emit <c>local_size_x = 0</c>,
    ///     which is a shader that fails to link rather than a compiler that said why.
    /// </remarks>
    static void VerifyWorkgroupSize(IrEntryPoint entryPoint, DiagnosticBag diagnostics) {
        var name = entryPoint.Function.Name;

        if (entryPoint.Stage != ShaderStage.Compute) {
            if (entryPoint.WorkgroupSize is not null) {
                Report(diagnostics, $"{entryPoint.Stage} entry point '{name}' carries a workgroup size");
            }

            return;
        }

        if (entryPoint.WorkgroupSize is not { } size) {
            Report(diagnostics, $"compute entry point '{name}' has no workgroup size");
            return;
        }

        if (size.IsInvalid) {
            Report(diagnostics, $"compute entry point '{name}' has workgroup size {size}");
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

                case IrAtomicInstruction atomic:
                    VerifyPlace(atomic.Place);

                    // Scalar integers only. Both targets stop there — a float atomic needs an
                    // extension in GLSL and a capability in SPIR-V, and neither has one on a vector
                    // at all — so anything wider reaching a backend would have no instruction.
                    if (atomic.Place.Type is not IrScalarType { Kind: IrTypeKind.Int or IrTypeKind.UInt }) {
                        Report($"{atomic.Result} is an atomic on {atomic.Place.Type.Name}, which is not a scalar integer");
                    }

                    RequireSame(atomic.Place.Type, atomic.Value.Type, $"atomic '{atomic.Op}' operand");
                    RequireSame(atomic.Place.Type, atomic.Result.Type, $"atomic '{atomic.Op}' result");

                    if (atomic.Comparand is { } comparand) {
                        RequireSame(atomic.Place.Type, comparand.Type, "atomic comparand");
                    } else if (atomic.Op == IrAtomicOp.CompareExchange) {
                        Report($"{atomic.Result} is a compare-exchange with nothing to compare against");
                    }

                    break;

                case IrArrayLengthInstruction length:
                    VerifyPlace(length.Place);

                    // Only a runtime-sized array: a sized one folds to a constant in the binder, so
                    // one reaching here means the fold was skipped and a backend would have had to
                    // invent an answer.
                    if (length.Place.Type is not IrArrayType { Length: null }) {
                        Report($"{length.Result} takes the length of {length.Place.Type.Name}, which is not a buffer");
                    }

                    RequireSame(IrScalarType.Int, length.Result.Type, "array length result");
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
                var parameter = call.Function.Parameters[i];
                var argument = call.Arguments[i];

                RequireSame(parameter.Type, argument.Type, $"argument {i} of '{call.Function.Name}'");

                // Direction has to agree, in both directions. A value passed to a by-reference
                // parameter loses the callee's write; a reference passed to a by-value one is a
                // pointer where SPIR-V expects a value and would not survive the validator.
                if (parameter.IsByReference != argument.IsByReference) {
                    Report(
                        $"argument {i} of '{call.Function.Name}' is passed "
                        + $"{(argument.IsByReference ? "by reference" : "by value")} but the parameter is "
                        + $"{(parameter.IsByReference ? "by reference" : "by value")}"
                    );
                }

                // Copy-in/copy-out needs somewhere to copy from and to. The lowerer always uses a
                // function-scoped temp, so anything else here means the IR was hand-built or a
                // library was decoded wrongly.
                if (argument.IsByReference && argument.Reference!.Kind != IrVariableKind.Local) {
                    Report(
                        $"argument {i} of '{call.Function.Name}' passes {argument.Reference.Kind} "
                        + $"'{argument.Reference.Name}' by reference; only a local can be"
                    );
                }
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

        /// <summary>Whether control definitely leaves the block without running off its end.</summary>
        /// <remarks>
        ///     A <c>discard</c> counts, and that is not a special case bolted on: the question this
        ///     answers is whether a caller could ever observe a value that was never produced, and
        ///     an invocation that has ended has no caller left to observe anything.
        /// </remarks>
        static bool AlwaysReturns(IrBlock block) {
            foreach (var statement in block.Statements) {
                switch (statement) {
                    case IrReturnStatement or IrDiscardStatement:
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
