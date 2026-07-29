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

    /// <summary>The declared <c>out</c> variables, in the order of <c>IrEntryPoint.Outputs</c>.</summary>
    readonly List<string> outputNames = [];

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
    int discardCounter;
    bool samplerlessFetch;
    bool nonUniformIndexing;

    /// <summary>
    ///     Per-material values that live in a record, and how to spell a read of one.
    /// </summary>
    /// <remarks>
    ///     A member of a material record has no variable name of its own — it is reached through the
    ///     buffer and the index, so the "name" of it is an expression rather than an identifier. Kept
    ///     apart from <c>variableNames</c> because that map's values are identifiers and a caller is
    ///     entitled to treat them as such.
    /// </remarks>
    readonly Dictionary<IrVariable, (string Buffer, string Field, IrBinding Index)> recordMembers = [];

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
    ///     Declares the push-constant block, if the shader has one.
    /// </summary>
    /// <remarks>
    ///     <c>std430</c> stated rather than left implicit: it is what a Vulkan push-constant block
    ///     takes by default and what the SPIR-V side decorates its members with, and writing it
    ///     down is what keeps the differential from depending on two defaults agreeing. No
    ///     <c>set</c> or <c>binding</c> — a push constant has no descriptor, which is the whole
    ///     reason to use one.
    /// </remarks>
    /// <summary>
    ///     Emits the per-material block as one record of a buffer, rather than as a block.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A named struct and a <c>readonly buffer</c> holding a runtime array of it, laid out
    ///         std430 — which is the shape SPIR-V's side has for the same reason, and the packing a
    ///         record needs rather than a block's std140.
    ///     </para>
    ///     <para>
    ///         The members are <em>not</em> reserved as variable names here. Each one is recorded as
    ///         an access through the array instead, so every read in the body spells
    ///         <c>materials.records[index].member</c> — which is what makes the block a record at the
    ///         use site as well as at the declaration.
    ///     </para>
    /// </remarks>
    void EmitMaterialRecords(PlannedBinding planned, string layout) {
        var structName = Reserve(planned.Name);
        var blockName = Reserve(planned.Name + "Buffer");
        var instance = GlslTypes.Identifier(char.ToLowerInvariant(planned.Name[0]) + planned.Name[1..]);

        writer.Line($"struct {structName} {{");
        writer.Indent();

        var fields = new string[planned.Members.Length];

        for (var i = 0; i < planned.Members.Length; i++) {
            var uniform = planned.Members[i];
            fields[i] = GlslTypes.Identifier(uniform.Name);
            writer.Line(Declare(uniform.Type, fields[i], uniform.Name) + ";" + Comment(uniform.Semantic));
        }

        writer.Outdent();
        writer.Line("};");
        writer.Blank();

        writer.Line($"layout(std430, {layout}) readonly buffer {blockName} {{");
        writer.Indent();
        writer.Line($"{structName} records[];");
        writer.Outdent();
        writer.Line($"}} {instance};");
        writer.Blank();

        // The index is a binding of its own, so it has a name by the time this runs — or will have
        // one by the time the body is emitted, which is why the accessor is built lazily rather than
        // captured here.
        for (var i = 0; i < planned.Members.Length; i++) {
            recordMembers[planned.Members[i].Variable] = (instance, fields[i], planned.RecordIndex!);
        }
    }

    void EmitPushConstants() {
        if (BindingPlan.PushConstants(shader) is not { IsEmpty: false } constants) {
            return;
        }

        writer.Line($"layout(push_constant, std430) uniform {Reserve(BindingPlan.PushConstantBlockName(shader))} {{");
        writer.Indent();

        foreach (var constant in constants) {
            var name = ReserveVariable(constant.Variable);
            writer.Line(Declare(constant.Type, name, constant.Name) + ";" + Comment(constant.Semantic));
        }

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

        EmitPushConstants();

        foreach (var planned in BindingPlan.Of(shader)) {
            var layout = $"set = {(int)planned.Set}, binding = {planned.Binding}";

            if (planned.Kind == IrBindingKind.StorageBuffer && planned.Resource is { } buffer) {
                EmitStorageBuffer(buffer, layout);
                opaque = false;
                continue;
            }

            if (planned.Resource is { } resource) {
                var name = ReserveVariable(resource.Variable);

                // The aliases take the same name rather than one of their own. A shared binding is
                // one declaration, and each feature that named it refers to its own variable — so
                // every one of those has to spell the same identifier, or the second feature's
                // sample names something the unit never declared.
                foreach (var alias in planned.Aliases) {
                    variableNames[alias.Variable] = name;
                }

                // A storage image carries its texel format in the layout qualifier. GLSL requires
                // it on any image that is read, and stating it always keeps the two backends
                // emitting the same declaration.
                var format = resource.Type is IrStorageImageType image ? image.Format + ", " : string.Empty;

                // A descriptor array is declared with an empty extent — `uniform texture2D t[];` —
                // which is the one place outside a storage block where GLSL allows one. Reached
                // through DeclareRuntime for exactly that reason; Declare would report it as a type
                // it cannot spell.
                var declaration = IsDescriptorArray(resource.Type)
                    ? DeclareRuntime(resource.Type, name, resource.Name)
                    : Declare(resource.Type, name, resource.Name);

                writer.Line(
                    $"layout({format}{layout}) uniform {declaration};" + Comment(resource.Semantic)
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

            if (planned.IsRecord) {
                EmitMaterialRecords(planned, layout);
                continue;
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

        var locations = StreamPlan.InputLocations(shader, entryPoint);
        var declared = 0;

        for (var i = 0; i < entryPoint.Inputs.Count; i++) {
            var input = entryPoint.Inputs[i];

            // A built-in is a variable GLSL already declares, so `main` passes it straight through
            // — there is no `in` to write and no location to spend.
            if (StageBuiltIns.Of(input.Semantic, entryPoint.Stage) is { } builtIn) {
                inputNames.Add(builtIn.GlslName);
                continue;
            }

            RequireCarryable(input, true);

            var name = Reserve("in_" + input.Name);
            inputNames.Add(name);
            declared++;
            writer.Line(
                $"layout(location = {locations[i]}) in {Declare(input.Type, name, input.Name)};"
                + Comment(input.Semantic)
            );
        }

        if (declared > 0) {
            writer.Blank();
        }

        if (OutputGoesToBuiltIn()) {
            return;
        }

        var outputLocation = StreamPlan.OutputBase(shader, entryPoint.Stage);

        foreach (var output in entryPoint.Outputs) {
            RequireCarryable(output, false);

            var name = Reserve("out_" + output.Name);
            outputNames.Add(name);
            writer.Line(
                $"layout(location = {outputLocation++}) out {Declare(output.Type, name, output.Name)};"
                + Comment(output.Semantic)
            );
        }

        if (entryPoint.Outputs.Count > 0) {
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
            inputNames.Add(StageBuiltIns.Of(input.Semantic, ShaderStage.Compute)!.GlslName);
        }
    }

    void EmitStreamInterface() {
        if (entryPoint.StreamInputs.Count == 0 && entryPoint.StreamOutputs.Count == 0) {
            return;
        }

        foreach (var stream in entryPoint.StreamInputs) {
            var name = Reserve("in_" + stream.Name);
            streamReads[stream.Variable] = name;

            // The same rule SPIR-V states as `Flat`: an integer has no interpolation to take, so GLSL
            // requires the qualifier here and rejects the declaration without it. Only on the input,
            // because it describes how a value is received.
            var flat = entryPoint.Stage == ShaderStage.Fragment && StageInterface.MustBeFlat(stream.Type)
                ? "flat "
                : string.Empty;

            writer.Line(
                $"layout(location = {StreamPlan.LocationOf(shader, stream)}) {flat}in "
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
        EmitUnreachableReturn(function);

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

        if (entryPoint.Outputs.Count == 0) {
            writer.Line(call + ";");
        } else if (OutputGoesToBuiltIn()) {
            writer.Line($"gl_Position = {call};");
        } else if (entryPoint.Outputs is [{ Member: null }]) {
            writer.Line($"{outputNames[0]} = {call};");
        } else {
            // Several render targets: the result is a struct, so it lands in a local once and
            // each target takes its member. Calling per target would run the shader body N times.
            var result = Reserve("_targets");
            writer.Line($"{TypeName(entryPoint.Function.ReturnType)} {result} = {call};");

            foreach (var (output, name) in entryPoint.Outputs.Zip(outputNames)) {
                writer.Line($"{name} = {result}.{GlslTypes.Identifier(output.Name)};");
            }
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

            case IrDiscardStatement:
                writer.Line("discard;");
                break;
        }
    }

    /// <summary>
    ///     Gives glslang the <c>return</c> it insists on for a path that only a <c>discard</c> ends.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The one place the two targets genuinely disagree about <c>discard</c>. SPIR-V's
    ///         <c>OpKill</c> is a block terminator, so a function ending in one is complete and the
    ///         emitter closes it with <c>OpUnreachable</c>. GLSL's <c>discard</c> is an ordinary
    ///         statement, and glslang rejects a value-returning function whose end its own flow
    ///         analysis can reach — so the text has to say <c>return</c> even though nothing runs
    ///         after the kill.
    ///     </para>
    ///     <para>
    ///         An uninitialised local rather than a constructed zero, because it is correct for
    ///         every type — struct, array, matrix — with no per-type spelling, and reading it is
    ///         exactly as impossible as reaching the line. Emitted only for a function that
    ///         <em>can</em> discard: glslang is happy with a body whose arms all return, so adding
    ///         this everywhere would be noise in every other function's output.
    ///     </para>
    /// </remarks>
    void EmitUnreachableReturn(IrFunction function) {
        if (function.ReturnType.IsVoid || !function.Discards) {
            return;
        }

        var name = Reserve($"_discarded{discardCounter++}");
        writer.Line(Declare(function.ReturnType, name, name) + ";");
        writer.Line($"return {name};");
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

            // A texel store produces nothing, so it is a statement rather than an assignment.
            case IrIntrinsicInstruction { Result: null } effect:
                writer.Line(IntrinsicCall(effect) + ";");
                return;
        }

        if (instruction.Result is not { } result) {
            return;
        }

        // GLSL forbids locals of opaque type, so a texture, sampler or image value is
        // never materialized: uses refer straight back to the uniform.
        if (result.Type is IrSamplerType or IrTextureType or IrStorageImageType) {
            values[result.Id] = instruction is IrLoadInstruction opaque ? Place(opaque.Place) : "/* opaque */";
            return;
        }

        var expression = Expression(instruction);
        var declaration = Declare(result.Type, Name(result), $"%{result.Id}");
        writer.Line($"{declaration} = {expression};");
    }

    /// <summary>
    ///     The GLSL for one intrinsic call, whether or not it produces a value.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="Expression" /> because a texel store is a statement: it has no
    ///     result to assign, and it is the one intrinsic that does not.
    /// </remarks>
    string IntrinsicCall(IrIntrinsicInstruction intrinsic) {
        if (intrinsic.Intrinsic is IrIntrinsic.LoadTexture or IrIntrinsic.TextureSize) {
            // texelFetch and textureSize on a separate texture, with no sampler to pair it
            // with, are what this extension adds. Recorded here so the prologue declares
            // it only in the units that need it.
            samplerlessFetch = true;
        }

        var resultType = intrinsic.Result?.Type ?? IrScalarType.Void;
        var call = GlslIntrinsics.Call(
            intrinsic.Intrinsic,
            [.. intrinsic.Arguments.Select(Value)],
            [.. intrinsic.Arguments.Select(a => a.Type)],
            TypeName(resultType),
            resultType
        );

        if (call is not null) {
            return call;
        }

        Report(BackendDiagnostics.NotImplemented, $"The '{intrinsic.Intrinsic}' intrinsic");
        return "0";
    }

    string Expression(IrInstruction instruction) {
        switch (instruction) {
            case IrLoadInstruction load:
                return Place(load.Place);

            // `.length()` on a storage block's runtime array, which is the only array GLSL will
            // answer for at run time.
            case IrArrayLengthInstruction length:
                return $"{Place(length.Place)}.length()";

            // The place, not a loaded value: GLSL's atomics take an l-value, which is the whole
            // reason the IR carries a place here.
            case IrAtomicInstruction atomic:
                return atomic.Comparand is { } comparand
                    ? $"atomicCompSwap({Place(atomic.Place)}, {Value(comparand)}, {Value(atomic.Value)})"
                    : $"{AtomicName(atomic.Op)}({Place(atomic.Place)}, {Value(atomic.Value)})";

            case IrUnaryInstruction unary:
                return UnaryExpression(unary);

            case IrBinaryInstruction binary:
                return BinaryExpression(binary);

            case IrConvertInstruction convert:
                // A GLSL constructor is both the numeric conversion and the splat.
                return $"{TypeName(convert.Result.Type)}({Value(convert.Operand)})";

            case IrIntrinsicInstruction intrinsic:
                return IntrinsicCall(intrinsic);

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

    /// <summary>What GLSL calls each atomic.</summary>
    /// <remarks>
    ///     One name per operation whatever the operand's signedness, which is where GLSL and SPIR-V
    ///     part company: <c>atomicMin</c> covers both and <c>OpAtomicSMin</c>/<c>OpAtomicUMin</c> do
    ///     not. Compare-exchange is not here because its argument order differs too, and one place
    ///     that spells the whole call is clearer than a name plus an exception.
    /// </remarks>
    static string AtomicName(IrAtomicOp op) =>
        op switch {
            IrAtomicOp.Add => "atomicAdd",
            IrAtomicOp.Min => "atomicMin",
            IrAtomicOp.Max => "atomicMax",
            IrAtomicOp.And => "atomicAnd",
            IrAtomicOp.Or => "atomicOr",
            IrAtomicOp.Xor => "atomicXor",
            _ => "atomicExchange"
        };

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

                // Indexing a descriptor array is the one subscript that has to say the number is not
                // uniform across the subgroup. Without `nonuniformEXT` the driver may hoist the
                // descriptor read, which is right for every other array here and wrong for the one
                // whose whole purpose is a different texture per fragment.
                case IrIndexAccess index when IsDescriptorArray(type):
                    nonUniformIndexing = true;
                    builder.Append("[nonuniformEXT(").Append(Value(index.Index)).Append(")]");
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

    string VariableName(IrVariable variable) =>
        recordMembers.TryGetValue(variable, out var record)
            ? $"{record.Buffer}.records[{VariableName(record.Index.Variable)}].{record.Field}"
            : variableNames.GetValueOrDefault(variable, variable.Name);

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

    /// <summary>Whether this is a descriptor array rather than a laid-out one.</summary>
    /// <remarks>
    ///     The same question <c>SpirvTypes.IsDescriptorArray</c> asks, and asked separately on
    ///     purpose: a backend reaching into the other one for a predicate is how the two come to
    ///     share a bug rather than a rule. The rule itself is the IR's — an unsized array of textures
    ///     — and both read it off the IR.
    /// </remarks>
    static bool IsDescriptorArray(IrType type) =>
        type is IrArrayType { Length: null, Element: IrTextureType };

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

        // Only where something actually indexed a descriptor array. A shader that declares one and
        // never subscripts it needs the empty extent and not the qualifier, and declaring an
        // extension a unit does not use is something a driver is allowed to reject.
        if (nonUniformIndexing) {
            prologue.Line("#extension GL_EXT_nonuniform_qualifier : require");
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
