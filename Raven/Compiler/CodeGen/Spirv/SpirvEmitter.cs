using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>
/// Where a global lives. A uniform is a member of one block rather than a
/// variable of its own, so reaching it always begins with a member index.
/// </summary>
/// <param name="Variable">The <c>OpVariable</c> id to start an access chain from.</param>
/// <param name="Storage">Its storage class, which decides the pointer types along the chain.</param>
/// <param name="Member">The member index inside the uniform block, if it is one.</param>
sealed record SpirvGlobal(uint Variable, SpirvStorageClass Storage, int? Member = null);

/// <summary>
/// Emits one SPIR-V module for one entry point.
/// </summary>
/// <remarks>
/// <para>
/// This is a bigger step down than GLSL. The IR's structured control flow has to
/// become basic blocks with explicit merge declarations; its named locals become
/// <c>OpVariable</c>s reached through access chains; and its types have to carry
/// an explicit memory layout, because SPIR-V has no implicit one.
/// </para>
/// <para>
/// What SPIR-V gives back is a separate sampler object, which GLSL lacks — a
/// texture and a sampler stay two bindings and pair up at the sample site, so
/// nothing is dropped.
/// </para>
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
    uint extendedInstructions;
    uint? outputVariable;

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

        module = new SpirvModule(options.Version);
        types = new SpirvTypes(module, (type, what) => Report(BackendDiagnostics.NotExpressible, Describe(type, what)));
    }

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

    // --- Declarations ------------------------------------------------------

    void EmitBindings() {
        var uniforms = shader.Bindings.Where(b => b.Kind == IrBindingKind.Uniform).ToArray();
        var textures = shader.Bindings.Where(b => b.Kind == IrBindingKind.Texture).ToArray();
        var samplers = shader.Bindings.Where(b => b.Kind == IrBindingKind.Sampler).ToArray();

        // The IR numbers each kind of binding from zero, but Vulkan wants one
        // descriptor-set namespace, so they are laid end to end: the block first,
        // then textures, then samplers.
        var binding = 0u;

        if (uniforms.Length > 0) {
            EmitUniformBlock(uniforms, binding++);
        }

        foreach (var texture in textures) {
            globals[texture.Variable] = new SpirvGlobal(
                DeclareOpaque(texture, binding++), SpirvStorageClass.UniformConstant);
        }

        foreach (var sampler in samplers) {
            globals[sampler.Variable] = new SpirvGlobal(
                DeclareOpaque(sampler, binding++), SpirvStorageClass.UniformConstant);
        }

        // Binding defaults are a property of the shader rather than of any one
        // stage, so SpirvBackend says that once however many modules come out.
    }

    void EmitUniformBlock(IrBinding[] uniforms, uint binding) {
        foreach (var uniform in uniforms.Where(u => ContainsBool(u.Type))) {
            // A SPIR-V bool has no size and no memory layout, so it cannot live
            // anywhere the host can see. GLSL hides this by giving it four bytes
            // in a std140 block; SPIR-V does not.
            Report(BackendDiagnostics.NotExpressible, $"The boolean in uniform binding '{uniform.Name}'");
        }

        var members = uniforms.Select(u => u.Type).ToArray();
        var structId = module.AddDeclaration(
            SpirvOp.TypeStruct, null, [.. members.Select(m => SpirvOperand.Id(types.Type(m, layout: true)))]);

        module.AddName(structId, shader.Name + "Uniforms");
        module.Decorate(structId, SpirvDecoration.Block);
        types.DecorateLayout(structId, members);

        for (var i = 0; i < uniforms.Length; i++) {
            module.AddMemberName(structId, i, uniforms[i].Name);
        }

        var variable = module.AddDeclaration(
            SpirvOp.Variable,
            types.Pointer(SpirvStorageClass.Uniform, structId),
            SpirvOperand.Enumerant(SpirvStorageClass.Uniform));

        module.AddName(variable, shader.Name.ToLowerInvariant() + "Uniforms");
        module.Decorate(variable, SpirvDecoration.DescriptorSet, SpirvOperand.Literal(options.DescriptorSet));
        module.Decorate(variable, SpirvDecoration.Binding, SpirvOperand.Literal(binding));

        for (var i = 0; i < uniforms.Length; i++) {
            globals[uniforms[i].Variable] = new SpirvGlobal(variable, SpirvStorageClass.Uniform, i);
        }
    }

    uint DeclareOpaque(IrBinding resource, uint binding) {
        var variable = module.AddDeclaration(
            SpirvOp.Variable,
            types.Pointer(SpirvStorageClass.UniformConstant, types.Type(resource.Type)),
            SpirvOperand.Enumerant(SpirvStorageClass.UniformConstant));

        module.AddName(variable, resource.Name);
        module.Decorate(variable, SpirvDecoration.DescriptorSet, SpirvOperand.Literal(options.DescriptorSet));
        module.Decorate(variable, SpirvDecoration.Binding, SpirvOperand.Literal(binding));
        return variable;
    }

    void EmitStageInterface() {
        var location = 0u;

        foreach (var input in entryPoint.Inputs) {
            var variable = DeclareStageVariable(input, SpirvStorageClass.Input, "in_" + input.Name);
            module.Decorate(variable, SpirvDecoration.Location, SpirvOperand.Literal(location++));
            inputs.Add((input, variable));
        }

        if (entryPoint.Output is not { } output) {
            return;
        }

        outputVariable = DeclareStageVariable(output, SpirvStorageClass.Output, "out_" + output.Name);

        if (OutputGoesToBuiltIn()) {
            module.Decorate(
                outputVariable.Value, SpirvDecoration.BuiltIn, SpirvOperand.Enumerant(SpirvBuiltIn.Position));
        } else {
            module.Decorate(outputVariable.Value, SpirvDecoration.Location, SpirvOperand.Literal(0));
        }
    }

    uint DeclareStageVariable(IrStageIo io, SpirvStorageClass storage, string name) {
        // Vulkan has no boolean interface type, and an aggregate would need a
        // location for every leaf. Both are rejected rather than mis-emitted.
        if (io.Type is not (IrScalarType { Kind: not IrTypeKind.Bool } or IrVectorType { Component.Kind: not IrTypeKind.Bool })) {
            Report(
                BackendDiagnostics.NotExpressible,
                $"The type '{io.Type.Name}' of stage {(storage == SpirvStorageClass.Input ? "input" : "output")} "
                + $"'{io.Name}'");
        }

        var variable = module.AddDeclaration(
            SpirvOp.Variable,
            types.Pointer(storage, types.Type(io.Type)),
            SpirvOperand.Enumerant(storage));

        module.AddName(variable, name);
        interfaceIds.Add(variable);
        return variable;
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
        pointers.Clear();
        opaqueParameters.Clear();
        loops.Clear();

        var returnType = types.Type(function.ReturnType);
        var parameterTypes = function.Parameters.Select(p => types.Type(p.Type)).ToArray();
        var id = functions[function];

        module.AddName(id, function.Name);
        Add(new SpirvInstruction(
            SpirvOp.Function,
            returnType,
            id,
            SpirvOperand.Enumerant(SpirvFunctionControl.None),
            SpirvOperand.Id(types.Function(returnType, parameterTypes))));

        var parameterIds = new uint[function.Parameters.Count];

        for (var i = 0; i < function.Parameters.Count; i++) {
            parameterIds[i] = module.AllocateId();
            module.AddName(parameterIds[i], function.Parameters[i].Name);
            Add(new SpirvInstruction(SpirvOp.FunctionParameter, parameterTypes[i], parameterIds[i]));
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

            copies.Add((DeclareLocal(parameter), parameterIds[i]));
        }

        foreach (var local in function.Locals) {
            DeclareLocal(local);
        }

        foreach (var (pointer, value) in copies) {
            Add(new SpirvInstruction(SpirvOp.Store, null, null, SpirvOperand.Id(pointer), SpirvOperand.Id(value)));
        }

        EmitBlock(function.Body);

        // A body that runs off its end still needs a terminator. Returning is
        // right for a void function; for any other the verifier has already
        // ruled the path out, so it can only be unreachable.
        if (!terminated) {
            Add(function.ReturnType.IsVoid
                ? new SpirvInstruction(SpirvOp.Return, null, null)
                : new SpirvInstruction(SpirvOp.Unreachable, null, null));
        }

        Add(new SpirvInstruction(SpirvOp.FunctionEnd, null, null));
    }

    uint DeclareLocal(IrVariable variable) {
        var pointer = module.AllocateId();
        Add(new SpirvInstruction(
            SpirvOp.Variable,
            types.Pointer(SpirvStorageClass.Function, types.Type(variable.Type)),
            pointer,
            SpirvOperand.Enumerant(SpirvStorageClass.Function)));

        module.AddName(pointer, variable.Name);
        pointers[variable] = pointer;
        return pointer;
    }

    /// <summary>
    /// The <c>main</c> the pipeline calls: it reads the stage inputs, hands them
    /// to the user's function, and writes the result to the stage output.
    /// </summary>
    void EmitEntryPoint() {
        values.Clear();
        pointers.Clear();
        loops.Clear();

        var main = module.AllocateId();
        module.AddName(main, "main");

        Add(new SpirvInstruction(
            SpirvOp.Function,
            types.Void,
            main,
            SpirvOperand.Enumerant(SpirvFunctionControl.None),
            SpirvOperand.Id(types.Function(types.Void, []))));

        BeginBlock(module.AllocateId());

        var arguments = new List<SpirvOperand>();

        foreach (var (io, variable) in inputs) {
            arguments.Add(SpirvOperand.Id(Emit(SpirvOp.Load, types.Type(io.Type), SpirvOperand.Id(variable))));
        }

        var target = entryPoint.Function;
        var returnType = types.Type(target.ReturnType);
        var call = Emit(SpirvOp.FunctionCall, returnType, [SpirvOperand.Id(functions[target]), .. arguments]);

        if (outputVariable is { } output && !target.ReturnType.IsVoid) {
            Add(new SpirvInstruction(SpirvOp.Store, null, null, SpirvOperand.Id(output), SpirvOperand.Id(call)));
        }

        Add(new SpirvInstruction(SpirvOp.Return, null, null));
        Add(new SpirvInstruction(SpirvOp.FunctionEnd, null, null));

        module.AddEntryPoint(ExecutionModel(entryPoint.Stage), main, "main", interfaceIds);

        if (entryPoint.Stage == ShaderStage.Pixel) {
            // A fragment shader has to say where its origin is, and Vulkan only
            // accepts the upper-left one.
            module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        }
    }

    static SpirvExecutionModel ExecutionModel(ShaderStage stage) => stage switch {
        ShaderStage.Vertex => SpirvExecutionModel.Vertex,
        ShaderStage.Geometry => SpirvExecutionModel.Geometry,
        ShaderStage.Compute => SpirvExecutionModel.GLCompute,
        _ => SpirvExecutionModel.Fragment
    };

    void Report(DiagnosticDescriptor descriptor, string subject) =>
        diagnostics.Add(descriptor, Location.None, subject, "SPIR-V");

    /// <summary>Whether a bool is anywhere inside a type, however deeply.</summary>
    static bool ContainsBool(IrType type) => type switch {
        { Kind: IrTypeKind.Bool } => true,
        IrVectorType vector => vector.Component.Kind == IrTypeKind.Bool,
        IrArrayType array => ContainsBool(array.Element),
        IrStructType structType => structType.Fields.Any(field => ContainsBool(field.Type)),
        _ => false
    };

    static string Describe(IrType type, string what) =>
        type is IrArrayType { Length: null } ? $"The unsized array type of '{what}'" : $"The type '{what}'";
}
