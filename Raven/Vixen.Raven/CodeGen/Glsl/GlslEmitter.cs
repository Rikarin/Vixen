// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.CodeGen.Glsl;

/// <summary>
///     Emits one Vulkan-flavoured GLSL translation unit for one entry point.
/// </summary>
/// <remarks>
///     <para>
///         The IR is already SSA with structured control flow, so emission is close to a
///         transcription: each instruction becomes one statement declaring one local.
///         Constants are the exception — they are pure, so they are inlined at every use
///         rather than given a name, which removes most of the noise.
///     </para>
///     <para>
///         Vulkan GLSL rather than desktop GLSL: <c>#version 450</c>, an explicit
///         <c>set</c> and <c>binding</c> on every descriptor, <c>location</c> on every stage
///         in/out, explicit <c>std140</c>, and separate <c>texture2D</c>/<c>sampler</c>
///         objects. That is not decoration. It is what lets <c>shaderc</c> compile this back
///         to SPIR-V that binds the same way Raven's own SPIR-V does, which is the
///         differential oracle in docs/plan/07 § C — and it is also the most useful form to
///         read in a frame debugger.
///     </para>
///     <para>
///         One thing GLSL still cannot mirror: a uniform cannot carry an initializer, so a
///         binding's declared default stays host-side metadata on
///         <see cref="IrShader.Initializer" />.
///     </para>
/// </remarks>
sealed class GlslEmitter {
    /// <summary>Output semantics GLSL routes to a built-in rather than a variable.</summary>
    static readonly HashSet<string> PositionSemantics = new(StringComparer.OrdinalIgnoreCase) {
        "SV_Position", "POSITION"
    };

    readonly DiagnosticBag diagnostics;
    readonly IrEntryPoint entryPoint;
    readonly Dictionary<IrFunction, string> functionNames = [];
    readonly HashSet<string> globalNames = new(StringComparer.Ordinal);
    readonly IrModule module;
    readonly GlslOptions options;
    readonly IrShader shader;
    readonly List<string> inputNames = [];
    readonly Dictionary<IrVariable, string> variableNames = [];
    readonly Dictionary<int, string> values = [];
    readonly Writer writer = new();

    int loopCounter;
    string? outputName;
    bool samplerlessFetch;

    internal GlslEmitter(
        IrModule module,
        IrShader shader,
        IrEntryPoint entryPoint,
        GlslOptions options,
        DiagnosticBag diagnostics
    ) {
        this.module = module;
        this.shader = shader;
        this.entryPoint = entryPoint;
        this.options = options;
        this.diagnostics = diagnostics;
    }

    // --- Declarations ------------------------------------------------------

    void EmitStructs() {
        if (module.Structs.Count == 0) {
            return;
        }

        foreach (var structType in module.Structs) {
            writer.Line($"struct {GlslTypes.Identifier(structType.Name)} {{");
            writer.Indent();

            foreach (var field in structType.Fields) {
                writer.Line(Declare(field.Type, GlslTypes.Identifier(field.Name), field.Name) + ";");
            }

            writer.Outdent();
            writer.Line("};");
            writer.Blank();
        }
    }

    /// <summary>
    ///     Declares the bindings, each with the explicit <c>set</c> and <c>binding</c> that
    ///     <see cref="BindingPlan" /> assigned.
    /// </summary>
    /// <remarks>
    ///     The plan is shared with the SPIR-V backend and with the reflection, which is what
    ///     makes the two outputs bind identically — and is the precondition for compiling this
    ///     GLSL back to SPIR-V and diffing the two.
    /// </remarks>
    void EmitBindings() {
        var opaque = false;

        foreach (var planned in BindingPlan.Of(shader)) {
            var layout = $"set = {(int)planned.Set}, binding = {planned.Binding}";

            if (planned.Resource is { } resource) {
                var name = ReserveVariable(resource.Variable);
                writer.Line(
                    $"layout({layout}) uniform {Declare(resource.Type, name, resource.Name)};"
                    + Comment(resource.Semantic)
                );

                opaque = true;
                continue;
            }

            if (opaque) {
                // A set's block comes before its resources, so this only happens between
                // sets; the blank line keeps them visually apart.
                writer.Blank();
                opaque = false;
            }

            writer.Line($"layout(std140, {layout}) uniform {Reserve(planned.Name)} {{");
            writer.Indent();

            foreach (var uniform in planned.Members) {
                var name = ReserveVariable(uniform.Variable);
                writer.Line(Declare(uniform.Type, name, uniform.Name) + ";" + Comment(uniform.Semantic));
            }

            writer.Outdent();
            writer.Line("};");
            writer.Blank();
        }

        if (opaque) {
            writer.Blank();
        }

        if (shader.Initializer.Statements.Count > 0) {
            writer.Line("// Binding defaults are host-side data: a GLSL uniform cannot be initialized here.");
            writer.Blank();
        }
    }

    void EmitStageInterface() {
        var location = 0;

        foreach (var input in entryPoint.Inputs) {
            var name = Reserve("in_" + input.Name);
            inputNames.Add(name);
            writer.Line(
                $"layout(location = {location++}) in {Declare(input.Type, name, input.Name)};"
                + Comment(input.Semantic)
            );
        }

        if (entryPoint.Inputs.Count > 0) {
            writer.Blank();
        }

        if (OutputGoesToBuiltIn()) {
            return;
        }

        if (entryPoint.Output is { } output) {
            outputName = Reserve("out_" + output.Name);
            writer.Line(
                $"layout(location = 0) out {Declare(output.Type, outputName, output.Name)};"
                + Comment(output.Semantic)
            );
            writer.Blank();
        }
    }

    /// <summary>
    ///     True when the stage's result belongs in a built-in rather than an
    ///     <c>out</c> variable — a vertex position, in practice.
    /// </summary>
    bool OutputGoesToBuiltIn() =>
        entryPoint.Stage == ShaderStage.Vertex
        && entryPoint.Output is { } output
        && (output.Semantic is null || PositionSemantics.Contains(output.Semantic))
        && output.Type is IrVectorType { Size: 4, Component.Kind: IrTypeKind.Float };

    // --- Functions ---------------------------------------------------------

    void EmitFunctions() {
        var functions = Reachable().ToArray();

        foreach (var function in functions) {
            functionNames[function] = Reserve(function.Name);
        }

        // Declare everything first so call order never matters.
        if (functions.Length > 1) {
            foreach (var function in functions) {
                writer.Line(Signature(function) + ";");
            }

            writer.Blank();
        }

        foreach (var function in functions) {
            EmitFunction(function);
        }
    }

    /// <summary>
    ///     The entry point and everything it calls, in module order. A GLSL unit is
    ///     one stage, so emitting another stage's functions would be dead code — and
    ///     dead code that references the wrong stage's built-ins.
    /// </summary>
    /// <remarks>
    ///     Reachability is what excludes other stages, not shader membership: a
    ///     <c>compose</c> slot puts the implementation's functions in a different shader,
    ///     and filtering to this shader's own list would drop the very function the entry
    ///     point calls.
    /// </remarks>
    IEnumerable<IrFunction> Reachable() {
        var reached = CallGraph.Reachable(entryPoint.Function);
        return module.AllFunctions.Where(reached.Contains);
    }

    string Signature(IrFunction function) {
        var parameters = function.Parameters
            .Select(p => Declare(p.Type, LocalName(function, p), p.Name))
            .ToArray();

        var returnType = GlslTypes.Name(function.ReturnType) ?? Unsupported(function.ReturnType, function.Name);
        return $"{returnType} {functionNames[function]}({string.Join(", ", parameters)})";
    }

    void EmitFunction(IrFunction function) {
        values.Clear();

        writer.Line(Signature(function) + " {");
        writer.Indent();

        foreach (var local in function.Locals) {
            writer.Line(Declare(local.Type, LocalName(function, local), local.Name) + ";");
        }

        if (function.Locals.Count > 0) {
            writer.Blank();
        }

        EmitBlock(function.Body);

        writer.Outdent();
        writer.Line("}");
        writer.Blank();
    }

    void EmitMain() {
        writer.Line("void main() {");
        writer.Indent();

        // Stage inputs are globals declared alongside the entry point; main just
        // threads them into the user's function.
        var call = $"{functionNames[entryPoint.Function]}({string.Join(", ", inputNames)})";

        if (entryPoint.Output is null) {
            writer.Line(call + ";");
        } else if (OutputGoesToBuiltIn()) {
            writer.Line($"gl_Position = {call};");
        } else {
            writer.Line($"{outputName} = {call};");
        }

        writer.Outdent();
        writer.Line("}");
    }

    // --- Statements --------------------------------------------------------

    void EmitBlock(IrBlock block) {
        foreach (var statement in block.Statements) {
            EmitStatement(statement);
        }
    }

    void EmitStatement(IrStatement statement) {
        switch (statement) {
            case IrBlock block:
                EmitBlock(block);
                break;

            case IrInstruction instruction:
                EmitInstruction(instruction);
                break;

            case IrIfStatement conditional:
                writer.Line($"if ({Value(conditional.Condition)}) {{");
                writer.Indent();
                EmitBlock(conditional.Then);
                writer.Outdent();

                if (conditional.Else is { } otherwise) {
                    writer.Line("} else {");
                    writer.Indent();
                    EmitBlock(otherwise);
                    writer.Outdent();
                }

                writer.Line("}");
                break;

            case IrLoopStatement loop:
                EmitLoop(loop);
                break;

            case IrReturnStatement @return:
                writer.Line(@return.Value is { } value ? $"return {Value(value)};" : "return;");
                break;

            case IrBreakStatement:
                writer.Line("break;");
                break;

            case IrContinueStatement:
                writer.Line("continue;");
                break;
        }
    }

    /// <summary>
    ///     Emits a structured loop as <c>while (true)</c> with an explicit exit.
    /// </summary>
    /// <remarks>
    ///     A loop with a step, or one that tests after the body, hoists that work to
    ///     the top of the iteration behind a first-time flag. That is what makes
    ///     <c>continue</c> land in the right place: GLSL's <c>continue</c> jumps to
    ///     the top of the loop body, so anything that must run before the next test
    ///     has to live there.
    /// </remarks>
    void EmitLoop(IrLoopStatement loop) {
        var needsFlag = loop.Continue is not null || !loop.TestBeforeBody;
        var flag = needsFlag ? Reserve($"_loop{loopCounter++}_first") : null;

        if (flag is not null) {
            writer.Line($"bool {flag} = true;");
        }

        writer.Line("while (true) {");
        writer.Indent();

        if (flag is not null) {
            writer.Line($"if ({flag}) {{");
            writer.Indent();
            writer.Line($"{flag} = false;");
            writer.Outdent();
            writer.Line("} else {");
            writer.Indent();

            if (loop.Continue is { } step) {
                EmitBlock(step);
            }

            if (!loop.TestBeforeBody) {
                EmitCondition(loop);
            }

            writer.Outdent();
            writer.Line("}");
        }

        if (loop.TestBeforeBody) {
            EmitCondition(loop);
        }

        EmitBlock(loop.Body);

        writer.Outdent();
        writer.Line("}");
    }

    void EmitCondition(IrLoopStatement loop) {
        EmitBlock(loop.Condition);
        writer.Line($"if (!({Value(loop.ConditionValue)})) {{");
        writer.Indent();
        writer.Line("break;");
        writer.Outdent();
        writer.Line("}");
    }

    // --- Instructions ------------------------------------------------------

    void EmitInstruction(IrInstruction instruction) {
        switch (instruction) {
            case IrConstantInstruction constant:
                // Constants are pure, so they inline at every use instead of
                // taking a name.
                values[constant.Result.Id] = FormatConstant(constant.Value, constant.Result.Type);
                return;

            case IrStoreInstruction store:
                writer.Line($"{Place(store.Place)} = {Value(store.Value)};");
                return;

            case IrCallInstruction { Result: null } call:
                writer.Line($"{functionNames[call.Function]}({Arguments(call.Arguments)});");
                return;
        }

        if (instruction.Result is not { } result) {
            return;
        }

        // GLSL forbids locals of opaque type, so a texture or sampler value is
        // never materialized: uses refer straight back to the uniform.
        if (result.Type is IrSamplerType or IrTextureType) {
            values[result.Id] = instruction is IrLoadInstruction opaque ? Place(opaque.Place) : "/* opaque */";
            return;
        }

        var expression = Expression(instruction);
        var declaration = Declare(result.Type, Name(result), $"%{result.Id}");
        writer.Line($"{declaration} = {expression};");
    }

    string Expression(IrInstruction instruction) {
        switch (instruction) {
            case IrLoadInstruction load:
                return Place(load.Place);

            case IrUnaryInstruction unary:
                return UnaryExpression(unary);

            case IrBinaryInstruction binary:
                return BinaryExpression(binary);

            case IrConvertInstruction convert:
                // A GLSL constructor is both the numeric conversion and the splat.
                return $"{TypeName(convert.Result.Type)}({Value(convert.Operand)})";

            case IrIntrinsicInstruction intrinsic: {
                if (intrinsic.Intrinsic == IrIntrinsic.LoadTexture) {
                    // texelFetch on a separate texture, with no sampler to pair it with,
                    // is what this extension adds. Recorded here so the prologue declares
                    // it only in the units that need it.
                    samplerlessFetch = true;
                }

                var arguments = intrinsic.Arguments.Select(Value).ToArray();
                var call = GlslIntrinsics.Call(
                    intrinsic.Intrinsic,
                    arguments,
                    [.. intrinsic.Arguments.Select(a => a.Type)],
                    TypeName(intrinsic.Result!.Type)
                );

                if (call is not null) {
                    return call;
                }

                Report(BackendDiagnostics.NotImplemented, $"The '{intrinsic.Intrinsic}' intrinsic");
                return "0";
            }

            case IrCallInstruction call:
                return $"{functionNames[call.Function]}({Arguments(call.Arguments)})";

            case IrConstructInstruction construct:
                return $"{TypeName(construct.Result.Type)}({Arguments(construct.Arguments)})";

            case IrExtractInstruction extract:
                return Value(extract.Source) + Chain(extract.Source.Type, extract.Chain);

            case IrSelectInstruction select:
                return $"{Value(select.Condition)} ? {Value(select.WhenTrue)} : {Value(select.WhenFalse)}";

            default:
                Report(BackendDiagnostics.NotImplemented, instruction.GetType().Name);
                return "0";
        }
    }

    string UnaryExpression(IrUnaryInstruction unary) {
        var operand = Value(unary.Operand);

        return unary.Op switch {
            IrUnaryOp.Negate => $"-{operand}",
            // GLSL spells boolean negation of a vector as a function.
            IrUnaryOp.Not when unary.Operand.Type is IrVectorType => $"not({operand})",
            IrUnaryOp.Not => $"!{operand}",
            _ => $"~{operand}"
        };
    }

    string BinaryExpression(IrBinaryInstruction binary) {
        var left = Value(binary.Left);
        var right = Value(binary.Right);

        // Comparing vectors is a function in GLSL, and it yields a bvec.
        if (binary.Result.Type is IrVectorType && ComparisonFunction(binary.Op) is { } function) {
            return $"{function}({left}, {right})";
        }

        var op = binary.Op switch {
            IrBinaryOp.Add => "+",
            IrBinaryOp.Subtract => "-",
            IrBinaryOp.Multiply or IrBinaryOp.MatrixMultiply => "*",
            IrBinaryOp.Divide => "/",
            IrBinaryOp.Modulo => "%",
            IrBinaryOp.ShiftLeft => "<<",
            IrBinaryOp.ShiftRight or IrBinaryOp.UnsignedShiftRight => ">>",
            IrBinaryOp.BitwiseAnd => "&",
            IrBinaryOp.BitwiseOr => "|",
            IrBinaryOp.BitwiseXor => "^",
            IrBinaryOp.LogicalAnd => "&&",
            IrBinaryOp.LogicalOr => "||",
            IrBinaryOp.Equal => "==",
            IrBinaryOp.NotEqual => "!=",
            IrBinaryOp.LessThan => "<",
            IrBinaryOp.LessThanOrEqual => "<=",
            IrBinaryOp.GreaterThan => ">",
            _ => ">="
        };

        return $"({left} {op} {right})";
    }

    static string? ComparisonFunction(IrBinaryOp op) =>
        op switch {
            IrBinaryOp.Equal => "equal",
            IrBinaryOp.NotEqual => "notEqual",
            IrBinaryOp.LessThan => "lessThan",
            IrBinaryOp.LessThanOrEqual => "lessThanEqual",
            IrBinaryOp.GreaterThan => "greaterThan",
            IrBinaryOp.GreaterThanOrEqual => "greaterThanEqual",
            _ => null
        };

    string Arguments(IReadOnlyList<IrValue> arguments) => string.Join(", ", arguments.Select(Value));

    // --- Places, values and names ------------------------------------------

    string Place(IrPlace place) => VariableName(place.Root) + Chain(place.Root.Type, place.Chain);

    string Chain(IrType rootType, IReadOnlyList<IrAccess> chain) {
        var builder = new StringBuilder();
        var type = rootType;

        foreach (var access in chain) {
            switch (access) {
                case IrFieldAccess field when type is IrStructType structType
                    && field.Index < structType.Fields.Count:
                    builder.Append('.').Append(GlslTypes.Identifier(structType.Fields[field.Index].Name));
                    break;

                case IrIndexAccess index:
                    builder.Append('[').Append(Value(index.Index)).Append(']');
                    break;

                case IrSwizzleAccess swizzle:
                    builder.Append('.').Append(string.Concat(swizzle.Components.Select(c => "xyzw"[c])));
                    break;
            }

            type = access.ResultType(type);
        }

        return builder.ToString();
    }

    string Value(IrValue value) => values.GetValueOrDefault(value.Id) ?? Name(value);

    static string Name(IrValue value) => "_" + value.Id;

    string VariableName(IrVariable variable) => variableNames.GetValueOrDefault(variable, variable.Name);

    /// <summary>Reserves the mangled form of a global name, keeping it unique.</summary>
    string Reserve(string name) {
        var candidate = GlslTypes.Identifier(name);
        var suffix = 1;
        while (!globalNames.Add(candidate)) {
            candidate = GlslTypes.Identifier(name) + suffix++;
        }

        return candidate;
    }

    string ReserveVariable(IrVariable variable) {
        var name = Reserve(variable.Name);
        variableNames[variable] = name;
        return name;
    }

    /// <summary>
    ///     Names a function-scoped variable. Mangling can make two distinct IR names
    ///     collide, so uniqueness is re-established per function.
    /// </summary>
    string LocalName(IrFunction function, IrVariable variable) {
        if (variableNames.TryGetValue(variable, out var existing)) {
            return existing;
        }

        HashSet<string> taken = new(StringComparer.Ordinal);
        foreach (var other in function.Parameters.Concat(function.Locals)) {
            if (ReferenceEquals(other, variable)) {
                break;
            }

            taken.Add(variableNames.GetValueOrDefault(other, GlslTypes.Identifier(other.Name)));
        }

        var candidate = GlslTypes.Identifier(variable.Name);
        var suffix = 1;
        while (taken.Contains(candidate) || globalNames.Contains(candidate)) {
            candidate = GlslTypes.Identifier(variable.Name) + suffix++;
        }

        variableNames[variable] = candidate;
        return candidate;
    }

    // --- Types and constants -----------------------------------------------

    string TypeName(IrType type) => GlslTypes.Name(type) ?? Unsupported(type, type.Name);

    string Declare(IrType type, string name, string what) =>
        GlslTypes.Declare(type, name) ?? $"{Unsupported(type, what)} {name}";

    string Unsupported(IrType type, string what) {
        Report(
            BackendDiagnostics.NotExpressible,
            type is IrArrayType { Length: null }
                ? $"The unsized array type of '{what}'"
                : $"The type '{type.Name}' of '{what}'"
        );

        return "float";
    }

    void Report(DiagnosticDescriptor descriptor, string subject) =>
        diagnostics.Add(descriptor, Location.None, subject, "GLSL");

    static string Comment(string? semantic) => semantic is null ? string.Empty : $"  // {semantic}";

    /// <summary>Renders a constant in GLSL's syntax, including its suffix rules.</summary>
    static string FormatConstant(object? value, IrType type) {
        if (value is null) {
            return Zero(type);
        }

        return value switch {
            bool flag => flag ? "true" : "false",
            int number => number.ToString(CultureInfo.InvariantCulture),
            uint number => number.ToString(CultureInfo.InvariantCulture) + "u",
            float number => Real(number.ToString("R", CultureInfo.InvariantCulture)),
            // GLSL needs the `lf` suffix to make a literal double rather than float.
            double number => Real(number.ToString("R", CultureInfo.InvariantCulture)) + "lf",
            _ => Zero(type)
        };
    }

    /// <summary>GLSL needs a decimal point to read a literal as floating point.</summary>
    static string Real(string text) =>
        text.Contains('.') || text.Contains('e') || text.Contains('E') || text.Contains("Infinity")
            ? text
            : text + ".0";

    static string Zero(IrType type) =>
        type.Kind switch {
            IrTypeKind.Bool => "false",
            IrTypeKind.Int => "0",
            IrTypeKind.UInt => "0u",
            IrTypeKind.Float => "0.0",
            IrTypeKind.Double => "0.0lf",
            _ => $"{GlslTypes.Name(type) ?? "float"}(0.0)"
        };

    /// <summary>Emits the whole unit.</summary>
    /// <remarks>
    ///     The body is emitted first and the prologue prepended, because which extensions the
    ///     unit requires is only known once the body has been walked. Declaring an extension
    ///     a unit does not use is not harmless: a driver may reject it.
    /// </remarks>
    internal string Emit() {
        EmitStructs();
        EmitBindings();
        EmitStageInterface();
        EmitFunctions();
        EmitMain();

        return Prologue() + writer;
    }

    /// <summary>
    ///     The version, the extensions the body turned out to need, and a line saying where
    ///     this came from.
    /// </summary>
    string Prologue() {
        var prologue = new Writer();
        prologue.Line($"#version {options.Version}");

        if (samplerlessFetch) {
            prologue.Line("#extension GL_EXT_samplerless_texture_functions : require");
        }

        prologue.Blank();
        prologue.Line($"// Generated by Raven from shader '{shader.Name}' ({entryPoint.Stage} stage).");
        prologue.Line("// Vulkan GLSL: explicit sets and bindings, separate textures and samplers.");
        prologue.Blank();

        return prologue.ToString();
    }

    /// <summary>Indent-tracking line writer, so the emitters stay declarative.</summary>
    sealed class Writer {
        readonly StringBuilder builder = new();
        int indent;

        public void Indent() => indent++;
        public void Outdent() => indent--;

        public void Line(string text) => builder.Append(' ', indent * 4).Append(text).Append('\n');

        public void Blank() => builder.Append('\n');

        public override string ToString() => builder.ToString();
    }
}
