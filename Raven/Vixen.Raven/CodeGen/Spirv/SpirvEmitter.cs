// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>
///     Where a global lives. A uniform is a member of one block rather than a
///     variable of its own, so reaching it always begins with a member index.
/// </summary>
/// <param name="Variable">The <c>OpVariable</c> id to start an access chain from.</param>
/// <param name="Storage">Its storage class, which decides the pointer types along the chain.</param>
/// <param name="Member">The member index inside the uniform block, if it is one.</param>
/// <param name="Layout">
///     The packing rule its type is decorated with, or null when it carries none. Recorded here
///     rather than derived from <paramref name="Storage" /> because a uniform block and a storage
///     buffer share the <c>Uniform</c> storage class in the form Vulkan 1.0 accepts, and pack
///     differently — std140 against std430.
/// </param>
sealed record SpirvGlobal(
    uint Variable,
    SpirvStorageClass Storage,
    int? Member = null,
    LayoutRule? Layout = null
) {
    /// <summary>
    ///     The binding whose value subscripts the record this member lives in, or null.
    /// </summary>
    /// <remarks>
    ///     Set only for a member of a per-material block the shader turned into a record. The access
    ///     chain then reads <c>buffer[0][index].member</c> rather than <c>block.member</c> — the
    ///     leading zero being the runtime array inside the block, which is the same shape every
    ///     storage buffer here has. The index is <em>loaded</em> where the access is emitted rather
    ///     than hoisted, because it lives in the per-draw block and a load from a uniform is what
    ///     every other read of it would be anyway.
    /// </remarks>
    public IrBinding? RecordIndex { get; init; }
}

/// <summary>
///     Emits one SPIR-V module for one entry point.
/// </summary>
/// <remarks>
///     <para>
///         This is a bigger step down than GLSL. The IR's structured control flow has to
///         become basic blocks with explicit merge declarations; its named locals become
///         <c>OpVariable</c>s reached through access chains; and its types have to carry
///         an explicit memory layout, because SPIR-V has no implicit one.
///     </para>
///     <para>
///         What SPIR-V gives back is a separate sampler object, which GLSL lacks — a
///         texture and a sampler stay two bindings and pair up at the sample site, so
///         nothing is dropped.
///     </para>
/// </remarks>
sealed partial class SpirvEmitter {
    /// <summary>Output semantics that belong in a built-in rather than a located variable.</summary>
    static readonly HashSet<string> PositionSemantics = new(StringComparer.OrdinalIgnoreCase) {
        "SV_Position", "POSITION"
    };

    readonly DiagnosticBag diagnostics;
    readonly IrEntryPoint entryPoint;
    readonly Dictionary<IrFunction, uint> functions = [];
    readonly Dictionary<IrVariable, SpirvGlobal> globals = [];
    readonly List<uint> interfaceIds = [];
    readonly IrModule irModule;
    readonly SpirvModule module;
    readonly SpirvOptions options;
    readonly IrShader shader;
    readonly SpirvTypes types;

    readonly List<(IrStageIo Io, uint Variable)> inputs = [];

    /// <summary>The <c>Output</c> variables, in the order of <c>IrEntryPoint.Outputs</c>.</summary>
    readonly List<(IrStageIo Io, uint Variable)> outputs = [];

    /// <summary>
    ///     The <c>Input</c> and <c>Output</c> variable a stream resolves to, by direction.
    /// </summary>
    /// <remarks>
    ///     Two variables for one IR variable, because SPIR-V splits what the IR does not: an
    ///     <c>Input</c> pointer cannot be stored through and an <c>Output</c> pointer is not what a
    ///     load should read. A stream a stage both reads and writes gets both, which is legal —
    ///     input and output locations are separate namespaces.
    /// </remarks>
    readonly Dictionary<IrVariable, uint> streamReads = [];

    readonly Dictionary<IrVariable, uint> streamWrites = [];

    uint extendedInstructions;

    internal SpirvEmitter(
        IrModule irModule,
        IrShader shader,
        IrEntryPoint entryPoint,
        SpirvOptions options,
        DiagnosticBag diagnostics
    ) {
        this.irModule = irModule;
        this.shader = shader;
        this.entryPoint = entryPoint;
        this.options = options;
        this.diagnostics = diagnostics;

        module = new(options.Version);
        types = new(module, (type, what) => Report(BackendDiagnostics.NotExpressible, Describe(type, what)));
    }

    // --- Declarations ------------------------------------------------------

    void EmitBindings() {
        EmitPushConstants();

        // Which (set, binding) each resource gets is BindingPlan's decision, not this
        // emitter's — the GLSL emitter and the reflection read the same plan, so the three
        // cannot drift apart.
        foreach (var planned in BindingPlan.Of(shader)) {
            if (planned.Kind == IrBindingKind.StorageBuffer && planned.Resource is { } buffer) {
                EmitStorageBuffer(buffer, planned);
                continue;
            }

            if (planned.Resource is { } resource) {
                var declared = new SpirvGlobal(
                    DeclareOpaque(resource, planned),
                    SpirvStorageClass.UniformConstant
                );

                // Every declaration the plan collapsed into this one, not only the first. A shared
                // binding is one resource named by several features, and each feature's body refers
                // to the variable *it* was compiled against — so all of them have to reach the one
                // OpVariable that was emitted, or the second feature's sample resolves to nothing.
                foreach (var declaration in planned.Declarations) {
                    globals[declaration.Variable] = declared;
                }

                continue;
            }

            EmitUniformBlock(planned);
        }

        // Binding defaults are a property of the shader rather than of any one
        // stage, so SpirvBackend says that once however many modules come out.
    }

    /// <summary>
    ///     Declares a storage buffer: a <c>BufferBlock</c> struct holding one runtime array.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>BufferBlock</c> with <c>Uniform</c> storage rather than <c>Block</c> with
    ///         <c>StorageBuffer</c> storage. The two spell the same thing, but the second needs
    ///         <c>SPV_KHR_storage_buffer_storage_class</c> in SPIR-V 1.0 — which Vulkan 1.0 has as an
    ///         extension and 1.1 folded in. This form needs no extension at all, and it is the form
    ///         <c>glslc</c> produces for the same GLSL, which is what keeps § C's differential honest.
    ///     </para>
    ///     <para>
    ///         The wrapping struct is not decoration: SPIR-V has no bare runtime array variable, so the
    ///         array is always member 0 of a block. That member index is why every access chain into a
    ///         buffer starts with a 0 — the same shape a uniform block's member index gives.
    ///     </para>
    /// </remarks>
    void EmitStorageBuffer(IrBinding buffer, PlannedBinding planned) {
        if (buffer.Type is not IrArrayType array) {
            Report(BackendDiagnostics.NotExpressible, Describe(buffer.Type, buffer.Name));
            return;
        }

        var contents = types.RuntimeArray(array, LayoutRule.Std430);
        var structId = module.AddDeclaration(SpirvOp.TypeStruct, null, SpirvOperand.Id(contents));

        module.AddName(structId, buffer.Name + "Block");
        module.AddMemberName(structId, 0, buffer.Name);
        module.Decorate(structId, SpirvDecoration.BufferBlock);
        module.DecorateMember(structId, 0, SpirvDecoration.Offset, SpirvOperand.Literal(0));

        // Declared read-only on the member *and* the variable, which is what glslc emits: the member
        // decoration is what a driver reads, and the variable decoration is what a later pass reads.
        if (!buffer.IsWritable) {
            module.DecorateMember(structId, 0, SpirvDecoration.NonWritable);
        }

        var variable = module.AddDeclaration(
            SpirvOp.Variable,
            types.Pointer(SpirvStorageClass.Uniform, structId),
            SpirvOperand.Enumerant(SpirvStorageClass.Uniform)
        );

        module.AddName(variable, buffer.Name);
        DecorateBinding(variable, planned);

        if (!buffer.IsWritable) {
            module.Decorate(variable, SpirvDecoration.NonWritable);
        }

        globals[buffer.Variable] = new(variable, SpirvStorageClass.Uniform, 0, LayoutRule.Std430);
    }

    void EmitUniformBlock(PlannedBinding planned) {
        var uniforms = planned.Members;

        foreach (var uniform in uniforms.Where(u => ContainsBool(u.Type))) {
            // A SPIR-V bool has no size and no memory layout, so it cannot live
            // anywhere the host can see. GLSL hides this by giving it four bytes
            // in a std140 block; SPIR-V does not.
            Report(BackendDiagnostics.NotExpressible, $"The boolean in uniform binding '{uniform.Name}'");
        }

        // std430 for a record and std140 for a block, because they are different objects: a record
        // is an element of a storage buffer, and packing it std140 would round every member up to
        // the rule a uniform block needs and leave the host writing a stride nothing agrees on.
        var layout = planned.IsRecord ? LayoutRule.Std430 : LayoutRule.Std140;

        var members = uniforms.Select(u => u.Type).ToArray();
        var structId = module.AddDeclaration(
            SpirvOp.TypeStruct,
            null,
            [.. members.Select(m => SpirvOperand.Id(types.Type(m, layout)))]
        );

        module.AddName(structId, planned.Name);

        // Before the layout, which is where it has always been: the golden listings pin the order
        // annotations come out in, and a `Block` that moved below the member offsets is a diff in
        // every fixture for no change in meaning. A record carries `BufferBlock` on the struct that
        // wraps it instead, which is emitted below.
        if (!planned.IsRecord) {
            module.Decorate(structId, SpirvDecoration.Block);
        }

        types.DecorateLayout(structId, members, layout);

        for (var i = 0; i < uniforms.Length; i++) {
            module.AddMemberName(structId, i, uniforms[i].Name);
        }

        // A record is one element of a runtime array inside a BufferBlock — the same shape
        // EmitStorageBuffer gives every storage buffer, and for the same reason: SPIR-V has no bare
        // runtime-array variable, so the array is always member 0 of a block. An ordinary uniform
        // block is the struct itself.
        var blockId = structId;

        if (planned.IsRecord) {
            var records = types.RecordArray(structId, ShaderLayout.Members(members, LayoutRule.Std430).Size);
            blockId = module.AddDeclaration(SpirvOp.TypeStruct, null, SpirvOperand.Id(records));

            module.AddName(blockId, planned.Name + "Block");
            module.AddMemberName(blockId, 0, "records");
            module.Decorate(blockId, SpirvDecoration.BufferBlock);
            module.DecorateMember(blockId, 0, SpirvDecoration.Offset, SpirvOperand.Literal(0));
            module.DecorateMember(blockId, 0, SpirvDecoration.NonWritable);
        }

        var variable = module.AddDeclaration(
            SpirvOp.Variable,
            types.Pointer(SpirvStorageClass.Uniform, blockId),
            SpirvOperand.Enumerant(SpirvStorageClass.Uniform)
        );

        module.AddName(variable, char.ToLowerInvariant(planned.Name[0]) + planned.Name[1..]);
        DecorateBinding(variable, planned);

        for (var i = 0; i < uniforms.Length; i++) {
            globals[uniforms[i].Variable] = new(variable, SpirvStorageClass.Uniform, i, layout) {
                RecordIndex = planned.RecordIndex
            };
        }
    }

    /// <summary>
    ///     Declares the push-constant block: one <c>Block</c>-decorated struct in the
    ///     <c>PushConstant</c> storage class, laid out std430.
    /// </summary>
    /// <remarks>
    ///     No <c>DescriptorSet</c> and no <c>Binding</c>, which is the difference that matters — a
    ///     push constant is not in a descriptor set, and decorating one as if it were is a module
    ///     <c>spirv-val</c> rejects. Layout is std430 rather than std140, matching both the GLSL
    ///     side and what <c>glslc</c> gives a <c>layout(push_constant)</c> block.
    /// </remarks>
    void EmitPushConstants() {
        if (BindingPlan.PushConstants(shader) is not { IsEmpty: false } constants) {
            return;
        }

        foreach (var constant in constants.Where(c => ContainsBool(c.Type))) {
            // Same reason as a uniform block: OpTypeBool has no size, so it cannot live anywhere
            // the host writes bytes into.
            Report(BackendDiagnostics.NotExpressible, $"The boolean in push constant '{constant.Name}'");
        }

        var members = constants.Select(c => c.Type).ToArray();
        var structId = module.AddDeclaration(
            SpirvOp.TypeStruct,
            null,
            [.. members.Select(m => SpirvOperand.Id(types.Type(m, LayoutRule.Std430)))]
        );

        var blockName = BindingPlan.PushConstantBlockName(shader);
        module.AddName(structId, blockName);
        module.Decorate(structId, SpirvDecoration.Block);
        types.DecorateLayout(structId, members, LayoutRule.Std430);

        for (var i = 0; i < constants.Length; i++) {
            module.AddMemberName(structId, i, constants[i].Name);
        }

        var variable = module.AddDeclaration(
            SpirvOp.Variable,
            types.Pointer(SpirvStorageClass.PushConstant, structId),
            SpirvOperand.Enumerant(SpirvStorageClass.PushConstant)
        );

        module.AddName(variable, char.ToLowerInvariant(blockName[0]) + blockName[1..]);

        for (var i = 0; i < constants.Length; i++) {
            globals[constants[i].Variable] = new(variable, SpirvStorageClass.PushConstant, i, LayoutRule.Std430);
        }
    }

    uint DeclareOpaque(IrBinding resource, PlannedBinding planned) {
        var variable = module.AddDeclaration(
            SpirvOp.Variable,
            types.Pointer(SpirvStorageClass.UniformConstant, types.Type(resource.Type)),
            SpirvOperand.Enumerant(SpirvStorageClass.UniformConstant)
        );

        module.AddName(variable, resource.Name);
        DecorateBinding(variable, planned);
        return variable;
    }

    void DecorateBinding(uint variable, PlannedBinding planned) {
        module.Decorate(
            variable,
            SpirvDecoration.DescriptorSet,
            SpirvOperand.Literal((uint)(int)planned.Set)
        );

        module.Decorate(variable, SpirvDecoration.Binding, SpirvOperand.Literal((uint)planned.Binding));
    }

    /// <summary>
    ///     Declares the stage interface: the streams this stage reads and writes, then its own
    ///     parameters and return value.
    /// </summary>
    /// <remarks>
    ///     Every location comes from <see cref="StreamPlan" />, the same source the GLSL emitter and
    ///     the reflection read — which is what makes the vertex stage's outputs line up with the
    ///     fragment stage's inputs without either stage knowing about the other.
    /// </remarks>
    void EmitStageInterface() {
        if (entryPoint.Stage == ShaderStage.Compute) {
            EmitComputeInterface();
            return;
        }

        EmitStreamInterface();

        var locations = StreamPlan.InputLocations(shader, entryPoint);

        for (var i = 0; i < entryPoint.Inputs.Count; i++) {
            var input = entryPoint.Inputs[i];
            var builtIn = StageBuiltIns.Of(input.Semantic, entryPoint.Stage);
            var variable = DeclareStageVariable(
                input,
                SpirvStorageClass.Input,
                "in_" + input.Name,
                located: builtIn is null
            );

            // `Location` and `BuiltIn` are mutually exclusive, so a pipeline-supplied value takes
            // the second and the plan gave it no location to spend.
            if (builtIn is not null) {
                module.Decorate(
                    variable,
                    SpirvDecoration.BuiltIn,
                    SpirvOperand.Enumerant(SpirvBuiltIns.Of(builtIn.BuiltIn))
                );
            } else {
                module.Decorate(variable, SpirvDecoration.Location, SpirvOperand.Literal((uint)locations[i]!.Value));
            }

            inputs.Add((input, variable));
        }

        var outputLocation = (uint)StreamPlan.OutputBase(shader, entryPoint.Stage);

        foreach (var output in entryPoint.Outputs) {
            var variable = DeclareStageVariable(
                output,
                SpirvStorageClass.Output,
                "out_" + output.Name,
                located: !OutputGoesToBuiltIn()
            );

            outputs.Add((output, variable));

            if (OutputGoesToBuiltIn()) {
                module.Decorate(variable, SpirvDecoration.BuiltIn, SpirvOperand.Enumerant(SpirvBuiltIn.Position));
            } else {
                module.Decorate(variable, SpirvDecoration.Location, SpirvOperand.Literal(outputLocation++));
            }
        }
    }

    /// <summary>
    ///     Declares a compute stage's parameters as built-in <c>Input</c> variables.
    /// </summary>
    /// <remarks>
    ///     A <c>Location</c> and a <c>BuiltIn</c> are mutually exclusive decorations, and a compute
    ///     stage has no located interface at all — nothing feeds an attribute and nothing takes a
    ///     result. So every parameter is a built-in, which the binder has already narrowed to the
    ///     four dispatch ids with the right type. They still join <c>interfaceIds</c>: a built-in a
    ///     module reads has to appear in its entry point's interface list, or <c>spirv-val</c>
    ///     rejects it.
    /// </remarks>
    void EmitComputeInterface() {
        EmitSharedVariables();

        foreach (var input in entryPoint.Inputs) {
            var variable = DeclareStageVariable(input, SpirvStorageClass.Input, "in_" + input.Name, located: false);

            module.Decorate(
                variable,
                SpirvDecoration.BuiltIn,
                SpirvOperand.Enumerant(SpirvBuiltIns.Of(StageBuiltIns.Of(input.Semantic, ShaderStage.Compute)!.BuiltIn))
            );

            inputs.Add((input, variable));
        }
    }

    /// <summary>
    ///     Declares the <c>groupshared</c> variables this stage reaches, in the <c>Workgroup</c>
    ///     storage class.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An <c>OpVariable</c> at module scope with no initializer — SPIR-V has no way to give
    ///         workgroup storage one, which is why <c>RVN2134</c> refuses the source that asks for
    ///         it. It joins <see cref="globals" /> like any other module-scope variable, so a read
    ///         is the same load and a write the same access chain every binding takes; only the
    ///         storage class in the pointer type differs.
    ///     </para>
    ///     <para>
    ///         Not in <see cref="interfaceIds" />, and that is a version rule rather than an
    ///         oversight: before SPIR-V 1.4 an entry point's interface lists <c>Input</c> and
    ///         <c>Output</c> variables and nothing else, and this backend emits 1.0. Adding a
    ///         <c>Workgroup</c> id there is what a 1.4 module would do and what <c>spirv-val</c>
    ///         rejects in a 1.0 one.
    ///     </para>
    /// </remarks>
    void EmitSharedVariables() {
        foreach (var shared in entryPoint.SharedVariables) {
            var variable = module.AddDeclaration(
                SpirvOp.Variable,
                types.Pointer(SpirvStorageClass.Workgroup, types.Type(shared.Type)),
                SpirvOperand.Enumerant(SpirvStorageClass.Workgroup)
            );

            module.AddName(variable, shared.Name);
            globals[shared.Variable] = new(variable, SpirvStorageClass.Workgroup);
        }
    }

    void EmitStreamInterface() {
        foreach (var stream in entryPoint.StreamInputs) {
            streamReads[stream.Variable] = DeclareStream(stream, SpirvStorageClass.Input, "in_");
        }

        foreach (var stream in entryPoint.StreamOutputs) {
            streamWrites[stream.Variable] = DeclareStream(stream, SpirvStorageClass.Output, "out_");
        }
    }

    uint DeclareStream(IrStream stream, SpirvStorageClass storage, string prefix) {
        var variable = DeclareStageVariable(
            new(stream.Name, stream.Type, null),
            storage,
            prefix + stream.Name,
            located: true
        );

        module.Decorate(
            variable,
            SpirvDecoration.Location,
            SpirvOperand.Literal((uint)StreamPlan.LocationOf(shader, stream))
        );

        // An integer has no interpolation to take — the rasteriser weights by barycentric
        // coordinates and that produces a fraction — so SPIR-V requires this on a fragment input of
        // integer type and a module without it is invalid. Only the input: the decoration says how a
        // value is *received*, and the vertex stage that wrote it has no interpolation to describe.
        if (entryPoint.Stage == ShaderStage.Fragment
            && storage == SpirvStorageClass.Input
            && StageInterface.MustBeFlat(stream.Type)) {
            module.Decorate(variable, SpirvDecoration.Flat);
        }

        return variable;
    }

    /// <param name="located">
    ///     Whether this variable takes a <c>Location</c>. False for one that takes a
    ///     <c>BuiltIn</c> instead, which is what exempts it from the check below — see
    ///     <see cref="StageInterface" />. The GLSL backend skips the same check for the same
    ///     variables, by declaring nothing for them at all.
    /// </param>
    uint DeclareStageVariable(IrStageIo io, SpirvStorageClass storage, string name, bool located) {
        // Vulkan has no boolean interface type, and an aggregate would need a location for every
        // leaf. Both are rejected rather than mis-emitted — through the shared predicate, so the
        // GLSL backend refuses exactly the same set rather than emitting `out SomeStruct`.
        if (located && !StageInterface.CanCarry(io.Type)) {
            Report(
                BackendDiagnostics.NotExpressible,
                StageInterface.Describe(io.Type, io.Name, storage == SpirvStorageClass.Input)
            );
        }

        var variable = module.AddDeclaration(
            SpirvOp.Variable,
            types.Pointer(storage, types.Type(io.Type)),
            SpirvOperand.Enumerant(storage)
        );

        module.AddName(variable, name);
        interfaceIds.Add(variable);
        return variable;
    }

    /// <summary>
    ///     The variable a stream resolves to in the given direction, or null when the root is not a
    ///     stream.
    /// </summary>
    /// <remarks>
    ///     A write-only stream loaded, or a read-only one stored to, falls back to the other
    ///     direction rather than reporting: lowering already marks a partial write as a read, so the
    ///     only way to get here is a whole-value use, and a wrong storage class is a
    ///     <c>spirv-val</c> failure with a clear message rather than a silent miscompilation.
    /// </remarks>
    uint? ResolveStream(IrVariable root, bool write) {
        var preferred = write ? streamWrites : streamReads;

        if (preferred.TryGetValue(root, out var variable)) {
            return variable;
        }

        var other = write ? streamReads : streamWrites;
        return other.TryGetValue(root, out var fallback) ? fallback : null;
    }

    /// <summary>True when the stage result belongs in <c>Position</c> rather than a located output.</summary>
    bool OutputGoesToBuiltIn() =>
        entryPoint.Stage == ShaderStage.Vertex
        && entryPoint.Output is { } output
        && (output.Semantic is null || PositionSemantics.Contains(output.Semantic))
        && output.Type is IrVectorType { Size: 4, Component.Kind: IrTypeKind.Float };

    // --- Functions ---------------------------------------------------------

    void EmitFunction(IrFunction function) {
        values.Clear();
        constants.Clear();
        pointers.Clear();
        opaqueParameters.Clear();
        loops.Clear();

        var returnType = types.Type(function.ReturnType);

        // A by-reference parameter takes a pointer into function storage. Function storage rather
        // than a choice: the caller always passes a function-scoped temp, because SPIR-V requires a
        // pointer argument to be a memory object declaration and requires the storage classes to
        // match — so no other class could ever appear here.
        var parameterTypes = function.Parameters
            .Select(p => p.IsByReference
                ? types.Pointer(SpirvStorageClass.Function, types.Type(p.Type))
                : types.Type(p.Type))
            .ToArray();

        var id = functions[function];

        module.AddName(id, function.Name);
        Add(
            new(
                SpirvOp.Function,
                returnType,
                id,
                SpirvOperand.Enumerant(SpirvFunctionControl.None),
                SpirvOperand.Id(types.Function(returnType, parameterTypes))
            )
        );

        var parameterIds = new uint[function.Parameters.Count];

        for (var i = 0; i < function.Parameters.Count; i++) {
            parameterIds[i] = module.AllocateId();
            module.AddName(parameterIds[i], function.Parameters[i].Name);
            Add(new(SpirvOp.FunctionParameter, parameterTypes[i], parameterIds[i]));
        }

        BeginBlock(module.AllocateId());

        // Every OpVariable has to sit at the top of the first block, so the
        // parameter copies and the locals are declared before anything runs.
        List<(uint Pointer, uint Value)> copies = [];

        for (var i = 0; i < function.Parameters.Count; i++) {
            var parameter = function.Parameters[i];

            // An image or a sampler cannot live in function storage, so an opaque
            // parameter keeps its value and reads of it resolve to that directly.
            if (parameter.Type is IrTextureType or IrSamplerType) {
                opaqueParameters[parameter] = parameterIds[i];
                continue;
            }

            // A by-reference parameter *is* a pointer, so it needs no local of its own: registering
            // it here makes every load, store and access chain on it resolve through the caller's
            // storage, which is exactly what copy-out has to observe.
            if (parameter.IsByReference) {
                pointers[parameter] = parameterIds[i];
                continue;
            }

            copies.Add((DeclareLocal(parameter), parameterIds[i]));
        }

        foreach (var local in function.Locals) {
            DeclareLocal(local);
        }

        foreach (var (pointer, value) in copies) {
            Add(new(SpirvOp.Store, null, null, SpirvOperand.Id(pointer), SpirvOperand.Id(value)));
        }

        EmitBlock(function.Body);

        // A body that runs off its end still needs a terminator. Returning is
        // right for a void function; for any other the verifier has already
        // ruled the path out, so it can only be unreachable.
        if (!terminated) {
            Add(
                function.ReturnType.IsVoid
                    ? new(SpirvOp.Return, null, null)
                    : new SpirvInstruction(SpirvOp.Unreachable, null, null)
            );
        }

        Add(new(SpirvOp.FunctionEnd, null, null));
    }

    uint DeclareLocal(IrVariable variable) {
        var pointer = module.AllocateId();
        Add(
            new(
                SpirvOp.Variable,
                types.Pointer(SpirvStorageClass.Function, types.Type(variable.Type)),
                pointer,
                SpirvOperand.Enumerant(SpirvStorageClass.Function)
            )
        );

        module.AddName(pointer, variable.Name);
        pointers[variable] = pointer;
        return pointer;
    }

    /// <summary>
    ///     The <c>main</c> the pipeline calls: it reads the stage inputs, hands them
    ///     to the user's function, and writes the result to the stage output.
    /// </summary>
    void EmitEntryPoint() {
        values.Clear();
        constants.Clear();
        pointers.Clear();
        loops.Clear();

        var main = module.AllocateId();
        module.AddName(main, "main");

        Add(
            new(
                SpirvOp.Function,
                types.Void,
                main,
                SpirvOperand.Enumerant(SpirvFunctionControl.None),
                SpirvOperand.Id(types.Function(types.Void, []))
            )
        );

        BeginBlock(module.AllocateId());

        var arguments = new List<SpirvOperand>();

        foreach (var (io, variable) in inputs) {
            arguments.Add(SpirvOperand.Id(Emit(SpirvOp.Load, types.Type(io.Type), SpirvOperand.Id(variable))));
        }

        var target = entryPoint.Function;
        var returnType = types.Type(target.ReturnType);
        var call = Emit(SpirvOp.FunctionCall, returnType, [SpirvOperand.Id(functions[target]), .. arguments]);

        foreach (var (io, variable) in outputs) {
            // A render target takes one member of the returned struct; anything else takes the
            // returned value itself. One call either way — the extracts share its result.
            var value = io.Member is { } member
                ? Emit(
                    SpirvOp.CompositeExtract,
                    types.Type(io.Type),
                    SpirvOperand.Id(call),
                    SpirvOperand.Literal((uint)member)
                )
                : call;

            Add(new(SpirvOp.Store, null, null, SpirvOperand.Id(variable), SpirvOperand.Id(value)));
        }

        Add(new(SpirvOp.Return, null, null));
        Add(new(SpirvOp.FunctionEnd, null, null));

        module.AddEntryPoint(ExecutionModel(entryPoint.Stage), main, "main", interfaceIds);

        if (entryPoint.Stage == ShaderStage.Fragment) {
            // A fragment shader has to say where its origin is, and Vulkan only
            // accepts the upper-left one.
            module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        }

        if (entryPoint.WorkgroupSize is { } size) {
            // GLCompute requires LocalSize; without it spirv-val rejects the module. The
            // verifier guarantees one is here on this stage and nowhere else.
            module.AddExecutionMode(
                main,
                SpirvExecutionMode.LocalSize,
                (uint)size.X,
                (uint)size.Y,
                (uint)size.Z
            );
        }
    }

    static SpirvExecutionModel ExecutionModel(ShaderStage stage) =>
        stage switch {
            ShaderStage.Vertex => SpirvExecutionModel.Vertex,
            ShaderStage.Geometry => SpirvExecutionModel.Geometry,
            ShaderStage.Compute => SpirvExecutionModel.GLCompute,
            _ => SpirvExecutionModel.Fragment
        };

    void Report(DiagnosticDescriptor descriptor, string subject) =>
        diagnostics.Add(descriptor, Location.None, subject, "SPIR-V");

    /// <summary>Whether a bool is anywhere inside a type, however deeply.</summary>
    static bool ContainsBool(IrType type) =>
        type switch {
            { Kind: IrTypeKind.Bool } => true,
            IrVectorType vector => vector.Component.Kind == IrTypeKind.Bool,
            IrArrayType array => ContainsBool(array.Element),
            IrStructType structType => structType.Fields.Any(field => ContainsBool(field.Type)),
            _ => false
        };

    static string Describe(IrType type, string what) =>
        type is IrArrayType { Length: null } ? $"The unsized array type of '{what}'" : $"The type '{what}'";

    /// <summary>Builds the module and hands it back for encoding.</summary>
    internal SpirvModule Emit() {
        module.AddCapability(SpirvCapability.Shader);
        extendedInstructions = module.AddExtendedInstructionSet("GLSL.std.450");
        module.SetMemoryModel(SpirvAddressingModel.Logical, SpirvMemoryModel.GLSL450);

        EmitBindings();
        EmitStageInterface();

        // Callees before callers: SPIR-V is read in one pass, and emitting a call
        // to a function that has not been defined yet would mean a forward
        // reference the reader is not obliged to accept.
        foreach (var function in CallGraph.InCallOrder(entryPoint.Function)) {
            functions[function] = module.AllocateId();
        }

        foreach (var function in CallGraph.InCallOrder(entryPoint.Function)) {
            EmitFunction(function);
        }

        EmitEntryPoint();
        return module;
    }
}
