using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>Phase 3: the bound tree lowers to a verifiable, target-independent IR.</summary>
public class LoweringTests {
    [Fact]
    public void Shader_fields_become_bindings_with_per_kind_slots() {
        var module = Lower(
            """
            package A

            shader S {
                var tint: float4
                var scale: float
                var albedo: Texture2D
                var normalMap: Texture2D
                var linear: Sampler
            }

            """
        );

        var shader = FindShader(module, "S");

        Assert.Equal(
            [
                ("tint", IrBindingKind.Uniform, 0),
                ("scale", IrBindingKind.Uniform, 1),
                ("albedo", IrBindingKind.Texture, 0),
                ("normalMap", IrBindingKind.Texture, 1),
                ("linear", IrBindingKind.Sampler, 0)
            ],
            shader.Bindings.Select(b => (b.Name, b.Kind, b.Slot))
        );

        Assert.Equal("vec<f32,4>", shader.Bindings[0].Type.Name);
        Assert.Equal("texture2d", shader.Bindings[2].Type.Name);
    }

    [Fact]
    public void A_const_field_gets_no_binding_and_is_folded_at_its_use() {
        var module = Lower(
            """
            package A

            shader S {
                const val Bias = 0.5

                func Read(): float {
                    return Bias
                }
            }

            """
        );

        Assert.Empty(FindShader(module, "S").Bindings);

        Assert.Equal(
            """
            func Read() : f32
              %0 = const 0.5f : f32
              return %0
            end

            """,
            PrintFunction(module, "Read")
        );
    }

    [Fact]
    public void A_field_initializer_becomes_the_shader_initializer() {
        var module = Lower(
            """
            package A

            shader S {
                var scale: float = 2
            }

            """
        );

        var shader = FindShader(module, "S");
        var store = Assert.IsType<IrStoreInstruction>(
            Assert.Single(shader.Initializer.Statements.OfType<IrStoreInstruction>())
        );

        Assert.Equal("@scale", store.Place.ToString());
    }

    [Fact]
    public void Accessing_a_shader_field_erases_the_receiver_into_a_global() =>
        Assert.Equal(
            """
            func Probe() : f32
              %0 = load @scale : f32
              return %0
            end

            """,
            LowerBody("        return scale", "    var scale: float\n", "func Probe(): float")
        );

    [Fact]
    public void Conversions_are_explicit_instructions() =>
        Assert.Equal(
            """
            func Probe() : f32
              local !whole : i32
              %0 = const 2 : i32
              store !whole, %0
              %1 = load !whole : i32
              %2 = convert.numeric %1 : f32
              return %2
            end

            """,
            LowerBody(
                """
                            val whole: int = 2
                            return whole
                """,
                signature: "func Probe(): float"
            )
        );

    [Fact]
    public void A_scalar_used_at_a_vector_type_becomes_a_splat() =>
        Assert.Equal(
            """
            func Probe($v : vec<f32,3>) : vec<f32,3>
              %0 = load $v : vec<f32,3>
              %1 = const 2f : f32
              %2 = convert.splat %1 : vec<f32,3>
              %3 = multiply %0, %2 : vec<f32,3>
              return %3
            end

            """,
            LowerBody("        return v * 2f", signature: "func Probe(v: float3): float3")
        );

    [Fact]
    public void Swizzles_become_access_chain_steps() {
        var printed = LowerBody("        return v.xy", signature: "func Probe(v: float4): float2");

        Assert.Contains("load $v.xy : vec<f32,2>", printed);
    }

    [Fact]
    public void Compound_assignment_and_increment_are_desugared() =>
        Assert.Equal(
            """
            func Probe() : i32
              local !x : i32
              %0 = const 1 : i32
              store !x, %0
              %1 = load !x : i32
              %2 = const 2 : i32
              %3 = add %1, %2 : i32
              store !x, %3
              %4 = load !x : i32
              %5 = const 1 : i32
              %6 = add %4, %5 : i32
              store !x, %6
              %7 = load !x : i32
              return %7
            end

            """,
            LowerBody(
                """
                            var x = 1
                            x += 2
                            x++
                            return x
                """,
                signature: "func Probe(): int"
            )
        );

    [Fact]
    public void A_range_for_loop_becomes_a_counted_loop() {
        var printed = LowerBody(
            """
                        var total = 0
                        for (i in 1 .. 4) {
                            total = total + i
                        }
            """
        );

        // The bound is hoisted into its own local rather than re-evaluated.
        Assert.Contains("local !i#limit : i32", printed);
        Assert.Contains("test %", printed);
        Assert.Contains("before-body", printed);
        Assert.Contains("step", printed);
    }

    [Fact]
    public void An_array_for_loop_indexes_the_sequence() {
        var printed = LowerBody(
            """
                        var total = 0
                        for (n in numbers) {
                            total = total + n
                        }
            """,
            "    var numbers: int[]\n"
        );

        Assert.Contains("local !n#index : i32", printed);
        Assert.Contains("intrinsic.arrayLength", printed);
        Assert.Contains("load @numbers[%", printed);
    }

    [Fact]
    public void While_and_repeat_differ_only_in_when_the_test_runs() {
        var whileLoop = LowerBody(
            """
                        while (flag) {
                        }
            """,
            "    var flag: bool\n"
        );

        var repeatLoop = LowerBody(
            """
                        repeat {
                        } while (flag)
            """,
            "    var flag: bool\n"
        );

        Assert.Contains("before-body", whileLoop);
        Assert.Contains("after-body", repeatLoop);
    }

    [Fact]
    public void An_if_statement_keeps_its_structure() {
        var printed = LowerBody(
            """
                        if (flag) {
                            other = 1f
                        } else {
                            other = 2f
                        }
            """,
            "    var flag: bool\n    var other: float\n"
        );

        Assert.Contains("if %0", printed);
        Assert.Contains("else", printed);
        Assert.Contains("end", printed);
    }

    [Fact]
    public void Intrinsics_resolve_to_opcodes_and_mul_becomes_an_operator() {
        var printed = LowerBody(
            "        return dot(normalize(a), b)",
            signature: "func Probe(a: float3, b: float3): float"
        );

        Assert.Contains("intrinsic.normalize", printed);
        Assert.Contains("intrinsic.dot", printed);

        var matrix = LowerBody(
            "        return mul(m, v)",
            signature: "func Probe(m: mat3, v: float3): float3"
        );

        Assert.Contains("matrixMultiply", matrix);
        Assert.DoesNotContain("intrinsic.mul", matrix);
    }

    [Fact]
    public void Texture_sampling_evaluates_the_receiver_first() {
        var printed = LowerBody(
            "        return albedo.Sample(linear, uv)",
            "    var albedo: Texture2D\n    var linear: Sampler\n",
            "func Probe(uv: float2): float4"
        );

        Assert.Equal(
            """
            func Probe($uv : vec<f32,2>) : vec<f32,4>
              %0 = load @albedo : texture2d
              %1 = load @linear : sampler
              %2 = load $uv : vec<f32,2>
              %3 = intrinsic.sampleTexture %0, %1, %2 : vec<f32,4>
              return %3
            end

            """,
            printed
        );
    }

    [Fact]
    public void Vector_construction_becomes_a_composite_construct() {
        var printed = LowerBody(
            "        return float4(v, 1)",
            signature: "func Probe(v: float3): float4"
        );

        Assert.Contains("construct %0, %1 : vec<f32,4>", printed);

        // A single scalar broadcasts instead of building from parts.
        var splat = LowerBody("        return float3(0)", signature: "func Probe(): float3");
        Assert.Contains("convert.splat", splat);
    }

    [Fact]
    public void A_conditional_expression_becomes_a_select() {
        var printed = LowerBody(
            "        return flag ? 1f : 2f",
            "    var flag: bool\n",
            "func Probe(): float"
        );

        Assert.Contains("select %0, %1, %2 : f32", printed);
    }

    [Fact]
    public void Calls_between_shader_functions_take_no_receiver() {
        var module = Lower(
            """
            package A

            shader S {
                func Helper(x: float): float {
                    return x
                }

                func Probe(): float {
                    return Helper(1f)
                }
            }

            """
        );

        Assert.Contains("call Helper(%0) : f32", PrintFunction(module, "Probe"));
    }

    [Fact]
    public void A_struct_becomes_an_ir_struct_and_its_methods_take_self() {
        var module = Lower(
            """
            package A

            struct Ray {
                var origin: float3
                var direction: float3

                func At(t: float): float3 {
                    return origin + direction * t
                }
            }

            """
        );

        var structType = Assert.Single(module.Structs);
        Assert.Equal("Ray", structType.Name);
        Assert.Equal(["origin", "direction"], structType.Fields.Select(f => f.Name));

        var method = FindFunction(module, "At");
        Assert.Equal(["self", "t"], method.Parameters.Select(p => p.Name));
        Assert.Same(structType, method.Parameters[0].Type);

        // The receiver is explicit, and fields are reached through it.
        Assert.Contains("load $self.0", IrPrinter.Print(method));
    }

    [Fact]
    public void A_constructor_lowers_to_a_function_returning_the_struct() {
        var module = Lower(
            """
            package A

            struct Point {
                var x: float

                init(value: float) {
                    x = value
                }
            }

            """
        );

        var constructor = FindFunction(module, "Point.init");
        Assert.Equal("Point", constructor.ReturnType.Name);
        Assert.Equal(["value"], constructor.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void Properties_lower_to_getter_and_setter_functions() {
        var module = Lower(
            """
            package A

            shader S {
                var backing: float

                var scaled: float {
                    get => backing
                    set => backing = value
                }

                func Probe() {
                    scaled = scaled
                }
            }

            """
        );

        Assert.NotNull(FindFunction(module, "get_scaled"));
        Assert.NotNull(FindFunction(module, "set_scaled"));

        var printed = PrintFunction(module, "Probe");
        Assert.Contains("call get_scaled()", printed);
        Assert.Contains("call set_scaled(%", printed);
    }

    [Fact]
    public void Entry_points_carry_their_stage_and_interface() {
        var module = Lower(
            """
            package A

            shader S {
                [VertexShader]
                [Semantic("SV_Position")]
                func Vertex(position: float3): float4 {
                    return float4(position, 1)
                }
            }

            """
        );

        var entryPoint = Assert.Single(FindShader(module, "S").EntryPoints);

        Assert.Equal(ShaderStage.Vertex, entryPoint.Stage);
        Assert.Equal("Vertex", entryPoint.Function.Name);
        Assert.Equal("position", Assert.Single(entryPoint.Inputs).Name);
        Assert.Equal("vec<f32,3>", entryPoint.Inputs[0].Type.Name);
        Assert.Equal("SV_Position", entryPoint.Output?.Semantic);
    }

    [Fact]
    public void Value_numbering_is_per_function() {
        var module = Lower(
            """
            package A

            shader S {
                func First(): int {
                    return 1
                }

                func Second(): int {
                    return 2
                }
            }

            """
        );

        // Both start at %0 — ids mean nothing outside their function.
        Assert.Contains("%0 = const 1", PrintFunction(module, "First"));
        Assert.Contains("%0 = const 2", PrintFunction(module, "Second"));
    }

    /// <summary>Wraps a method body in a shader and returns the printed function.</summary>
    static string LowerBody(string body, string members = "", string signature = "func Probe()") {
        var module = Lower(
            $$"""
              package A

              shader S {
              {{members}}
                  {{signature}} {
              {{body}}
                  }
              }

              """
        );

        return PrintFunction(module, "Probe");
    }
}
