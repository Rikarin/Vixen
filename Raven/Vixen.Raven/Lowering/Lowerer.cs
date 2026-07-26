// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Vixen.Raven.Symbols.Source;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;

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
        new Lowerer(compilation, diagnostics).LowerModule();

    IrModule LowerModule() {
        CollectBodies();

        var types = compilation.GetAllTypes();

        // Shells first: a function body can call anything in the module, and a
        // struct can hold a field of a struct declared later.
        foreach (var type in types) {
            switch (type.TypeKind) {
                case TypeKind.Struct:
                    structs[type] = new(type.Name);
                    break;
            }
        }

        foreach (var structType in structs.Values) {
            module.Add(structType);
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

        return module;
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

        var slots = new Dictionary<IrBindingKind, int>();

        ReportInheritanceNotFlattened(type);
        DeclareCompileTimeConstants(type, shader);

        foreach (var member in type.GetMembers()) {
            // A `const` field is folded at every use, so it needs no binding.
            if (member is not FieldSymbol { IsConst: false, IsCompose: false } field) {
                continue;
            }

            var irType = LowerType(field.Type, field.DeclaringSyntax);
            if (irType.IsVoid) {
                continue;
            }

            var kind = field.ResourceKind switch {
                ResourceKind.Texture => IrBindingKind.Texture,
                ResourceKind.Sampler => IrBindingKind.Sampler,
                _ => IrBindingKind.Uniform
            };

            slots.TryGetValue(kind, out var slot);
            slots[kind] = slot + 1;

            var variable = new IrVariable(field.Name, irType, IrVariableKind.Global);
            globals[field] = variable;
            shader.Add(new IrBinding(variable, kind, slot, field.SemanticName, field.ResourceSet));
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

    static IrEntryPoint BuildEntryPoint(MethodSymbol method, IrFunction function) {
        var inputs = method.Parameters
            .Select((p, i) => new IrStageIo(p.Name, function.Parameters[i].Type, p.SemanticName))
            .ToArray();

        var output = function.ReturnType.IsVoid
            ? null
            : new IrStageIo("result", function.ReturnType, method.SemanticName);

        return new(method.Stage, function, inputs, output);
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

    void LowerStruct(NamedTypeSymbol type) {
        var structType = structs[type];

        ReportInheritanceNotFlattened(type);

        List<IrField> fields = [];
        foreach (var member in type.GetMembers()) {
            if (member is not FieldSymbol { IsConst: false, IsCompose: false } field) {
                continue;
            }

            var irType = LowerType(field.Type, field.DeclaringSyntax);
            if (!irType.IsVoid) {
                fields.Add(new(field.Name, irType));
            }
        }

        structType.SetFields(fields.ToArray());

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
                if (member is FieldSymbol { IsConst: false, IsCompose: false, IsValueParameter: false } field) {
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
                case MethodSymbol method when method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor: {
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

                    yield return (Unique(used, method.IsConstructor ? $"{type.Name}.init" : method.Name), body);
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
            add(LowerFunction(name, body, type, selfType));
        }
    }

    /// <summary>
    ///     Creates the signature for every method and property accessor of a type, so that
    ///     any body lowered later can call it.
    /// </summary>
    void DeclareMemberFunctions(NamedTypeSymbol type) {
        var selfType = type.TypeKind is TypeKind.Struct ? structs[type] : null;

        foreach (var (name, body) in MemberBodies(type, report: false)) {
            DeclareFunction(name, body, type, selfType);
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
            parameters.Add((parameter, function.AddParameter(parameter.Name, parameterType)));
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
