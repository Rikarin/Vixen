// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Binding;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Vixen.Raven.Symbols.Source;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Lowering;

/// <summary>
///     Lowers a bound compilation to the target-independent <see cref="IrModule" />.
/// </summary>
/// <remarks>
///     <para>
///         The three jobs here are erasure, explicitness and desugaring. Erasure: a
///         shader's fields have no runtime object, so <c>self.scale</c> becomes a global
///         binding and <c>self</c> disappears. Explicitness: every conversion, every
///         load and every store is its own instruction. Desugaring: <c>for</c> loops,
///         compound assignment and <c>++</c> become plain loops, loads and stores.
///     </para>
///     <para>
///         Lowering assumes the compilation bound cleanly. Anything the binder already
///         reported flows in as <see cref="ErrorTypeSymbol" /> and is passed over
///         silently rather than reported twice.
///     </para>
/// </remarks>
public sealed partial class Lowerer {
    readonly Dictionary<Symbol, List<BoundBody>> bodies = [];
    readonly Compilation compilation;
    readonly DiagnosticBag diagnostics;
    readonly Dictionary<FieldSymbol, IrVariable> globals = [];
    readonly IrModule module;
    readonly Dictionary<NamedTypeSymbol, IrStructType> structs = [];

    /// <summary>
    ///     The lowered shader per shader type, so a <c>compose</c> slot's implementation can be
    ///     found from the symbol its consumer names.
    /// </summary>
    readonly Dictionary<NamedTypeSymbol, IrShader> shaders = [];

    readonly Dictionary<TupleTypeSymbol, IrStructType> tuples = [];
    readonly Dictionary<TypeSymbol, IrType> typeCache = [];

    IrBlock currentBlock = new();
    IrFunction? currentFunction;
    NamedTypeSymbol? currentType;
    IrVariable? selfLocal;
    IrVariable? selfParameter;
    readonly Dictionary<Symbol, IrVariable> variables = [];
    readonly Dictionary<(Symbol Member, BoundBodyKind Kind), IrFunction> functions = [];
    readonly Dictionary<(Symbol Member, BoundBodyKind Kind), FunctionShell> shells = [];

    /// <summary>
    ///     A function created before its body was lowered: the signature, and the mapping
    ///     from parameter symbols to the IR variables holding them.
    /// </summary>
    /// <remarks>
    ///     Parameters are created with the shell rather than with the body because an entry
    ///     point reads them off the function, and an entry point can be built before the
    ///     body it belongs to has been lowered.
    /// </remarks>
    sealed record FunctionShell(
        IrFunction Function,
        IrVariable? SelfParameter,
        IrVariable? SelfLocal,
        (Symbol Symbol, IrVariable Variable)[] Parameters
    );

    /// <summary>The receiver's storage, whether it arrived as a parameter or is being built.</summary>
    IrPlace? SelfPlace =>
        selfParameter is not null ? new(selfParameter)
        : selfLocal is not null ? new IrPlace(selfLocal)
        : null;

    /// <summary>True while lowering a constructor that returns the value it builds.</summary>
    bool IsConstructingSelf => selfLocal is not null;

    // --- Emission helpers --------------------------------------------------

    IrFunction Function => currentFunction!;

    Lowerer(Compilation compilation, DiagnosticBag diagnostics) {
        this.compilation = compilation;
        this.diagnostics = diagnostics;
        module = new(compilation.AssemblyName);
    }

    /// <summary>Lowers every shader and type in the compilation.</summary>
    public static IrModule Lower(Compilation compilation, DiagnosticBag diagnostics) =>
        LowerWithLinks(compilation, diagnostics).Module;

    /// <summary>
    ///     Lowers the compilation and keeps the map from symbols to what they lowered to.
    /// </summary>
    /// <remarks>
    ///     Only <c>.rvnlib</c> needs this. The module alone is what a backend consumes — it is
    ///     deliberately symbol-free — but writing a library means recording which IR function each
    ///     method's body became, and that link exists nowhere else. Everything else keeps calling
    ///     <see cref="Lower" />.
    /// </remarks>
    public static LoweringResult LowerWithLinks(Compilation compilation, DiagnosticBag diagnostics) {
        var lowerer = new Lowerer(compilation, diagnostics);
        var module = lowerer.LowerModule();

        return new(
            module,
            lowerer.functions,
            lowerer.structs,
            lowerer.importedFunctions,
            lowerer.importedStructs,
            lowerer.importedFunctionNames,
            lowerer.importedStructNames
        );
    }

    IrModule LowerModule() {
        CollectBodies();

        var types = compilation.GetAllTypes();
        var link = LinkReferences(types);

        // Shells first: a function body can call anything in the module, and a
        // struct can hold a field of a struct declared later.
        foreach (var type in types) {
            switch (type.TypeKind) {
                case TypeKind.Struct: {
                    var structType = new IrStructType(type.Name);
                    structs[type] = structType;
                    module.Add(structType);
                    break;
                }
            }
        }

        // Struct fields before any body, because a field access lowers to an index into them: a
        // struct declared after its first user would otherwise have an empty field list when that
        // user's body was lowered. Separate from the shell pass because resolving a field's type can
        // reach another struct, which needs its shell to exist already.
        foreach (var type in types) {
            if (type.TypeKind == TypeKind.Struct) {
                DeclareStructFields(type);
            }
        }

        // Function shells, for the same reason the struct shells exist: a body can call a
        // function declared later in the module. `compose` makes that ordinary — the shader
        // filling a slot sits wherever the material author put it — but it was always
        // possible between two structs.
        foreach (var type in types) {
            if (type.TypeKind is TypeKind.Shader or TypeKind.Struct) {
                DeclareMemberFunctions(type);
            }
        }

        // The libraries' functions after the compilation's own shells, so a name a source
        // declaration uses is the one that keeps it and the library's copy gives way.
        link?.LinkFunctions();

        foreach (var type in types) {
            switch (type.TypeKind) {
                case TypeKind.Shader:
                    LowerShader(type);
                    break;
                case TypeKind.Struct:
                    LowerStruct(type);
                    break;
            }
        }

        // After every shader exists, because a slot's implementation may be declared later in the
        // file than the shader that composes it, and its globals only exist once it is lowered.
        MergeComposedInterfaces(types);

        // After every body exists, and after pruning: a stream's direction comes from what the
        // stage's reachable code does with it, which is only knowable once the module is settled.
        ImportPruner.Prune(module, importedStructs, importedFunctions);
        ResolveStreamDirections();

        return module;
    }

    /// <summary>
    ///     Gives every shader the bindings and streams of the shaders it composes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A <c>compose</c> slot's implementation is its own <see cref="IrShader" /> with its own
    ///         bindings, and only the consuming shader's reach the translation unit a backend emits.
    ///         So a feature with a single material parameter produced GLSL naming an identifier it
    ///         never declared — <c>glslc</c> rejected it and Raven said nothing, which is the same
    ///         shape as the inheritance defects in docs/plan/07 § J. Resolution, calling and pruning
    ///         all worked; the interface did not.
    ///     </para>
    ///     <para>
    ///         Merged in lowering rather than in each emitter, because <c>BindingPlan</c>,
    ///         <c>StreamPlan</c> and the reflection all read the shader — so doing it here is what
    ///         makes the descriptor a host binds against the same one both backends emit. Two
    ///         emitters each patching their own interface is how they come to differ.
    ///     </para>
    ///     <para>
    ///         The <em>same</em> <see cref="IrVariable" /> rather than a copy: the implementation's
    ///         body was lowered against it, so a copy would leave the body reading storage the
    ///         consumer never declared — the original bug with an extra step.
    ///     </para>
    ///     <para>
    ///         Every binding the implementation declares, not only those the consumer's code reaches.
    ///         A shader's own unread bindings are kept too, and for the same reason: the descriptor
    ///         set layout is what the host writes against, and a material parameter vanishing from
    ///         the reflection because this variant happened not to read it is a far worse failure
    ///         than a spare slot.
    ///     </para>
    /// </remarks>
    void MergeComposedInterfaces(IReadOnlyList<NamedTypeSymbol> types) {
        foreach (var type in types) {
            if (type.TypeKind != TypeKind.Shader || !shaders.TryGetValue(type, out var shader)) {
                continue;
            }

            foreach (var contributor in ComposedShaders(type)) {
                if (!shaders.TryGetValue(contributor, out var source)) {
                    continue;
                }

                MergeInterface(shader, source);
            }
        }
    }

    /// <summary>
    ///     Every shader reachable through this shader's <c>compose</c> slots, transitively.
    /// </summary>
    /// <remarks>
    ///     Transitive because a feature may compose one of its own: a layered material's coat
    ///     feature filling a slot with a BRDF. The visited set also covers the same implementation
    ///     bound to two slots, which must contribute its bindings once.
    /// </remarks>
    static IEnumerable<NamedTypeSymbol> ComposedShaders(NamedTypeSymbol type) {
        HashSet<NamedTypeSymbol> visited = [];
        Queue<NamedTypeSymbol> pending = new([type]);

        while (pending.Count > 0) {
            foreach (var member in pending.Dequeue().GetMembers()) {
                if (member is FieldSymbol { IsCompose: true, ComposedType: { } bound }
                    && bound.TypeKind == TypeKind.Shader
                    && visited.Add(bound)) {
                    pending.Enqueue(bound);
                    yield return bound;
                }
            }
        }
    }

    /// <summary>
    ///     Copies one shader's bindings and streams onto another, skipping what it already has.
    /// </summary>
    static void MergeInterface(IrShader target, IrShader source) {
        var present = target.Bindings.Select(binding => binding.Variable).ToHashSet();

        // Continue the target's own numbering. `IrBinding.Slot` counts per kind and is what the
        // verifier checks for duplicates; the (set, binding) pair a backend emits comes from
        // BindingPlan, which renumbers from the merged list.
        Dictionary<IrBindingKind, int> slots = [];
        foreach (var binding in target.Bindings) {
            slots[binding.Kind] = slots.GetValueOrDefault(binding.Kind) + 1;
        }

        foreach (var binding in source.Bindings) {
            if (!present.Add(binding.Variable)) {
                continue;
            }

            slots.TryGetValue(binding.Kind, out var slot);
            slots[binding.Kind] = slot + 1;

            target.Add(
                new IrBinding(
                    binding.Variable,
                    binding.Kind,
                    slot,
                    binding.Semantic,
                    binding.Set,
                    // Qualified by the shader that declares it — see IrBinding.Name. `binding.Name`
                    // rather than the variable's, so a transitive contribution keeps the whole path.
                    $"{source.Name}.{binding.Name}"
                )
            );
        }

        // Streams have the same problem for the same reason. Appended, so the consumer's own keep
        // their locations — a stream's location is its index in this list, which is what makes the
        // writing and reading stages agree.
        var streams = target.Streams.Select(stream => stream.Variable).ToHashSet();
        foreach (var stream in source.Streams) {
            if (streams.Add(stream.Variable)) {
                target.Add(stream);
            }
        }
    }

    void CollectBodies() {
        foreach (var tree in compilation.SyntaxTrees) {
            foreach (var body in compilation.GetSemanticModel(tree).GetBoundBodies()) {
                if (!bodies.TryGetValue(body.Member, out var list)) {
                    bodies[body.Member] = list = [];
                }

                list.Add(body);
            }
        }
    }

    BoundBody? FindBody(Symbol member, BoundBodyKind kind) =>
        bodies.GetValueOrDefault(member)?.FirstOrDefault(b => b.Kind == kind);

    // --- Shaders -----------------------------------------------------------

    void LowerShader(NamedTypeSymbol type) {
        var shader = new IrShader(type.Name);
        module.Add(shader);
        shaders[type] = shader;

        var slots = new Dictionary<IrBindingKind, int>();

        ReportInheritanceNotFlattened(type);
        DeclareCompileTimeConstants(type, shader);
        DeclareStreams(type, shader);

        foreach (var member in type.GetMembers()) {
            // A `const` field is folded at every use, so it needs no binding; a `stream` is
            // per-invocation, so it is in the pipeline's interface rather than in a descriptor.
            // `IsBinding` is that rule, shared with the binder's writability check.
            if (member is not FieldSymbol { IsBinding: true } field) {
                continue;
            }

            var irType = LowerType(field.Type, field.DeclaringSyntax);
            if (irType.IsVoid) {
                continue;
            }

            var kind = field.ResourceKind switch {
                ResourceKind.Texture => IrBindingKind.Texture,
                ResourceKind.Sampler => IrBindingKind.Sampler,
                ResourceKind.StorageBuffer => IrBindingKind.StorageBuffer,
                _ => IrBindingKind.Uniform
            };

            slots.TryGetValue(kind, out var slot);
            slots[kind] = slot + 1;

            var variable = new IrVariable(field.Name, irType, IrVariableKind.Global);
            globals[field] = variable;
            shader.Add(
                new IrBinding(
                    variable,
                    kind,
                    slot,
                    field.SemanticName,
                    field.ResourceSet,
                    writable: field.Type.IsWritableResource
                )
            );
        }

        LowerMemberFunctions(type, shader.Add);
        LowerBindingInitializers(type, shader);

        foreach (var member in type.GetMembers()) {
            if (member is MethodSymbol { Stage: not ShaderStage.None } method
                && functions.TryGetValue((method, BoundBodyKind.Method), out var function)) {
                shader.Add(BuildEntryPoint(method, function));
            }
        }
    }

    /// <summary>
    ///     Records what the shader can be varied by: its <c>[Permutation]</c> keys and its
    ///     <c>val</c> type parameters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both are gone from the lowered body by design — folded to constants, their dead
    ///         branches eliminated — so this is the only place the fact survives. Without it nothing
    ///         downstream can answer "what variants does this shader have?", which is what a host
    ///         needs to enumerate them and what the C# key generator turns into a
    ///         <c>PermutationKey</c>.
    ///     </para>
    ///     <para>
    ///         Read through <see cref="FieldSymbol.DeclaredValue" /> rather than
    ///         <c>ConstantValue</c>, deliberately: the latter records a permutation use, so
    ///         describing a shader here would add keys to the cache key that the body never read.
    ///     </para>
    /// </remarks>
    void DeclareCompileTimeConstants(NamedTypeSymbol type, IrShader shader) {
        foreach (var member in type.GetMembers()) {
            switch (member) {
                case FieldSymbol { IsValueParameter: true } parameter: {
                    if (LowerType(parameter.Type, parameter.DeclaringSyntax) is { IsVoid: false } irType) {
                        shader.Add(new IrValueParameter(parameter.Name, irType));
                    }

                    break;
                }

                case FieldSymbol { IsPermutation: true } key: {
                    if (LowerType(key.Type, key.DeclaringSyntax) is { IsVoid: false } irType) {
                        shader.Add(new IrPermutation(key.Name, irType, key.DeclaredValue));
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    ///     Declares the shader's <c>stream</c> fields as module-scope globals.
    /// </summary>
    /// <remarks>
    ///     A global because that is what a stage interface is in both targets — a SPIR-V
    ///     <c>Input</c>/<c>Output</c> variable and a GLSL <c>in</c>/<c>out</c> are both module
    ///     scope — so a read lowers to an ordinary load and a write to an ordinary store, with no
    ///     new instruction and nothing for the body lowering to know about. Which direction each
    ///     stage needs is worked out afterwards, from the call graph:
    ///     see <see cref="ResolveStreamDirections" />.
    /// </remarks>
    void DeclareStreams(NamedTypeSymbol type, IrShader shader) {
        foreach (var member in type.GetMembers()) {
            if (member is not FieldSymbol { IsStream: true, IsConst: false, IsCompose: false } field) {
                continue;
            }

            var irType = LowerType(field.Type, field.DeclaringSyntax);
            if (irType.IsVoid) {
                continue;
            }

            var variable = new IrVariable(field.Name, irType, IrVariableKind.Global);
            globals[field] = variable;
            shader.Add(new IrStream(variable));
        }
    }

    /// <summary>
    ///     Decides, for every entry point, which of its shader's streams are inputs and which are
    ///     outputs.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Runs once the whole module is lowered, because it reads bodies. A stream the stage
    ///         stores to is an output. A stream is an input only when the stage can <em>read it
    ///         before writing it</em> — not merely when it reads it somewhere, which is the
    ///         distinction that keeps <c>normalWS = n; return normalWS</c> in a vertex stage from
    ///         declaring a vertex attribute nobody asked for. A read of a stream this stage produces
    ///         resolves to the output variable, which both targets allow: only SPIR-V's
    ///         <c>Input</c> is read-only.
    ///     </para>
    ///     <para>
    ///         "Before" is a pre-order walk of the stage's code with calls expanded at their call
    ///         sites — exact for the straight-line code shaders are made of, and conservative the
    ///         safe way otherwise, since declaring an input the stage did not need costs a location
    ///         while missing one it did need would read undefined values.
    ///     </para>
    ///     <para>
    ///         Deriving the direction instead of declaring it is the point of the feature. A
    ///         <c>compose</c>d surface function three calls deep can write <c>normalWS</c> and the
    ///         vertex stage grows an output, with no signature between them mentioning it. Which is
    ///         also why reachability rather than shader membership decides what belongs to a stage:
    ///         a composed implementation's functions live in a different <see cref="IrShader" />.
    ///     </para>
    /// </remarks>
    void ResolveStreamDirections() {
        foreach (var shader in module.Shaders) {
            if (shader.Streams.Count == 0) {
                continue;
            }

            var streams = shader.Streams.Select(stream => stream.Variable).ToHashSet();

            foreach (var entryPoint in shader.EntryPoints) {
                Dictionary<IrVariable, bool> firstUseIsRead = [];
                HashSet<IrVariable> written = [];

                CollectStreamUses(entryPoint.Function.Body, streams, firstUseIsRead, written, []);

                // Declaration order, so the locations a plan assigns come out ascending.
                entryPoint.SetStreams(
                    [.. shader.Streams.Where(s => firstUseIsRead.GetValueOrDefault(s.Variable))],
                    [.. shader.Streams.Where(s => written.Contains(s.Variable))]
                );

                ReportUnusableStreams(shader, entryPoint);
                ReportUnconsumedStreams(shader, entryPoint);
            }
        }
    }

    /// <summary>
    ///     Refuses a stream a compute stage touches, in either direction.
    /// </summary>
    /// <remarks>
    ///     A compute dispatch has no stage before it and none after it, so there is no interface for
    ///     a stream to occupy — and the streams were left declared but not emitted, so a store
    ///     assigned to an identifier the translation unit never declared. Reported for reads too:
    ///     the value would be undefined rather than merely unread.
    /// </remarks>
    void ReportUnusableStreams(IrShader shader, IrEntryPoint entryPoint) {
        if (entryPoint.Stage != ShaderStage.Compute) {
            return;
        }

        foreach (var stream in entryPoint.StreamInputs.Concat(entryPoint.StreamOutputs).Distinct()) {
            diagnostics.Add(
                LoweringDiagnostics.StreamInComputeStage,
                LocationOf(SyntaxOf(shader, stream)),
                stream.Name,
                entryPoint.Function.Name
            );
        }
    }

    /// <summary>
    ///     Warns about a stream written by a stage that nothing downstream can read.
    /// </summary>
    /// <remarks>
    ///     A fragment stage's outputs are render targets, not interstage values — location 0 is
    ///     target 0 — so a stream written there goes nowhere. The shader still compiles, so this is
    ///     a warning on the RVN2091 pattern: the code is correct and the author believes something
    ///     untrue about where the value ends up.
    /// </remarks>
    void ReportUnconsumedStreams(IrShader shader, IrEntryPoint entryPoint) {
        if (entryPoint.Stage != ShaderStage.Pixel) {
            return;
        }

        foreach (var stream in entryPoint.StreamOutputs) {
            diagnostics.Add(
                LoweringDiagnostics.StreamNotConsumed,
                LocationOf(SyntaxOf(shader, stream)),
                stream.Name,
                entryPoint.Function.Name
            );
        }
    }

    /// <summary>The declaration a lowered stream came from, so the warning has a span.</summary>
    SyntaxNode? SyntaxOf(IrShader shader, IrStream stream) =>
        globals.FirstOrDefault(entry => ReferenceEquals(entry.Value, stream.Variable)).Key?.DeclaringSyntax;

    /// <summary>
    ///     Walks a body in execution order, recording for each stream whether its first use is a
    ///     read, and which streams are written at all.
    /// </summary>
    /// <param name="statement">The statement to walk.</param>
    /// <param name="streams">The shader's streams, so other globals are passed over.</param>
    /// <param name="firstUseIsRead">
    ///     Filled in once per stream, at its first use: true for a load, false for a store.
    /// </param>
    /// <param name="written">Every stream stored to anywhere.</param>
    /// <param name="active">
    ///     The functions on the current call path. Raven has no recursion, but a set costs nothing
    ///     and a cycle in hand-built IR would otherwise not terminate.
    /// </param>
    static void CollectStreamUses(
        IrStatement statement,
        HashSet<IrVariable> streams,
        Dictionary<IrVariable, bool> firstUseIsRead,
        HashSet<IrVariable> written,
        HashSet<IrFunction> active
    ) {
        void Note(IrVariable root, bool isRead) {
            // First use wins; a later read of a stream this stage already wrote does not make it an
            // input, because the value it wants is the one it produced.
            firstUseIsRead.TryAdd(root, isRead);

            if (!isRead) {
                written.Add(root);
            }
        }

        void Walk(IrStatement inner) => CollectStreamUses(inner, streams, firstUseIsRead, written, active);

        switch (statement) {
            case IrBlock block:
                foreach (var nested in block.Statements) {
                    Walk(nested);
                }

                break;

            case IrLoadInstruction load when streams.Contains(load.Place.Root):
                Note(load.Place.Root, isRead: true);
                break;

            case IrStoreInstruction store when streams.Contains(store.Place.Root):
                // A partial write — one lane of a vector, one column of a matrix — keeps the rest
                // of the value, so it reads before it writes. Saying so here rather than in each
                // backend keeps the two agreeing about what the interface is.
                Note(store.Place.Root, isRead: store.Place.Chain.Count > 0);
                written.Add(store.Place.Root);
                break;

            // Expanded at the call site: a stream is declared on the shader precisely so that a
            // function anywhere in the stage's call graph can use it, so the walk has to follow
            // the calls to see the uses in the order they happen.
            case IrCallInstruction call when active.Add(call.Function):
                try {
                    Walk(call.Function.Body);
                } finally {
                    active.Remove(call.Function);
                }

                break;

            case IrIfStatement conditional:
                Walk(conditional.Then);

                if (conditional.Else is { } otherwise) {
                    Walk(otherwise);
                }

                break;

            case IrLoopStatement loop:
                Walk(loop.Condition);
                Walk(loop.Body);

                if (loop.Continue is { } step) {
                    Walk(step);
                }

                break;
        }
    }

    static IrEntryPoint BuildEntryPoint(MethodSymbol method, IrFunction function) {
        var inputs = method.Parameters
            .Select((p, i) => new IrStageIo(p.Name, function.Parameters[i].Type, p.SemanticName))
            .ToArray();

        var output = function.ReturnType.IsVoid
            ? null
            : new IrStageIo("result", function.ReturnType, method.SemanticName);

        // Only on the stage that has workgroups. A size the binder warned about (RVN2106) is
        // dropped here rather than carried to a backend that has nowhere to put it.
        var workgroupSize = method.Stage == ShaderStage.Compute ? method.WorkgroupSize : null;

        return new(method.Stage, function, inputs, output, workgroupSize);
    }

    /// <summary>
    ///     Emits the stores that give bindings their declared defaults, as one block
    ///     a backend can run before the first stage invocation.
    /// </summary>
    void LowerBindingInitializers(NamedTypeSymbol type, IrShader shader) {
        var initializer = new IrFunction($"{type.Name}.<init>", IrScalarType.Void);

        BeginFunction(initializer, type);

        foreach (var member in type.GetMembers()) {
            if (member is not FieldSymbol { IsConst: false, IsCompose: false } field
                || !globals.TryGetValue(field, out var variable)
                || FindBody(field, BoundBodyKind.FieldInitializer) is not { } body) {
                continue;
            }

            // The initializer body is `return <expression>`; take the expression.
            if (SingleReturnValue(body) is not { } expression) {
                continue;
            }

            var value = LowerExpression(expression);
            Emit(new IrStoreInstruction(new(variable), value));
        }

        EndFunction();
        shader.Initializer.AddRange(initializer.Body.Statements);
    }

    static BoundExpression? SingleReturnValue(BoundBody body) =>
        body.Body.Statements is [BoundReturnStatement { Expression: { } expression }] ? expression : null;

    // --- Structs -----------------------------------------------------------

    /// <summary>
    ///     Fills in a struct's fields.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="LowerStruct" /> and run over every struct first, because a field
    ///     access lowers to an <em>index</em> into this list: a body that touches a struct whose
    ///     fields were not yet populated finds no index and gets <c>RVN3003</c>, "no storage the
    ///     target can address". That happened for any struct declared later in the file than its
    ///     first user — which is ordinary, and which the binder accepts without complaint because it
    ///     resolves the type perfectly well.
    /// </remarks>
    void DeclareStructFields(NamedTypeSymbol type) {
        List<IrField> fields = [];

        foreach (var member in type.GetMembers()) {
            if (member is not FieldSymbol { IsConst: false, IsCompose: false, IsStream: false } field) {
                continue;
            }

            var irType = LowerType(field.Type, field.DeclaringSyntax);
            if (!irType.IsVoid) {
                fields.Add(new(field.Name, irType));
            }
        }

        structs[type].SetFields(fields.ToArray());
    }

    void LowerStruct(NamedTypeSymbol type) {
        ReportInheritanceNotFlattened(type);
        LowerMemberFunctions(type, module.Add);
    }

    /// <summary>
    ///     Reports the parts of inheritance lowering does not implement: an inherited field, and an
    ///     <c>override</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The symbol layer models inheritance correctly — member lookup walks the base chain,
    ///         nearest first — so the binder accepts <c>shader Derived : Base</c> and resolves
    ///         everything. Lowering is where it stops: a type contributes only its <em>declared</em>
    ///         members, so a base's fields never become bindings or struct fields, and a base's
    ///         method body is lowered once against its own type rather than once per derived shader.
    ///     </para>
    ///     <para>
    ///         Three silent failures came out of that, which is why these are errors rather than gaps
    ///         left open. An inherited uniform emitted GLSL naming an undeclared identifier (SPIR-V
    ///         at least reported <c>RVN4002</c>); an inherited struct field lowered to the
    ///         <em>wrong field</em>, because access is by index and a derived type's indices are its
    ///         own; and an <c>override</c> was dropped, so a base's own call kept reaching the base's
    ///         method. All three compiled without a word.
    ///     </para>
    ///     <para>
    ///         Deliberately narrow rather than rejecting every base type. Inheritance used purely to
    ///         supply a member — a stateless base whose method satisfies a protocol that a
    ///         <c>compose</c> slot resolves against — lowers correctly today, and taking that down
    ///         with the broken cases would remove a working mechanism. Fixing the rest means
    ///         flattening: the derived type takes the base's fields into its own layout and its own
    ///         copy of every inherited body, overrides winning. That is Stride's mixin resolver for a
    ///         source-declared chain; until something needs it, <c>compose</c> plus a protocol is the
    ///         composition that works. See docs/plan/07 § J.
    ///     </para>
    /// </remarks>
    void ReportInheritanceNotFlattened(NamedTypeSymbol type) {
        // A protocol base contributes no storage and no bodies, so it is unaffected — and it is
        // what `compose` slots are typed against.
        if (type.BaseType is not { } baseType || baseType.IsErrorType) {
            return;
        }

        // A field on the base never reaches the derived layout. Reported on the base's storage
        // rather than at each use, so it is said once and names the cause.
        foreach (var inherited in InstanceFields(baseType)) {
            diagnostics.Add(
                LoweringDiagnostics.ConstructNotSupported,
                LocationOf(type.DeclaringSyntax),
                $"The field '{inherited.Name}' that '{type.Name}' inherits from "
                + $"'{baseType.ToDisplayString()}' — a base type's storage is not flattened, so it"
            );
        }

        // An override does not replace the base's member: the base's own calls were bound to the
        // base's method, and its body is lowered once.
        foreach (var member in type.GetMembers()) {
            if (member is MethodSymbol { MethodKind: MethodKind.Ordinary } method
                && DeclaresOverride(method)
                && FindOverridden(baseType, method) is not null) {
                diagnostics.Add(
                    LoweringDiagnostics.ConstructNotSupported,
                    LocationOf(method.DeclaringSyntax),
                    $"'{method.Name}' overriding '{baseType.ToDisplayString()}.{method.Name}' — "
                    + "the base's own calls still reach the base's method, so it"
                );
            }
        }
    }

    /// <summary>Every field of a type and its bases that occupies storage.</summary>
    static IEnumerable<FieldSymbol> InstanceFields(NamedTypeSymbol type) {
        for (var current = type; current is not null; current = current.BaseType) {
            foreach (var member in current.GetMembers()) {
                if (member is FieldSymbol {
                        IsConst: false, IsCompose: false, IsValueParameter: false, IsStream: false
                    } field) {
                    yield return field;
                }
            }
        }
    }

    static bool DeclaresOverride(MethodSymbol method) =>
        method.DeclaringSyntax is MethodDeclarationSyntax declaration
        && DeclarationFacts.Has(declaration.Modifiers, SyntaxKind.OverrideKeyword);

    /// <summary>The base-chain method a declaration overrides, matched by name and arity.</summary>
    static MethodSymbol? FindOverridden(NamedTypeSymbol baseType, MethodSymbol method) {
        for (var current = baseType; current is not null; current = current.BaseType) {
            foreach (var candidate in current.GetMembers(method.Name).OfType<MethodSymbol>()) {
                if (candidate.Parameters.Count == method.Parameters.Count) {
                    return candidate;
                }
            }
        }

        return null;
    }

    // --- Functions ---------------------------------------------------------

    /// <summary>
    ///     Every method and property accessor of a type that has a body, with the IR name it
    ///     gets. Walked twice — once to declare the signatures, once to lower the bodies — so
    ///     the two passes agree by construction.
    /// </summary>
    /// <param name="report">
    ///     Whether to report a missing body or an unsupported member kind. Only the second
    ///     pass does, so nothing is said twice.
    /// </param>
    IEnumerable<(string Name, BoundBody Body)> MemberBodies(NamedTypeSymbol type, bool report) {
        HashSet<string> used = [];

        foreach (var member in type.GetMembers()) {
            switch (member) {
                case MethodSymbol method when method.MethodKind
                    is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.Operator: {
                    var kind = method.IsConstructor ? BoundBodyKind.Constructor : BoundBodyKind.Method;
                    if (FindBody(method, kind) is not { } body) {
                        if (report) {
                            diagnostics.Add(
                                LoweringDiagnostics.MissingBody,
                                LocationOf(method.DeclaringSyntax),
                                method.ToDisplayString()
                            );
                        }

                        continue;
                    }

                    yield return (Unique(used, FunctionName(type, method)), body);
                    break;
                }

                case MethodSymbol method:
                    if (report) {
                        diagnostics.Add(
                            LoweringDiagnostics.ConstructNotSupported,
                            LocationOf(method.DeclaringSyntax),
                            $"A {Describe(method.MethodKind)} declaration"
                        );
                    }

                    break;

                case PropertySymbol property: {
                    foreach (var (kind, prefix) in
                             new[] { (BoundBodyKind.PropertyGetter, "get_"), (BoundBodyKind.PropertySetter, "set_") }) {
                        if (FindBody(property, kind) is { } body) {
                            yield return (Unique(used, prefix + property.Name), body);
                        }
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    ///     Lowers the body of every method and property accessor of a type, handing each
    ///     finished function to <paramref name="add" />.
    /// </summary>
    void LowerMemberFunctions(NamedTypeSymbol type, Action<IrFunction> add) {
        // A struct's methods take the receiver explicitly; a shader's do not,
        // because its fields are globals.
        var selfType = type.TypeKind is TypeKind.Struct ? structs[type] : null;

        foreach (var (name, body) in MemberBodies(type, report: true)) {
            add(LowerFunction(name, body, type, SelfTypeFor(body, selfType)));
        }
    }

    /// <summary>
    ///     The receiver type a body's function takes, or none when it takes no receiver.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         None for an operator: every operand is already an explicit parameter, so a
    ///         <c>self</c> would make the signature disagree with the call the binder produced.
    ///     </para>
    ///     <para>
    ///         None for a <c>static</c> member either, for the same reason — and that one was a
    ///         defect rather than an omission. <c>struct M { static func Saturate(x: float) }</c> was
    ///         given a <c>self</c> parameter it has no receiver for, so a call to it from outside the
    ///         struct passed one argument to a function taking two. The IR verifier caught it as
    ///         malformed IR (<c>RVN3010</c>), which means the construct could not be compiled at all
    ///         — and it is the shape a library of helpers is made of, which is how it surfaced.
    ///     </para>
    /// </remarks>
    static IrStructType? SelfTypeFor(BoundBody body, IrStructType? selfType) =>
        body.Member is MethodSymbol { MethodKind: MethodKind.Operator } or { IsStatic: true } ? null : selfType;

    /// <summary>
    ///     Creates the signature for every method and property accessor of a type, so that
    ///     any body lowered later can call it.
    /// </summary>
    void DeclareMemberFunctions(NamedTypeSymbol type) {
        var selfType = type.TypeKind is TypeKind.Struct ? structs[type] : null;

        foreach (var (name, body) in MemberBodies(type, report: false)) {
            DeclareFunction(name, body, type, SelfTypeFor(body, selfType));
        }
    }

    void DeclareFunction(string name, BoundBody body, NamedTypeSymbol type, IrStructType? selfType) {
        // A struct's constructor builds a value and hands it back, rather than
        // mutating a receiver: the IR has no by-reference parameters.
        var constructsSelf = body.Kind == BoundBodyKind.Constructor && selfType is not null;

        var returnType = constructsSelf
            ? selfType!
            : LowerType(body.ReturnType, body.Member.DeclaringSyntax);

        var function = new IrFunction(name, returnType);
        IrVariable? shellSelfParameter = null;
        IrVariable? shellSelfLocal = null;

        // Order matters: `self` occupies the first parameter slot.
        if (selfType is not null) {
            if (constructsSelf) {
                shellSelfLocal = function.AddLocal("self", selfType);
            } else {
                shellSelfParameter = function.AddParameter("self", selfType);
            }
        }

        List<(Symbol, IrVariable)> parameters = [];
        foreach (var parameter in body.Parameters) {
            var parameterType = LowerType(parameter.Type, parameter.DeclaringSyntax);

            parameters.Add(
                (parameter, function.AddParameter(
                    parameter.Name,
                    parameterType,
                    parameter.RefKind == RefKind.InOut
                ))
            );
        }

        functions[(body.Member, body.Kind)] = function;
        shells[(body.Member, body.Kind)] = new(function, shellSelfParameter, shellSelfLocal, [.. parameters]);
    }

    IrFunction LowerFunction(string name, BoundBody body, NamedTypeSymbol type, IrStructType? selfType) {
        var constructsSelf = body.Kind == BoundBodyKind.Constructor && selfType is not null;
        var shell = shells[(body.Member, body.Kind)];
        var function = shell.Function;

        BeginFunction(function, type);

        // The shell already created the parameters; restore the mapping the body needs
        // rather than adding them a second time.
        selfParameter = shell.SelfParameter;
        selfLocal = shell.SelfLocal;
        foreach (var (symbol, variable) in shell.Parameters) {
            variables[symbol] = variable;
        }

        LowerStatement(body.Body);

        if (constructsSelf) {
            Emit(new IrReturnStatement(Load(SelfPlace!)));
        }

        EndFunction();

        return function;
    }

    /// <summary>
    ///     Points emission at <paramref name="function" /> and clears the per-function state.
    /// </summary>
    /// <remarks>
    ///     Does not create the receiver or the parameters — the function shell already did
    ///     (see <see cref="DeclareFunction" />). A caller with a receiver restores it from the
    ///     shell straight after.
    /// </remarks>
    void BeginFunction(IrFunction function, NamedTypeSymbol type) {
        currentFunction = function;
        currentType = type;
        currentBlock = function.Body;
        variables.Clear();
        selfParameter = null;
        selfLocal = null;
    }

    void EndFunction() {
        currentFunction = null;
        currentType = null;
        selfParameter = null;
        selfLocal = null;
        variables.Clear();
    }

    /// <summary>
    ///     The IR name for a member, which both backends emit verbatim as an identifier.
    /// </summary>
    /// <remarks>
    ///     An operator's symbol name is <c>operator+</c>, which is not an identifier in either
    ///     target — the GLSL mangler would turn every operator on a type into <c>operator_</c>,
    ///     <c>operator_1</c>, and so on, and a disassembly would say nothing about which is which.
    ///     Spelling the operator gives <c>Spectrum_Add</c>, which reads in a frame debugger.
    /// </remarks>
    static string FunctionName(NamedTypeSymbol type, MethodSymbol method) {
        if (method.IsConstructor) {
            return $"{type.Name}.init";
        }

        if (method.MethodKind != MethodKind.Operator) {
            return method.Name;
        }

        var symbol = method.Name.StartsWith("operator", StringComparison.Ordinal)
            ? method.Name["operator".Length..]
            : method.Name;

        return $"{type.Name}_{OperatorWord(symbol)}";
    }

    /// <summary>A pronounceable name for an operator symbol.</summary>
    static string OperatorWord(string symbol) =>
        symbol switch {
            "+" => "Add",
            "-" => "Subtract",
            "*" => "Multiply",
            "/" => "Divide",
            "%" => "Modulo",
            "==" => "Equals",
            "!=" => "NotEquals",
            "<" => "LessThan",
            "<=" => "LessThanOrEqual",
            ">" => "GreaterThan",
            ">=" => "GreaterThanOrEqual",
            "&" => "BitwiseAnd",
            "|" => "BitwiseOr",
            "^" => "BitwiseXor",
            "~" => "BitwiseNot",
            "!" => "LogicalNot",
            "<<" => "ShiftLeft",
            ">>" => "ShiftRight",
            ">>>" => "UnsignedShiftRight",
            "++" => "Increment",
            "--" => "Decrement",
            "true" => "True",
            "false" => "False",
            _ => "Operator"
        };

    static string Unique(HashSet<string> used, string name) {
        var candidate = name;
        var suffix = 1;
        while (!used.Add(candidate)) {
            candidate = $"{name}#{suffix++}";
        }

        return candidate;
    }

    static string Describe(MethodKind kind) =>
        kind switch {
            MethodKind.Operator => "user-defined operator",
            MethodKind.Conversion => "conversion operator",
            MethodKind.LocalFunction => "local function",
            _ => "member"
        };

    void Emit(IrStatement statement) => currentBlock.Add(statement);

    /// <summary>
    ///     Whether the block being emitted into already ends in a terminator, making
    ///     anything further unreachable.
    /// </summary>
    /// <remarks>
    ///     Only reachable once a constant condition has been folded: <c>if (Flag) return x</c>
    ///     against a true key lowers to a bare <c>return x</c>, and whatever followed the
    ///     <c>if</c> in source is then dead. Before folding, the code after an <c>if</c> was
    ///     always reachable through the other branch.
    /// </remarks>
    bool CurrentBlockIsTerminated =>
        currentBlock.Statements is [.., IrReturnStatement or IrBreakStatement or IrContinueStatement];

    /// <summary>Emits an instruction and hands back the value it defines.</summary>
    IrValue Emit(Func<IrValue, IrInstruction> build, IrType resultType) {
        var result = Function.NewValue(resultType);
        currentBlock.Add(build(result));
        return result;
    }

    /// <summary>Collects everything <paramref name="body" /> emits into a fresh block.</summary>
    IrBlock EmitInto(Action body) {
        var previous = currentBlock;
        var block = new IrBlock();
        currentBlock = block;

        try {
            body();
        } finally {
            currentBlock = previous;
        }

        return block;
    }

    static Location LocationOf(SyntaxNode? syntax) => syntax?.GetLocation() ?? Location.None;

    void ReportUnsupported(BoundNode node, string what) =>
        diagnostics.Add(LoweringDiagnostics.ConstructNotSupported, node.Syntax.GetLocation(), what);
}
