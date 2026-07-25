using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 3: the verifier rejects malformed IR, so a backend can assume what it
///     is given is well formed. The modules here are hand-built to be wrong in one
///     specific way each.
/// </summary>
public class IrVerifierTests {
    [Fact]
    public void A_well_formed_function_verifies() => Assert.Empty(Verify(ModuleWith(Identity())));

    [Fact]
    public void Using_a_value_that_was_never_defined_is_rejected() {
        var function = new IrFunction("Bad", IrScalarType.Float);
        var stray = new IrValue(7, IrScalarType.Float);
        function.Body.Add(new IrReturnStatement(stray));

        var diagnostic = Assert.Single(Verify(ModuleWith(function)));
        Assert.Equal("RVN3010", diagnostic.Id);
        Assert.Contains("%7", diagnostic.GetMessage());
    }

    [Fact]
    public void A_value_defined_inside_a_branch_does_not_escape_it() {
        var function = new IrFunction("Bad", IrScalarType.Float);

        var condition = function.NewValue(IrScalarType.Bool);
        function.Body.Add(new IrConstantInstruction(condition, true));

        // %1 is defined in the `then` branch, which does not dominate the return.
        var inner = function.NewValue(IrScalarType.Float);
        var then = new IrBlock();
        then.Add(new IrConstantInstruction(inner, 1f));

        function.Body.Add(new IrIfStatement(condition, then, null));
        function.Body.Add(new IrReturnStatement(inner));

        Assert.Contains(Verify(ModuleWith(function)), d => d.GetMessage().Contains("%1"));
    }

    [Fact]
    public void Defining_the_same_value_twice_is_rejected() {
        var function = new IrFunction("Bad", IrScalarType.Void);
        var value = new IrValue(0, IrScalarType.Int);

        function.Body.Add(new IrConstantInstruction(value, 1));
        function.Body.Add(new IrConstantInstruction(value, 2));

        Assert.Contains(Verify(ModuleWith(function)), d => d.GetMessage().Contains("defined more than once"));
    }

    [Fact]
    public void Mismatched_operand_types_are_rejected() {
        var function = new IrFunction("Bad", IrScalarType.Float);

        var left = function.NewValue(IrScalarType.Float);
        var right = function.NewValue(IrScalarType.Int);
        var sum = function.NewValue(IrScalarType.Float);

        function.Body.Add(new IrConstantInstruction(left, 1f));
        function.Body.Add(new IrConstantInstruction(right, 1));
        function.Body.Add(new IrBinaryInstruction(sum, IrBinaryOp.Add, left, right));
        function.Body.Add(new IrReturnStatement(sum));

        Assert.Contains(Verify(ModuleWith(function)), d => d.GetMessage().Contains("does not match"));
    }

    [Fact]
    public void Storing_the_wrong_type_is_rejected() {
        var function = new IrFunction("Bad", IrScalarType.Void);
        var local = function.AddLocal("x", IrScalarType.Float);

        var value = function.NewValue(IrScalarType.Int);
        function.Body.Add(new IrConstantInstruction(value, 1));
        function.Body.Add(new IrStoreInstruction(new(local), value));

        Assert.Contains(Verify(ModuleWith(function)), d => d.GetMessage().Contains("store"));
    }

    [Fact]
    public void A_non_boolean_condition_is_rejected() {
        var function = new IrFunction("Bad", IrScalarType.Void);

        var condition = function.NewValue(IrScalarType.Int);
        function.Body.Add(new IrConstantInstruction(condition, 1));
        function.Body.Add(new IrIfStatement(condition, new(), null));

        Assert.Contains(Verify(ModuleWith(function)), d => d.GetMessage().Contains("expected bool"));
    }

    [Fact]
    public void An_access_chain_that_does_not_fit_its_root_is_rejected() {
        var function = new IrFunction("Bad", IrScalarType.Void);
        var local = function.AddLocal("scalar", IrScalarType.Float);

        // A field access into a scalar is meaningless.
        var value = function.NewValue(IrScalarType.Float);
        function.Body.Add(new IrLoadInstruction(value, new(local, [new IrFieldAccess(0)])));

        Assert.Contains(Verify(ModuleWith(function)), d => d.GetMessage().Contains("is not valid on"));
    }

    [Fact]
    public void A_call_with_the_wrong_argument_count_is_rejected() {
        var callee = Identity();
        var caller = new IrFunction("Caller", IrScalarType.Void);
        caller.Body.Add(new IrCallInstruction(null, callee, []));

        var module = new IrModule("Test");
        module.Add(callee);
        module.Add(caller);

        Assert.Contains(Verify(module), d => d.GetMessage().Contains("passes 0 arguments"));
    }

    [Fact]
    public void Break_outside_a_loop_is_rejected() {
        var function = new IrFunction("Bad", IrScalarType.Void);
        function.Body.Add(new IrBreakStatement());

        Assert.Contains(Verify(ModuleWith(function)), d => d.GetMessage().Contains("'break' outside a loop"));
    }

    [Fact]
    public void Break_inside_a_loop_is_accepted() {
        var function = new IrFunction("Loop", IrScalarType.Void);

        var condition = new IrBlock();
        var flag = function.NewValue(IrScalarType.Bool);
        condition.Add(new IrConstantInstruction(flag, true));

        var body = new IrBlock();
        body.Add(new IrBreakStatement());

        function.Body.Add(new IrLoopStatement(condition, flag, body, null, true));

        Assert.Empty(Verify(ModuleWith(function)));
    }

    [Fact]
    public void A_value_returning_function_that_can_fall_off_the_end_is_rejected() {
        var function = new IrFunction("Bad", IrScalarType.Float);

        Assert.Contains(
            Verify(ModuleWith(function)),
            d => d.GetMessage().Contains("can finish without returning")
        );
    }

    [Fact]
    public void An_entry_point_must_belong_to_its_shader() {
        var stray = Identity();
        var shader = new IrShader("S");
        shader.Add(new IrEntryPoint(ShaderStage.Vertex, stray, [new("x", IrScalarType.Float, null)], null));

        var module = new IrModule("Test");
        module.Add(shader);

        Assert.Contains(Verify(module), d => d.GetMessage().Contains("is not a function of shader"));
    }

    [Fact]
    public void Two_bindings_may_not_share_a_slot() {
        var shader = new IrShader("S");
        var first = new IrVariable("a", IrScalarType.Float, IrVariableKind.Global);
        var second = new IrVariable("b", IrScalarType.Float, IrVariableKind.Global);

        shader.Add(new IrBinding(first, IrBindingKind.Uniform, 0, null));
        shader.Add(new IrBinding(second, IrBindingKind.Uniform, 0, null));

        var module = new IrModule("Test");
        module.Add(shader);

        Assert.Contains(Verify(module), d => d.GetMessage().Contains("reuses Uniform slot 0"));
    }

    [Fact]
    public void Everything_the_lowerer_produces_verifies() {
        // The lowering suite runs the verifier on every module it builds; this
        // pins the contract explicitly for a realistic shader.
        var module = LoweringTestBase.Lower(
            """
            package A

            shader Lit {
                var world: mat4
                var albedo: Texture2D
                var linear: Sampler

                func Shade(normal: float3): float {
                    return saturate(dot(normalize(normal), float3(0, 1, 0)))
                }

                [VertexShader]
                func Vertex(position: float3): float4 {
                    return world * float4(position, 1)
                }

                [PixelShader]
                func Pixel(normal: float3, uv: float2): float4 {
                    val sampled = albedo.Sample(linear, uv)
                    return float4(sampled.rgb * Shade(normal), sampled.a)
                }
            }

            """
        );

        Assert.Empty(Verify(module));
    }

    static IReadOnlyList<Diagnostic> Verify(IrModule module) {
        var bag = new DiagnosticBag();
        IrVerifier.Verify(module, bag);
        return bag.ToArray();
    }

    static IrModule ModuleWith(IrFunction function) {
        var module = new IrModule("Test");
        module.Add(function);
        return module;
    }

    /// <summary>A function that loads its one parameter and returns it.</summary>
    static IrFunction Identity() {
        var function = new IrFunction("Identity", IrScalarType.Float);
        var parameter = function.AddParameter("x", IrScalarType.Float);

        var loaded = function.NewValue(IrScalarType.Float);
        function.Body.Add(new IrLoadInstruction(loaded, new(parameter)));
        function.Body.Add(new IrReturnStatement(loaded));

        return function;
    }
}
