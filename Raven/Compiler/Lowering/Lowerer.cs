using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Lowering;

/// <summary>
/// Lowers a bound compilation to the target-independent <see cref="IrModule"/>.
/// </summary>
/// <remarks>
/// <para>
/// The three jobs here are erasure, explicitness and desugaring. Erasure: a
/// shader's fields have no runtime object, so <c>self.scale</c> becomes a global
/// binding and <c>self</c> disappears. Explicitness: every conversion, every
/// load and every store is its own instruction. Desugaring: <c>for</c> loops,
/// compound assignment and <c>++</c> become plain loops, loads and stores.
/// </para>
/// <para>
/// Lowering assumes the compilation bound cleanly. Anything the binder already
/// reported flows in as <see cref="ErrorTypeSymbol"/> and is passed over
/// silently rather than reported twice.
/// </para>
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

    Lowerer(Compilation compilation, DiagnosticBag diagnostics) {
        this.compilation = compilation;
        this.diagnostics = diagnostics;
        module = new IrModule(compilation.AssemblyName);
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
                case TypeKind.Struct or TypeKind.Class:
                    structs[type] = new IrStructType(type.Name);
                    break;
            }
        }

        foreach (var structType in structs.Values) {
            module.Add(structType);
        }

        foreach (var type in types) {
            switch (type.TypeKind) {
                case TypeKind.Shader:
                    LowerShader(type);
                    break;
                case TypeKind.Struct or TypeKind.Class:
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

        foreach (var member in type.GetMembers()) {
            // A `const` field is folded at every use, so it needs no binding.
            if (member is not FieldSymbol { IsConst: false } field) {
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
            shader.Add(new IrBinding(variable, kind, slot, field.SemanticName));
        }

        LowerMemberFunctions(type, shader.Add);
        LowerBindingInitializers(type, shader);

        foreach (var member in type.GetMembers()) {
            if (member is MethodSymbol { Stage: not ShaderStage.None } method &&
                functions.TryGetValue((method, BoundBodyKind.Method), out var function)) {
                shader.Add(BuildEntryPoint(method, function));
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

        return new IrEntryPoint(method.Stage, function, inputs, output);
    }

    /// <summary>
    /// Emits the stores that give bindings their declared defaults, as one block
    /// a backend can run before the first stage invocation.
    /// </summary>
    void LowerBindingInitializers(NamedTypeSymbol type, IrShader shader) {
        var initializer = new IrFunction($"{type.Name}.<init>", IrScalarType.Void);

        BeginFunction(initializer, type, selfType: null);

        foreach (var member in type.GetMembers()) {
            if (member is not FieldSymbol { IsConst: false } field
                || !globals.TryGetValue(field, out var variable)
                || FindBody(field, BoundBodyKind.FieldInitializer) is not { } body) {
                continue;
            }

            // The initializer body is `return <expression>`; take the expression.
            if (SingleReturnValue(body) is not { } expression) {
                continue;
            }

            var value = LowerExpression(expression);
            Emit(new IrStoreInstruction(new IrPlace(variable), value));
        }

        EndFunction();
        shader.Initializer.AddRange(initializer.Body.Statements);
    }

    static BoundExpression? SingleReturnValue(BoundBody body) =>
        body.Body.Statements is [BoundReturnStatement { Expression: { } expression }] ? expression : null;

    // --- Structs -----------------------------------------------------------

    void LowerStruct(NamedTypeSymbol type) {
        var structType = structs[type];

        List<IrField> fields = [];
        foreach (var member in type.GetMembers()) {
            if (member is not FieldSymbol { IsConst: false } field) {
                continue;
            }

            var irType = LowerType(field.Type, field.DeclaringSyntax);
            if (!irType.IsVoid) {
                fields.Add(new IrField(field.Name, irType));
            }
        }

        structType.SetFields(fields.ToArray());

        LowerMemberFunctions(type, module.Add);
    }

    // --- Functions ---------------------------------------------------------

    /// <summary>
    /// Creates and fills a function for every method and property accessor of a
    /// type, handing each finished function to <paramref name="add"/>.
    /// </summary>
    void LowerMemberFunctions(NamedTypeSymbol type, Action<IrFunction> add) {
        // A struct's methods take the receiver explicitly; a shader's do not,
        // because its fields are globals.
        var selfType = type.TypeKind is TypeKind.Struct or TypeKind.Class ? structs[type] : null;
        HashSet<string> used = [];

        foreach (var member in type.GetMembers()) {
            switch (member) {
                case MethodSymbol method when method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor: {
                    var kind = method.IsConstructor ? BoundBodyKind.Constructor : BoundBodyKind.Method;
                    if (FindBody(method, kind) is not { } body) {
                        diagnostics.Add(
                            LoweringDiagnostics.MissingBody,
                            LocationOf(method.DeclaringSyntax),
                            method.ToDisplayString());
                        continue;
                    }

                    var name = Unique(used, method.IsConstructor ? $"{type.Name}.init" : method.Name);
                    add(LowerFunction(name, body, type, selfType));
                    break;
                }

                case MethodSymbol method:
                    diagnostics.Add(
                        LoweringDiagnostics.ConstructNotSupported,
                        LocationOf(method.DeclaringSyntax),
                        $"A {Describe(method.MethodKind)} declaration");
                    break;

                case PropertySymbol property: {
                    foreach (var (kind, prefix) in
                             new[] { (BoundBodyKind.PropertyGetter, "get_"), (BoundBodyKind.PropertySetter, "set_") }) {
                        if (FindBody(property, kind) is { } body) {
                            add(LowerFunction(Unique(used, prefix + property.Name), body, type, selfType));
                        }
                    }

                    break;
                }
            }
        }
    }

    IrFunction LowerFunction(string name, BoundBody body, NamedTypeSymbol type, IrStructType? selfType) {
        // A struct's constructor builds a value and hands it back, rather than
        // mutating a receiver: the IR has no by-reference parameters.
        var constructsSelf = body.Kind == BoundBodyKind.Constructor && selfType is not null;

        var returnType = constructsSelf
            ? selfType!
            : LowerType(body.ReturnType, body.Member.DeclaringSyntax);

        var function = new IrFunction(name, returnType);
        functions[(body.Member, body.Kind)] = function;

        BeginFunction(function, type, selfType, constructsSelf);

        foreach (var parameter in body.Parameters) {
            var parameterType = LowerType(parameter.Type, parameter.DeclaringSyntax);
            variables[parameter] = function.AddParameter(parameter.Name, parameterType);
        }

        LowerStatement(body.Body);

        if (constructsSelf) {
            Emit(new IrReturnStatement(Load(SelfPlace!)));
        }

        EndFunction();

        return function;
    }

    void BeginFunction(
        IrFunction function,
        NamedTypeSymbol type,
        IrStructType? selfType,
        bool constructsSelf = false
    ) {
        currentFunction = function;
        currentType = type;
        currentBlock = function.Body;
        variables.Clear();
        selfParameter = null;
        selfLocal = null;

        if (selfType is null) {
            return;
        }

        if (constructsSelf) {
            selfLocal = function.AddLocal("self", selfType);
        }
        else {
            selfParameter = function.AddParameter("self", selfType);
        }
    }

    void EndFunction() {
        currentFunction = null;
        currentType = null;
        selfParameter = null;
        selfLocal = null;
        variables.Clear();
    }

    /// <summary>The receiver's storage, whether it arrived as a parameter or is being built.</summary>
    IrPlace? SelfPlace =>
        selfParameter is not null ? new IrPlace(selfParameter)
        : selfLocal is not null ? new IrPlace(selfLocal)
        : null;

    /// <summary>True while lowering a constructor that returns the value it builds.</summary>
    bool IsConstructingSelf => selfLocal is not null;

    static string Unique(HashSet<string> used, string name) {
        var candidate = name;
        var suffix = 1;
        while (!used.Add(candidate)) {
            candidate = $"{name}#{suffix++}";
        }

        return candidate;
    }

    static string Describe(MethodKind kind) => kind switch {
        MethodKind.Destructor => "destructor",
        MethodKind.Operator => "user-defined operator",
        MethodKind.Conversion => "conversion operator",
        MethodKind.LocalFunction => "local function",
        _ => "member"
    };

    // --- Emission helpers --------------------------------------------------

    IrFunction Function => currentFunction!;

    void Emit(IrStatement statement) => currentBlock.Add(statement);

    /// <summary>Emits an instruction and hands back the value it defines.</summary>
    IrValue Emit(Func<IrValue, IrInstruction> build, IrType resultType) {
        var result = Function.NewValue(resultType);
        currentBlock.Add(build(result));
        return result;
    }

    /// <summary>Collects everything <paramref name="body"/> emits into a fresh block.</summary>
    IrBlock EmitInto(Action body) {
        var previous = currentBlock;
        var block = new IrBlock();
        currentBlock = block;

        try {
            body();
        }
        finally {
            currentBlock = previous;
        }

        return block;
    }

    static Location LocationOf(SyntaxNode? syntax) => syntax?.GetLocation() ?? Location.None;

    void ReportUnsupported(BoundNode node, string what) =>
        diagnostics.Add(LoweringDiagnostics.ConstructNotSupported, node.Syntax.GetLocation(), what);
}
