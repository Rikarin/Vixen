// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;

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

    /// <summary>
    ///     The <c>in</c> and <c>out</c> variable a stream resolves to, by direction.
    /// </summary>
    /// <remarks>
    ///     Two names for one IR variable, because GLSL splits what the IR does not: an <c>in</c> is
    ///     read-only and an <c>out</c> write-only, so a load resolves to one and a store to the
    ///     other. A stream a stage both reads and writes has both, which is legal — a stage's input
    ///     and output locations are separate namespaces.
    /// </remarks>
    readonly Dictionary<IrVariable, string> streamReads = [];

    readonly Dictionary<IrVariable, string> streamWrites = [];

    int loopCounter;
    string? outputName;
    bool samplerlessFetch;

    /// <summary>
    ///     The function being emitted, for naming a by-reference argument's variable — a name is
    ///     only unique within one function, so the lookup needs to know which.
    /// </summary>
    IrFunction? currentFunction;

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
            // GLSL has no empty struct — `struct S { };` is a syntax error — and a field-less
            // struct is exactly how Raven spells a namespace of free functions, which is what every
            // file in `Raven/Library` is. Nothing can reference the type as a value, since it has no
            // members to reach and nothing constructs one, so dropping the declaration loses
            // nothing. SPIR-V is unaffected: it emits a type only where one is used.
            if (structType.Fields.Count == 0) {
                continue;
            }

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
    ///     Declares a storage buffer: a block of its own holding one unsized array.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>std430</c> rather than <c>std140</c>, which is the whole reason a storage buffer is a
    ///         different thing from a uniform block and not merely a bigger one: an array of
    ///         <c>float</c> costs four bytes per element instead of sixteen, so a host-side
    ///         <c>Particle[]</c> uploads as a straight memcpy.
    ///     </para>
    ///     <para>
    ///         The array is unsized, which is legal exactly here — as a storage block's last member —
    ///         and nowhere else. That is what lets the host decide the element count, and what makes
    ///         <c>data.length()</c> a run-time question with a real answer.
    ///     </para>
    /// </remarks>
    void EmitStorageBuffer(IrBinding buffer, string layout) {
        var name = ReserveVariable(buffer.Variable);
        var access = buffer.IsWritable ? string.Empty : "readonly ";

        // The block needs a name of its own: GLSL scopes an interface block's members into the
        // enclosing scope, so the block name is only ever seen by a frame debugger — but two
        // unnamed blocks in one shader would collide.
        writer.Line($"layout(std430, {layout}) {access}buffer {Reserve(buffer.Name + "Block")} {{");
        writer.Indent();
        writer.Line(DeclareRuntime(buffer.Type, name, buffer.Name) + ";" + Comment(buffer.Semantic));
        writer.Outdent();
        writer.Line("};");
        writer.Blank();
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

            if (planned.Kind == IrBindingKind.StorageBuffer && planned.Resource is { } buffer) {
                EmitStorageBuffer(buffer, layout);
                opaque = false;
                continue;
            }

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

    /// <summary>
    ///     Declares the stage interface: the streams this stage reads and writes, then its own
    ///     parameters and return value.
    /// </summary>
    /// <remarks>
    ///     Every location comes from <see cref="StreamPlan" />, which is what makes the vertex
    ///     stage's outputs line up with the fragment stage's inputs — neither emitter and neither
    ///     stage decides a number for itself.
    /// </remarks>
    void EmitStageInterface() {
        if (entryPoint.Stage == ShaderStage.Compute) {
            EmitComputeInterface();
            return;
        }

        EmitStreamInterface();

        var location = StreamPlan.ParameterBase(shader);

        foreach (var input in entryPoint.Inputs) {
            RequireCarryable(input, true);

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
            RequireCarryable(output, false);

            outputName = Reserve("out_" + output.Name);
            writer.Line(
                $"layout(location = {StreamPlan.OutputBase(shader, entryPoint.Stage)}) out "
                + $"{Declare(output.Type, outputName, output.Name)};"
                + Comment(output.Semantic)
            );
            writer.Blank();
        }
    }

    /// <summary>
    ///     Refuses a stage input or output GLSL cannot declare.
    /// </summary>
    /// <remarks>
    ///     Through <see cref="StageInterface" />, which the SPIR-V backend reads too. This check was
    ///     missing here: an aggregate output emitted <c>out SomeStruct</c>, which GLSL has no such
    ///     thing as, so <c>glslc</c> rejected the unit while SPIR-V had already reported
    ///     <c>RVN4001</c> for the same shader. One backend noticing and the other not is the shape
    ///     worth removing, not just the message.
    /// </remarks>
    void RequireCarryable(IrStageIo io, bool isInput) {
        if (!StageInterface.CanCarry(io.Type)) {
            diagnostics.Add(
                BackendDiagnostics.NotExpressible,
                Location.None,
                StageInterface.Describe(io.Type, io.Name, isInput),
                "GLSL"
            );
        }
    }

    /// <summary>
    ///     Declares the workgroup size, and resolves each parameter to the GLSL built-in its
    ///     semantic names.
    /// </summary>
    /// <remarks>
    ///     A compute stage has no locations to assign: nothing feeds its parameters from a vertex
    ///     buffer and nothing takes a result, so there is no <c>in</c> or <c>out</c> to declare.
    ///     Each parameter is a built-in GLSL already provides, so <c>main</c> passes the built-in
    ///     straight through — which is why <see cref="inputNames" /> takes the built-in's own name
    ///     rather than a declared one.
    /// </remarks>
    void EmitComputeInterface() {
        // Verified before we got here (IrVerifier), so a missing size is a compiler bug rather
        // than something to emit around.
        var size = entryPoint.WorkgroupSize!.Value;

        writer.Line(
            $"layout(local_size_x = {size.X}, local_size_y = {size.Y}, local_size_z = {size.Z}) in;"
        );
        writer.Blank();

        foreach (var input in entryPoint.Inputs) {
            inputNames.Add(ComputeBuiltIns.GlslName(ComputeBuiltIns.Of(input.Semantic)));
        }
    }

    void EmitStreamInterface() {
        if (entryPoint.StreamInputs.Count == 0 && entryPoint.StreamOutputs.Count == 0) {
            return;
        }

        foreach (var stream in entryPoint.StreamInputs) {
            var name = Reserve("in_" + stream.Name);
            streamReads[stream.Variable] = name;
            writer.Line(
                $"layout(location = {StreamPlan.LocationOf(shader, stream)}) in "
                + $"{Declare(stream.Type, name, stream.Name)};"
                + Comment("stream")
            );
        }

        foreach (var stream in entryPoint.StreamOutputs) {
            var name = Reserve("out_" + stream.Name);
            streamWrites[stream.Variable] = name;
            writer.Line(
                $"layout(location = {StreamPlan.LocationOf(shader, stream)}) out "
                + $"{Declare(stream.Type, name, stream.Name)};"
                + Comment("stream")
            );
        }

        writer.Blank();
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
            .Select(p => Direction(p) + Declare(p.Type, LocalName(function, p), p.Name))
            .ToArray();

        var returnType = GlslTypes.Name(function.ReturnType) ?? Unsupported(function.ReturnType, function.Name);
        return $"{returnType} {functionNames[function]}({string.Join(", ", parameters)})";
    }

    /// <summary>
    ///     The direction qualifier for a parameter. GLSL has <c>inout</c> natively, and its meaning
    ///     — copy-in/copy-out — is the same as the IR's, so this is a transcription.
    /// </summary>
    static string Direction(IrVariable parameter) => parameter.IsByReference ? "inout " : string.Empty;

    void EmitFunction(IrFunction function) {
        values.Clear();
        currentFunction = function;

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
                writer.Line($"{Place(store.Place, write: true)} = {Value(store.Value)};");
                return;

            case IrCallInstruction { Result: null } call:
                writer.Line($"{functionNames[call.Function]}({CallArguments(call.Arguments)});");
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

            // `.length()` on a storage block's runtime array, which is the only array GLSL will
            // answer for at run time.
            case IrArrayLengthInstruction length:
                return $"{Place(length.Place)}.length()";

            case IrUnaryInstruction unary:
                return UnaryExpression(unary);

            case IrBinaryInstruction binary:
                return BinaryExpression(binary);

            case IrConvertInstruction convert:
                // A GLSL constructor is both the numeric conversion and the splat.
                return $"{TypeName(convert.Result.Type)}({Value(convert.Operand)})";

            case IrIntrinsicInstruction intrinsic: {
                if (intrinsic.Intrinsic is IrIntrinsic.LoadTexture or IrIntrinsic.TextureSize) {
                    // texelFetch and textureSize on a separate texture, with no sampler to pair it
                    // with, are what this extension adds. Recorded here so the prologue declares
                    // it only in the units that need it.
                    samplerlessFetch = true;
                }

                var arguments = intrinsic.Arguments.Select(Value).ToArray();
                var call = GlslIntrinsics.Call(
                    intrinsic.Intrinsic,
                    arguments,
                    [.. intrinsic.Arguments.Select(a => a.Type)],
                    TypeName(intrinsic.Result!.Type),
                    intrinsic.Result.Type
                );

                if (call is not null) {
                    return call;
                }

                Report(BackendDiagnostics.NotImplemented, $"The '{intrinsic.Intrinsic}' intrinsic");
                return "0";
            }

            case IrCallInstruction call:
                return $"{functionNames[call.Function]}({CallArguments(call.Arguments)})";

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

    /// <summary>
    ///     Renders a call's arguments. A by-reference one names its variable, which is what GLSL's
    ///     own <c>inout</c> needs — an l-value, not a value.
    /// </summary>
    /// <remarks>
    ///     GLSL specifies <c>inout</c> as copy-in/copy-out, the same as the IR, so naming the temp
    ///     the lowerer already made is exact rather than approximate. GLSL then copies it a second
    ///     time into the parameter, which is redundant and free — and it is the price of the IR
    ///     carrying a shape SPIR-V can also express.
    /// </remarks>
    string CallArguments(IReadOnlyList<IrArgument> arguments) =>
        string.Join(
            ", ",
            arguments.Select(argument =>
                argument.IsByReference
                    ? LocalName(currentFunction!, argument.Reference!)
                    : Value(argument.Value!)
            )
        );

    // --- Places, values and names ------------------------------------------

    /// <summary>
    ///     Renders a place. <paramref name="write" /> picks the direction a stream resolves in.
    /// </summary>
    /// <remarks>
    ///     Every other root is direction-blind — a uniform, a local and a parameter each have one
    ///     name — so the flag only matters for a stream, where GLSL genuinely has two variables for
    ///     what the IR models as one.
    /// </remarks>
    string Place(IrPlace place, bool write = false) =>
        StreamName(place.Root, write) + Chain(place.Root.Type, place.Chain);

    string StreamName(IrVariable root, bool write) {
        var names = write ? streamWrites : streamReads;

        if (names.TryGetValue(root, out var name)) {
            return name;
        }

        // A stage that writes a stream without reading it still reads it when the write is
        // partial — lowering records that, so a missing read name here means a genuinely
        // write-only stream and the fallback is the other direction's variable.
        var other = write ? streamReads : streamWrites;
        return other.TryGetValue(root, out var fallback) ? fallback : VariableName(root);
    }

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

    /// <summary>As <see cref="Declare" />, but an unsized outer extent is legal here.</summary>
    string DeclareRuntime(IrType type, string name, string what) =>
        GlslTypes.Declare(type, name, true) ?? $"{Unsupported(type, what)} {name}";

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
