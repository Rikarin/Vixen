// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Artefacts;
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
        shader.Add(new IrEntryPoint(ShaderStage.Vertex, stray, [new("x", IrScalarType.Float, null)], []));

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

                [FragmentShader]
                func Fragment(normal: float3, uv: float2): float4 {
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

    // --- Struct layout terminates -----------------------------------------
    //
    // ⚠ RVN2008 is a *source* rule — it runs in the binder over syntax — so it cannot see a struct
    // that arrived through a `.rvnlib`. The artefact reader deliberately rebuilds a graph that may
    // contain cycles and nothing between it and the first walk of the type graph asked whether the
    // graph it rebuilt is one the compiler can walk (#1153). These modules are built the way the
    // decoder builds one: shells first, fields second.

    /// <summary>Two structs that hold each other never lay out, and the verifier says so.</summary>
    [Fact]
    public void Two_structs_that_hold_each_other_do_not_verify() {
        var a = new IrStructType("A");
        var b = new IrStructType("B");

        a.SetFields([new("b", b)]);
        b.SetFields([new("a", a)]);

        var diagnostic = Assert.Single(Verify(ModuleWith(a, b)));

        Assert.Equal("RVN3010", diagnostic.Id);

        // The route rather than the type: naming one end sends the reader to the file where nothing
        // is wrong, which is the reason RVN2008 gives for the same message shape.
        Assert.Equal(
            "laying out struct 'A' never terminates: A.b: B → B.a: A",
            diagnostic.GetMessage()
        );
    }

    /// <summary>A struct that holds itself directly is the degenerate case of the same thing.</summary>
    [Fact]
    public void A_struct_that_holds_itself_does_not_verify() {
        var self = new IrStructType("Node");
        self.SetFields([new("next", self)]);

        Assert.Contains(Verify(ModuleWith(self)), d => d.GetMessage().Contains("Node.next: Node"));
    }

    /// <summary>An array of the type is the same infinity, so the walk goes through its element.</summary>
    /// <remarks>
    ///     <c>T[4]</c> is <c>T</c> laid out four times. A check that stopped at an array would call
    ///     the commonest spelling of the bug well formed.
    /// </remarks>
    [Fact]
    public void A_struct_that_holds_an_array_of_itself_does_not_verify() {
        var self = new IrStructType("Cell");
        self.SetFields([new("children", new IrArrayType(self, 4))]);

        Assert.Contains(Verify(ModuleWith(self)), d => d.GetMessage().Contains("never terminates"));
    }

    /// <summary>
    ///     ⚠ And the positive half: a deep, shared, acyclic graph still verifies.
    /// </summary>
    /// <remarks>
    ///     The diamond is the part that matters. A walk that marked a struct visited and then read
    ///     a second visit as a cycle would reject this — which is the failure mode a check like this
    ///     has, and it would reject every real module, since a struct held by two others is ordinary.
    /// </remarks>
    [Fact]
    public void A_deep_acyclic_struct_graph_verifies() {
        var leaf = new IrStructType("Leaf");
        var left = new IrStructType("Left");
        var right = new IrStructType("Right");
        var root = new IrStructType("Root");

        leaf.SetFields([new("x", IrScalarType.Float)]);
        left.SetFields([new("leaf", leaf)]);
        right.SetFields([new("leaf", leaf), new("leaves", new IrArrayType(leaf, 8))]);
        root.SetFields([new("left", left), new("right", right), new("also", leaf)]);

        Assert.Empty(Verify(ModuleWith(root, left, right, leaf)));
    }

    /// <summary>
    ///     ⚠ Identity and not the name, which is what the tuple exemption needs.
    /// </summary>
    /// <remarks>
    ///     A tuple and a monomorphised generic keep a bare artefact key on purpose, so a check that
    ///     walked keys or names would report a cycle between two libraries' unrelated <c>Shape</c>
    ///     that is not there. Struct identity in the IR is reference identity, and this is that
    ///     claim as an assertion: two distinct objects that share a name are two types.
    /// </remarks>
    [Fact]
    public void Two_structs_that_only_share_a_name_are_not_a_cycle() {
        var outer = new IrStructType("Shape");
        var inner = new IrStructType("Shape");

        inner.SetFields([new("radius", IrScalarType.Float)]);
        outer.SetFields([new("inner", inner)]);

        Assert.Empty(Verify(ModuleWith(outer, inner)));
    }

    /// <summary>
    ///     A <c>.rvnlib</c> whose struct table holds a cycle is refused rather than walked.
    /// </summary>
    /// <remarks>
    ///     Through <see cref="LibraryIrDecoder" /> rather than by hand, because the decoder is the
    ///     door this defect comes in by: it loads every shell before any field precisely so a cycle
    ///     decodes, which is right for a reader and is why the question has to be asked afterwards.
    /// </remarks>
    [Fact]
    public void A_library_whose_struct_table_holds_a_cycle_is_refused() {
        var decoder = new LibraryIrDecoder(name => name);

        decoder.DecodeStructs(new() {
            Structs = [
                new() { Key = "Lib::A", Name = "A", Fields = [new("b", Reference("Lib::B"))] },
                new() { Key = "Lib::B", Name = "B", Fields = [new("a", Reference("Lib::A"))] }
            ]
        });

        var module = new IrModule("Consumer");

        foreach (var structType in decoder.Structs.Values) {
            module.Add(structType);
        }

        Assert.Contains(Verify(module), d => d.GetMessage().Contains("never terminates"));
    }

    static LibraryIrTypeReference Reference(string key) =>
        new() { Kind = IrTypeKind.Struct, Struct = key };

    static IrModule ModuleWith(params IrStructType[] structs) {
        var module = new IrModule("Test");

        foreach (var structType in structs) {
            module.Add(structType);
        }

        return module;
    }
}
