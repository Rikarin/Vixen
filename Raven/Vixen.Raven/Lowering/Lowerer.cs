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

    /// <summary>The instantiations to emit, once <see cref="PlanInstantiations" /> has run.</summary>
    Monomorphiser? monomorphiser;

    /// <summary>The flattened name each instantiation was given, and the names already taken.</summary>
    readonly Dictionary<ConstructedNamedTypeSymbol, string> instantiationNames = [];

    readonly HashSet<string> instantiationNamesUsed = new(StringComparer.Ordinal);

    /// <summary>
    ///     The substitution in force while an instantiation's bodies are lowered, or null outside
    ///     one.
    /// </summary>
    /// <remarks>
    ///     One field rather than a parameter threaded through every <c>Lower*</c> method, because
    ///     that is what it is: a property of which copy of a body is being emitted, not of any one
    ///     expression. <see cref="LowerType" /> applies it before anything else looks at a type, so
    ///     no other part of lowering has to know instantiation is happening — a <c>T</c> simply
    ///     never reaches them.
    /// </remarks>
    TypeMap? substitution;

    /// <summary>
    ///     The type a body is being emitted <em>for</em>, when that is not the type that declared
    ///     it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two features need the same fact, which is why it is one field. An instantiation's
    ///         body names the <em>definition</em>'s fields — <c>Box&lt;T&gt;.value</c> — because it
    ///         was bound once against the open type. An inherited body names the <em>base</em>'s
    ///         fields, for the same reason: it was bound once against the base.
    ///     </para>
    ///     <para>
    ///         In both cases the struct a field belongs to cannot be found from the field alone, and
    ///         this is what says which copy is being emitted.
    ///     </para>
    /// </remarks>
    NamedTypeSymbol? currentSelfType;
    IrVariable? selfLocal;
    IrVariable? selfParameter;
    readonly Dictionary<Symbol, IrVariable> variables = [];
    readonly Dictionary<(Symbol Member, BoundBodyKind Kind), IrFunction> functions = [];
    readonly Dictionary<(Symbol Member, BoundBodyKind Kind), FunctionShell> shells = [];

    /// <summary>Where each function lowered from source first <c>discard</c>s.</summary>
    /// <remarks>
    ///     Kept beside the IR rather than in it, because an <see cref="IrStatement" /> carries no
    ///     span. A function linked from a <c>.rvnlib</c> has none to record, which is why the check
    ///     itself asks <see cref="IrFunction.Discards" /> and only consults this for the location:
    ///     the rule holds for linked code too, and there the diagnostic simply has no span to offer.
    /// </remarks>
    readonly Dictionary<IrFunction, SyntaxNode> discards = [];

    /// <summary>
    ///     Where each function's first barrier was written, for the same reason and with the same
    ///     shape as <see cref="discards" />: the rule is checked against the IR, and this is only
    ///     consulted for somewhere to point.
    /// </summary>
    readonly Dictionary<IrFunction, SyntaxNode> barriers = [];

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

        // An open generic is never emitted — only its instantiations are, which is what
        // monomorphisation means and the only thing either target can take.
        var declared = compilation.GetAllTypes();
        var types = declared.Where(t => t.TypeParameters.Count == 0).ToArray();
        var instantiations = PlanInstantiations(declared);
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

        // An instantiation's shell alongside the ordinary ones, for exactly the same reason: a
        // body may name `Box<float4>` before the pass that fills its fields runs.
        foreach (var instantiation in instantiations) {
            if (instantiation.Type is { } constructed) {
                var structType = new IrStructType(MangledName(constructed));
                structs[constructed] = structType;
                module.Add(structType);
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

        foreach (var instantiation in instantiations) {
            if (instantiation.Type is { } constructed) {
                DeclareStructFields(constructed);
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

        // After every type's own members, because an inherited copy's name is uniquified against
        // them and a call inside one may reach any of them.
        foreach (var type in types) {
            if (type.TypeKind is TypeKind.Shader or TypeKind.Struct) {
                DeclareInheritedFunctions(type);
            }
        }

        foreach (var instantiation in instantiations) {
            DeclareInstantiation(instantiation);
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

        foreach (var instantiation in instantiations) {
            LowerInstantiation(instantiation);
        }

        // After every shader exists, because a slot's implementation may be declared later in the
        // file than the shader that composes it, and its globals only exist once it is lowered.
        // Inheritance first, so a composed shader contributes what it inherits too.
        MergeInheritedInterfaces(types);
        MergeComposedInterfaces(types);

        // After every body exists, and after pruning: a stream's direction comes from what the
        // stage's reachable code does with it, which is only knowable once the module is settled.
        ImportPruner.Prune(module, importedStructs, importedFunctions);
        ResolveStreamDirections();
        ResolveSharedVariables();
        ReportDiscardsOutsideFragmentStages();
        ReportBarriersOutsideComputeStages();

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
        HashSet<NamedTypeSymbol> merged = [];

        foreach (var type in types) {
            MergeComposed(type, merged);
        }
    }

    /// <summary>
    ///     Merges one shader's composed contributors into it, and theirs into them first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <strong>Depth first, and that is the whole of it.</strong> A contribution is
    ///         qualified one level — <see cref="MergeInterface" /> prefixes the source shader's name —
    ///         so the full path a host binds through, <c>CompositeSurface.MetalRoughnessSurface.baseColor</c>,
    ///         exists only if the chain was given the surface's parameter <em>before</em> the pass was
    ///         given the chain's. Merging the transitive closure straight into the pass instead names
    ///         the same parameter <c>MetalRoughnessSurface.baseColor</c>, and which of the two came
    ///         out depended on the order the module happened to declare its types in.
    ///     </para>
    ///     <para>
    ///         That is not a cosmetic difference. The engine predicts these names without a compiler
    ///         — <c>MaterialCompilationContext</c> builds the path from the composition — and a name
    ///         it predicts that no member matches is dropped in silence, so every value a composed
    ///         material sets reaches the GPU as zero. A whole-library compilation and a
    ///         single-pass one disagreed about the name, which is why the checked-in reflection
    ///         looked right while a frame rendered black.
    ///     </para>
    ///     <para>
    ///         Marked before recursing, so a compose cycle terminates rather than overflowing. Only
    ///         the shader's <em>own</em> slots are walked here: whatever they reach transitively is
    ///         already in the contributor by the time it is merged.
    ///     </para>
    /// </remarks>
    void MergeComposed(NamedTypeSymbol type, HashSet<NamedTypeSymbol> merged) {
        if (type.TypeKind != TypeKind.Shader || !merged.Add(type)) {
            return;
        }

        foreach (var member in type.GetMembers()) {
            if (member is not FieldSymbol { IsCompose: true, ComposedType: { } bound }
                || bound.TypeKind != TypeKind.Shader) {
                continue;
            }

            MergeComposed(bound, merged);

            if (shaders.TryGetValue(type, out var shader) && shaders.TryGetValue(bound, out var source)) {
                MergeInterface(shader, source);
            }
        }
    }

    /// <summary>
    ///     Copies one shader's bindings and streams onto another, skipping what it already has.
    /// </summary>
    static void MergeInterface(IrShader target, IrShader source, bool qualify = true) {
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
                    // A composed contribution is qualified by the shader that declares it — see
                    // IrBinding.Name — using `binding.Name` rather than the variable's, so a
                    // transitive contribution keeps the whole path. An *inherited* one is not: the
                    // author wrote `tint` on a base and reads `tint` in the derived shader, and a
                    // host binding by name should see what the source says. The two differ because
                    // a composed feature's parameter belongs to the feature, and an inherited field
                    // belongs to the type that inherited it.
                    // A shared binding keeps its declared name however deep it was reached through,
                    // because the name is how the several features that declare it are recognised as
                    // meaning one resource. Qualifying it would make each feature's mention its own
                    // binding again, which is the thing the marker exists to stop.
                    qualify && !binding.IsShared ? $"{source.Name}.{binding.Name}" : binding.Name,
                    // Carried, not defaulted. A merged `RWBuffer` that arrived here read-only was
                    // decorated `readonly` and then stored into: SPIR-V's validator accepts the
                    // contradiction and GLSL's front end does not, so an inherited or composed
                    // storage buffer compiled on one target and failed on the other.
                    binding.IsWritable,
                    binding.DefaultValue,
                    binding.IsShared,
                    binding.IsMaterialIndex
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

        // And the permutations, which are as much a part of a shader's interface as its bindings: they
        // are what a host enumerates variants with, and the C# generator turns each into a
        // `PermutationKey`. A derived shader whose base declared them would otherwise report none of
        // them — the body is already folded against their values by this point, so the fact survives
        // nowhere else, and a host asking for `UseShadows` on a pass that inherits it would silently get
        // the default variant.
        //
        // Only for inheritance. A *composed* shader's permutations stay its own: a material's feature is
        // selected by the composition and configured by its own key, and hoisting those into the
        // consumer would make one shader's variant space the product of every feature's.
        if (!qualify) {
            var permutations = target.Permutations.Select(permutation => permutation.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var permutation in source.Permutations) {
                if (permutations.Add(permutation.Name)) {
                    target.Add(permutation);
                }
            }

            var values = target.ValueParameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var parameter in source.ValueParameters) {
                if (values.Add(parameter.Name)) {
                    target.Add(parameter);
                }
            }
        }
    }

    // --- Monomorphisation --------------------------------------------------

    /// <summary>
    ///     Works out which instantiations of the compilation's generics are actually used.
    /// </summary>
    /// <remarks>
    ///     Seeded from the non-generic declarations, because those are what the pipeline reaches:
    ///     an entry point is never generic, so anything a shader uses is reachable from a concrete
    ///     declaration. An unused <c>Box&lt;T&gt;</c> costs nothing, which is the property that
    ///     makes a generic library affordable.
    /// </remarks>
    IReadOnlyList<Instantiation> PlanInstantiations(IReadOnlyList<NamedTypeSymbol> declared) {
        monomorphiser = new(member => bodies.GetValueOrDefault(member) ?? (IEnumerable<BoundBody>)[]);

        foreach (var type in declared.Where(t => t.TypeParameters.Count == 0)) {
            monomorphiser.Seed(type);
        }

        monomorphiser.Close();

        if (monomorphiser.Overflowed) {
            diagnostics.Add(
                LoweringDiagnostics.ConstructNotSupported,
                Location.None,
                "A generic instantiation nested deeper than the compiler expands"
            );
        }

        return monomorphiser.Instantiations;
    }

    /// <summary>
    ///     The IR name of an instantiation: the definition plus its arguments, flattened.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both backends emit an IR name verbatim as an identifier and neither language has
    ///         angle brackets, so <c>Box&lt;float4&gt;</c> becomes <c>Box_float4</c> — which reads
    ///         in a frame debugger and survives a disassembly. A nested argument recurses through
    ///         the same rule, so <c>Box&lt;Pair&lt;float&gt;&gt;</c> is <c>Box_Pair_float</c> rather
    ///         than a qualified name with the punctuation beaten out of it.
    ///     </para>
    ///     <para>
    ///         Uniquified against the names already taken, because flattening cannot be injective:
    ///         a two-argument <c>Box&lt;Pair, float&gt;</c> would land on the same string. A module's
    ///         struct names are one flat namespace, so a collision has to be resolved here rather
    ///         than left for a backend to rename one of them and not the other.
    ///     </para>
    /// </remarks>
    string MangledName(ConstructedNamedTypeSymbol type) {
        if (instantiationNames.TryGetValue(type, out var existing)) {
            return existing;
        }

        return instantiationNames[type] = Unique(instantiationNamesUsed, Mangle(Flatten(type)));
    }

    string MangledName(SubstitutedMethodSymbol method) =>
        Unique(instantiationNamesUsed, Mangle($"{method.Name}_{Arguments(method.TypeArguments)}"));

    string Flatten(ConstructedNamedTypeSymbol type) =>
        $"{type.OriginalDefinition.Name}_{Arguments(type.TypeArguments)}";

    string Arguments(IReadOnlyList<TypeSymbol> typeArguments) =>
        string.Join(
            "_",
            typeArguments.Select(
                a => a is ConstructedNamedTypeSymbol nested ? Flatten(nested) : a.ToDisplayString()
            )
        );

    static string Mangle(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));

    /// <summary>Creates the signatures an instantiation contributes, before any body is lowered.</summary>
    void DeclareInstantiation(Instantiation instantiation) {
        substitution = instantiation.Map;
        currentSelfType = instantiation.Type;

        try {
            if (instantiation.Type is { } constructed) {
                DeclareMemberFunctions(constructed);
                return;
            }

            var method = instantiation.Method!;

            if (FindBody(method, BoundBodyKind.Method) is { } body) {
                DeclareFunction(MangledName(method), body, method, ContainerOf(method), null);
            }
        } finally {
            substitution = null;
            currentSelfType = null;
        }
    }

    /// <summary>Lowers an instantiation's bodies through its substitution.</summary>
    void LowerInstantiation(Instantiation instantiation) {
        substitution = instantiation.Map;
        currentSelfType = instantiation.Type;

        try {
            if (instantiation.Type is { } constructed) {
                LowerMemberFunctions(constructed, module.Add);
                return;
            }

            var method = instantiation.Method!;

            if (FindBody(method, BoundBodyKind.Method) is { } body) {
                module.Add(LowerFunction(MangledName(method), body, method, ContainerOf(method), null));
            }
        } finally {
            substitution = null;
            currentSelfType = null;
        }
    }

    /// <summary>
    ///     The one symbol that stands for a member of an instantiation, so the declaration and
    ///     the call site key the function table alike.
    /// </summary>
    Symbol Canonical(Symbol member) => monomorphiser?.Canonical(member) ?? member;

    /// <summary>
    ///     Whether a body is being emitted for a type other than the one that declared it, which is
    ///     what an inherited copy is.
    /// </summary>
    /// <remarks>
    ///     An instantiation is not one of these: its members are read <em>through</em> the
    ///     constructed type, so the member's container already is the type being emitted for.
    /// </remarks>
    static bool IsInheritedCopy(Symbol member, NamedTypeSymbol? owner) =>
        owner is not null
        && member.ContainingSymbol is NamedTypeSymbol declaring
        && !declaring.Equals(owner);

    /// <summary>The declaring type of a generic method, for the <c>currentType</c> a body is lowered in.</summary>
    static NamedTypeSymbol? ContainerOf(MethodSymbol method) =>
        method.ContainingSymbol as NamedTypeSymbol ?? (method as SubstitutedMethodSymbol)?.OriginalDefinition
            .ContainingSymbol as NamedTypeSymbol;

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

    /// <summary>
    ///     The bound body of a member, reached through its definition when the member is a view of
    ///     one.
    /// </summary>
    /// <remarks>
    ///     An instantiation has no body of its own: <c>Box&lt;float4&gt;.Get</c> and
    ///     <c>Box&lt;int&gt;.Get</c> are the same bound tree read through different maps, and
    ///     binding each instantiation separately would type-check the same code twice for no gain.
    /// </remarks>
    BoundBody? FindBody(Symbol member, BoundBodyKind kind) =>
        bodies.GetValueOrDefault(
                member switch {
                    SubstitutedMethodSymbol method => method.OriginalDefinition,
                    SubstitutedPropertySymbol property => property.OriginalDefinition,
                    SubstitutedFieldSymbol field => field.OriginalDefinition,
                    _ => member
                }
            )
            ?.FirstOrDefault(b => b.Kind == kind);

    // --- Shaders -----------------------------------------------------------

    void LowerShader(NamedTypeSymbol type) {
        var shader = new IrShader(type.Name);
        module.Add(shader);
        shaders[type] = shader;

        var slots = new Dictionary<IrBindingKind, int>();

        DeclareCompileTimeConstants(type, shader);
        DeclareStreams(type, shader);
        DeclareSharedVariables(type, shader);

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

            var kind = field switch {
                // Checked before the resource kind, because `[PushConstant]` on anything but a
                // plain value is RVN2120 — an opaque resource has no bytes to push.
                { IsPushConstant: true } => IrBindingKind.PushConstant,
                { ResourceKind: ResourceKind.Texture } => IrBindingKind.Texture,
                { ResourceKind: ResourceKind.Sampler } => IrBindingKind.Sampler,
                { ResourceKind: ResourceKind.StorageBuffer } => IrBindingKind.StorageBuffer,
                { ResourceKind: ResourceKind.StorageImage } => IrBindingKind.StorageImage,
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
                    writable: field.Type.IsWritableResource,
                    // The author's initialiser, kept for the host rather than for the GPU: a uniform
                    // block arrives already filled, so `= 1f` is a statement about what to put there
                    // when nobody said otherwise. See IrBinding.DefaultValue.
                    defaultValue: field.DeclaredValue,
                    shared: field.IsShared,
                    materialIndex: field.IsMaterialIndex
                )
            );
        }

        LowerMemberFunctions(type, shader.Add);
        LowerInheritedFunctions(type, shader.Add);
        LowerBindingInitializers(type, shader);
        ReportOversizedPushConstants(type, shader);

        foreach (var member in type.GetMembers()) {
            if (member is MethodSymbol { Stage: not ShaderStage.None } method
                && functions.TryGetValue((method, BoundBodyKind.Method), out var function)) {
                shader.Add(BuildEntryPoint(method, function));
            }
        }
    }

    /// <summary>
    ///     Warns when a shader's push-constant block is bigger than every Vulkan implementation
    ///     has to offer.
    /// </summary>
    /// <remarks>
    ///     Here rather than in the binder because the size is a property of the <em>laid-out</em>
    ///     block: it is std430 packing over the IR types, which is the number the host has to fit
    ///     and the number both backends decorate their members with.
    /// </remarks>
    void ReportOversizedPushConstants(NamedTypeSymbol type, IrShader shader) {
        const int Guaranteed = 128;

        if (Reflection.BindingPlan.PushConstants(shader) is not { IsEmpty: false } constants) {
            return;
        }

        var size = Reflection.ShaderLayout
            .Members([.. constants.Select(c => c.Type)], Reflection.LayoutRule.Std430)
            .Size;

        if (size > Guaranteed) {
            diagnostics.Add(
                LoweringDiagnostics.PushConstantsOverGuaranteedSize,
                LocationOf(type.DeclaringSyntax),
                shader.Name,
                size
            );
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
    ///     Declares the shader's <c>groupshared</c> fields as module-scope globals.
    /// </summary>
    /// <remarks>
    ///     A global for the reason <see cref="DeclareStreams" /> gives — both targets model
    ///     workgroup storage as a module-scope variable, so a read is an ordinary load and a write
    ///     an ordinary store, and only the storage class differs. Which entry points may actually
    ///     reach it is worked out afterwards, from the call graph: see
    ///     <see cref="ResolveSharedVariables" />.
    /// </remarks>
    void DeclareSharedVariables(NamedTypeSymbol type, IrShader shader) {
        foreach (var member in type.GetMembers()) {
            if (member is not FieldSymbol { IsGroupShared: true, IsConst: false, IsCompose: false } field) {
                continue;
            }

            var irType = LowerType(field.Type, field.DeclaringSyntax);
            if (irType.IsVoid) {
                continue;
            }

            var variable = new IrVariable(field.Name, irType, IrVariableKind.Global);
            globals[field] = variable;
            shader.Add(new IrSharedVariable(variable));
        }
    }

    /// <summary>
    ///     Decides, for every entry point, which of its shader's <c>groupshared</c> variables it
    ///     can reach — and refuses the ones a stage with no workgroups reached.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Runs once the whole module is lowered, because it reads bodies, and answers by
    ///         reachability rather than by shader membership for the reason
    ///         <see cref="ResolveStreamDirections" /> gives. Two things come out of the same walk:
    ///         what each compute stage's unit has to declare, and whether a stage that has no
    ///         workgroups touched any of it.
    ///     </para>
    ///     <para>
    ///         The refusal is not pedantry. <c>Workgroup</c> storage exists in neither target
    ///         outside a compute stage — there is no group for it to belong to — so a fragment
    ///         shader reaching a shared variable would either read a variable the unit never
    ///         declared or, worse, be given one per invocation and quietly work in a test and race
    ///         on a device.
    ///     </para>
    /// </remarks>
    void ResolveSharedVariables() {
        foreach (var shader in module.Shaders) {
            if (shader.SharedVariables.Count == 0) {
                continue;
            }

            var declared = shader.SharedVariables.Select(s => s.Variable).ToHashSet();

            foreach (var entryPoint in shader.EntryPoints) {
                HashSet<IrVariable> touched = [];

                foreach (var function in CallGraph.Reachable(entryPoint.Function)) {
                    CollectSharedUses(function.Body, declared, touched);
                }

                // Declaration order, so a unit's declarations come out in the order the author
                // wrote them rather than in the order the body happened to reach them.
                entryPoint.SetSharedVariables([.. shader.SharedVariables.Where(s => touched.Contains(s.Variable))]);

                if (entryPoint.Stage == ShaderStage.Compute) {
                    continue;
                }

                foreach (var shared in entryPoint.SharedVariables) {
                    diagnostics.Add(
                        LoweringDiagnostics.WorkgroupStorageOutsideCompute,
                        LocationOf(SyntaxOf(shared.Variable)),
                        $"the group-shared variable '{shared.Name}'",
                        entryPoint.Stage.ToString().ToLowerInvariant(),
                        entryPoint.Function.Name
                    );
                }
            }
        }
    }

    /// <summary>
    ///     Refuses a barrier some stage other than a compute one can reach.
    /// </summary>
    /// <remarks>
    ///     The same shape and the same argument as
    ///     <see cref="ReportDiscardsOutsideFragmentStages" />, one stage over: reachability decides
    ///     which stages a helper belongs to, and it is reported once per offending function rather
    ///     than once per entry point that reaches it. A barrier outside a compute stage is not
    ///     merely useless — it is an <c>OpControlBarrier</c> with a <c>Workgroup</c> execution scope
    ///     in an execution model that has no workgroups, which <c>spirv-val</c> rejects.
    /// </remarks>
    void ReportBarriersOutsideComputeStages() {
        HashSet<IrFunction> said = [];

        foreach (var entryPoint in module.Shaders.SelectMany(shader => shader.EntryPoints)) {
            if (entryPoint.Stage == ShaderStage.Compute) {
                continue;
            }

            foreach (var function in CallGraph.Reachable(entryPoint.Function)) {
                if (!ContainsBarrier(function.Body) || !said.Add(function)) {
                    continue;
                }

                diagnostics.Add(
                    LoweringDiagnostics.WorkgroupStorageOutsideCompute,
                    LocationOf(barriers.GetValueOrDefault(function)),
                    "a barrier",
                    entryPoint.Stage.ToString().ToLowerInvariant(),
                    entryPoint.Function.Name
                );
            }
        }
    }

    /// <summary>
    ///     Records which of <paramref name="shared" /> a body touches.
    /// </summary>
    /// <remarks>
    ///     Calls are <em>not</em> expanded here, unlike <see cref="CollectStreamUses" />: the caller
    ///     already walks every reachable function, so following calls as well would only revisit
    ///     bodies. What the stream walk needs and this does not is <em>order</em> — a stream's
    ///     direction depends on which use comes first, and a shared variable's declaration does
    ///     not.
    /// </remarks>
    static void CollectSharedUses(IrStatement statement, HashSet<IrVariable> shared, HashSet<IrVariable> touched) {
        switch (statement) {
            case IrBlock block:
                foreach (var nested in block.Statements) {
                    CollectSharedUses(nested, shared, touched);
                }

                break;

            case IrIfStatement conditional:
                CollectSharedUses(conditional.Then, shared, touched);

                if (conditional.Else is { } otherwise) {
                    CollectSharedUses(otherwise, shared, touched);
                }

                break;

            case IrLoopStatement loop:
                CollectSharedUses(loop.Condition, shared, touched);
                CollectSharedUses(loop.Body, shared, touched);

                if (loop.Continue is { } step) {
                    CollectSharedUses(step, shared, touched);
                }

                break;

            case IrLoadInstruction load when shared.Contains(load.Place.Root):
                touched.Add(load.Place.Root);
                break;

            case IrStoreInstruction store when shared.Contains(store.Place.Root):
                touched.Add(store.Place.Root);
                break;

            // An atomic is the third way to reach one, and the reason the storage exists at all: a
            // read-modify-write that never becomes a load or a store.
            case IrAtomicInstruction atomic when shared.Contains(atomic.Place.Root):
                touched.Add(atomic.Place.Root);
                break;

            case IrArrayLengthInstruction length when shared.Contains(length.Place.Root):
                touched.Add(length.Place.Root);
                break;

            default:
                break;
        }
    }

    /// <summary>Whether a body reaches a barrier anywhere, calls aside.</summary>
    static bool ContainsBarrier(IrStatement statement) =>
        statement switch {
            IrIntrinsicInstruction { Intrinsic: IrIntrinsic.ControlBarrier or IrIntrinsic.MemoryBarrierShared } => true,
            IrBlock block => block.Statements.Any(ContainsBarrier),
            IrIfStatement conditional => ContainsBarrier(conditional.Then)
                || (conditional.Else is { } otherwise && ContainsBarrier(otherwise)),
            IrLoopStatement loop => ContainsBarrier(loop.Condition)
                || ContainsBarrier(loop.Body)
                || (loop.Continue is { } step && ContainsBarrier(step)),
            _ => false
        };

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
        if (entryPoint.Stage != ShaderStage.Fragment) {
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

    /// <summary>
    ///     Refuses a <c>discard</c> that some stage other than a fragment one can reach.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Reachability rather than where the keyword sits, for the reason
    ///         <see cref="ResolveStreamDirections" /> gives about streams: a helper belongs to
    ///         whichever stages call it, and the file it is written in cannot say which those are. A
    ///         cutout test shared by the depth prepass and a compute pass is wrong only in the
    ///         second.
    ///     </para>
    ///     <para>
    ///         Once per discarding function rather than once per entry point that reaches it: one
    ///         function is one mistake, and the author fixes it in one place.
    ///     </para>
    ///     <para>
    ///         Asked of every function the graph reaches, not only the ones lowered here, so a
    ///         helper linked from a <c>.rvnlib</c> is covered — that one has no syntax to point at,
    ///         and a diagnostic with no span still beats <c>spirv-val</c> rejecting the module for
    ///         an <c>OpKill</c> outside the Fragment execution model.
    ///     </para>
    /// </remarks>
    void ReportDiscardsOutsideFragmentStages() {
        HashSet<IrFunction> said = [];

        foreach (var entryPoint in module.Shaders.SelectMany(shader => shader.EntryPoints)) {
            if (entryPoint.Stage == ShaderStage.Fragment) {
                continue;
            }

            foreach (var function in CallGraph.Reachable(entryPoint.Function)) {
                if (!function.Discards || !said.Add(function)) {
                    continue;
                }

                diagnostics.Add(
                    LoweringDiagnostics.DiscardOutsideFragmentStage,
                    LocationOf(discards.GetValueOrDefault(function)),
                    entryPoint.Stage.ToString().ToLowerInvariant(),
                    entryPoint.Function.Name
                );
            }
        }
    }

    /// <summary>The declaration a lowered stream came from, so the warning has a span.</summary>
    SyntaxNode? SyntaxOf(IrShader shader, IrStream stream) => SyntaxOf(stream.Variable);

    /// <summary>The declaration a lowered global came from, so a diagnostic about it has a span.</summary>
    SyntaxNode? SyntaxOf(IrVariable variable) =>
        globals.FirstOrDefault(entry => ReferenceEquals(entry.Value, variable)).Key?.DeclaringSyntax;

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

        // Only on the stage that has workgroups. A size the binder warned about (RVN2106) is
        // dropped here rather than carried to a backend that has nowhere to put it.
        var workgroupSize = method.Stage == ShaderStage.Compute ? method.WorkgroupSize : null;

        return new(method.Stage, function, inputs, BuildOutputs(method, function), workgroupSize);
    }

    /// <summary>
    ///     The stage's outputs: one, none, or one per member of a fragment stage's returned struct.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Multiple render targets.</strong> An interface variable takes one location and
    ///         so has to be one scalar or vector; a stage that writes four targets therefore returns a
    ///         struct, and this is where it comes apart. The <em>declaration order</em> of the struct
    ///         is the render-target order — the same rule <c>StreamPlan</c> uses for streams, and the
    ///         same reason: a number both sides can derive beats a number one side spells.
    ///     </para>
    ///     <para>
    ///         Fragment stages only. A vertex stage's several outputs are <c>stream</c>s, which is a
    ///         different mechanism and a better one — a stream's location is a property of the shader,
    ///         so the writing and reading stages agree without either declaring the other's struct.
    ///         Everywhere else an aggregate output stays <c>RVN4001</c>, which is what
    ///         <c>StageInterface</c> is for.
    ///     </para>
    /// </remarks>
    static IrStageIo[] BuildOutputs(MethodSymbol method, IrFunction function) {
        if (function.ReturnType.IsVoid) {
            return [];
        }

        if (method.Stage != ShaderStage.Fragment || function.ReturnType is not IrStructType targets) {
            return [new IrStageIo("result", function.ReturnType, method.SemanticName)];
        }

        return [
            .. targets.Fields.Select(
                (field, index) => new IrStageIo(field.Name, field.Type, $"SV_Target{index}", index)
            )
        ];
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

        // A base's fields first, so a derived value's prefix is the base's layout — see
        // Lowerer.Inheritance. Nothing else in lowering has to know: a field access is by index,
        // and the index comes from this list.
        foreach (var field in LayoutFields(type)) {
            var irType = LowerType(field.Type, field.DeclaringSyntax);
            if (!irType.IsVoid) {
                fields.Add(new(field.Name, irType));
            }
        }

        structs[type].SetFields(fields.ToArray());
    }

    void LowerStruct(NamedTypeSymbol type) {
        LowerMemberFunctions(type, module.Add);
        LowerInheritedFunctions(type, module.Add);
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
    IEnumerable<(string Name, Symbol Member, BoundBody Body)> MemberBodies(NamedTypeSymbol type, bool report) {
        HashSet<string> used = [];

        foreach (var member in type.GetMembers()) {
            switch (member) {
                // A method with type parameters of its own is emitted once per instantiation, from
                // the monomorphiser's plan — never here, where it would be emitted open and every
                // `T` in it would be RVN3001.
                case MethodSymbol { TypeParameters.Count: > 0 }:
                    break;

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

                    yield return (Unique(used, FunctionName(type, method)), method, body);
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
                            yield return (Unique(used, prefix + property.Name), property, body);
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

        // Set even for a type's own members, because they reach the base's fields too: `self` has
        // this type's layout whichever type declared the field being read.
        var previous = currentSelfType;
        currentSelfType ??= type;

        try {
            foreach (var (name, member, body) in MemberBodies(type, report: true)) {
                add(LowerFunction(name, body, member, type, SelfTypeFor(body, selfType)));
            }
        } finally {
            currentSelfType = previous;
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
        var previous = currentSelfType;
        currentSelfType ??= type;

        try {
            foreach (var (name, member, body) in MemberBodies(type, report: false)) {
                DeclareFunction(name, body, member, type, SelfTypeFor(body, selfType));
            }
        } finally {
            currentSelfType = previous;
        }
    }

    void DeclareFunction(string name, BoundBody body, Symbol member, NamedTypeSymbol? type, IrStructType? selfType) {
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

        var shell = new FunctionShell(function, shellSelfParameter, shellSelfLocal, [.. parameters]);

        // A copy emitted for a type that did not declare the member is keyed by the pair, because
        // the member symbol alone already names the base's own function.
        if (IsInheritedCopy(member, type)) {
            inherited[(type!, member, body.Kind)] = function;
            inheritedShells[(type!, member, body.Kind)] = shell;
            return;
        }

        var key = Canonical(member);
        functions[(key, body.Kind)] = function;
        shells[(key, body.Kind)] = shell;
    }

    IrFunction LowerFunction(string name, BoundBody body, Symbol member, NamedTypeSymbol? type, IrStructType? selfType) {
        var constructsSelf = body.Kind == BoundBodyKind.Constructor && selfType is not null;
        var shell = IsInheritedCopy(member, type)
            ? inheritedShells[(type!, member, body.Kind)]
            : shells[(Canonical(member), body.Kind)];

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
    void BeginFunction(IrFunction function, NamedTypeSymbol? type) {
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
    string FunctionName(NamedTypeSymbol type, MethodSymbol method) {
        // An instantiation's members are qualified by the mangled type: a module's function names
        // are one flat namespace, and `Box<float4>.Get` and `Box<int>.Get` are two functions.
        var instantiation = type as ConstructedNamedTypeSymbol;
        var typeName = instantiation is null ? type.Name : MangledName(instantiation);

        if (method.IsConstructor) {
            return $"{typeName}.init";
        }

        if (method.MethodKind != MethodKind.Operator) {
            // Qualified when the type inherits: a derived type's override and the base member it
            // replaces are two functions, and a dump or a frame debugger showing `Scaled` twice
            // says nothing about which one execution is in.
            return instantiation is null && !BaseChain(type).Any()
                ? method.Name
                : $"{typeName}_{method.Name}";
        }

        var symbol = method.Name.StartsWith("operator", StringComparison.Ordinal)
            ? method.Name["operator".Length..]
            : method.Name;

        return $"{typeName}_{OperatorWord(symbol)}";
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
        currentBlock.Statements is
            [.., IrReturnStatement or IrBreakStatement or IrContinueStatement or IrDiscardStatement];

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
