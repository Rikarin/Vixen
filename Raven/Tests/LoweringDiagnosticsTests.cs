using Xunit;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
/// Phase 3: constructs the binder accepts but a GPU cannot represent are caught
/// at the IR boundary rather than in a backend.
/// </summary>
public class LoweringDiagnosticsTests {
    static void AssertLowering(string source, params string[] expectedIds) {
        var diagnostics = LoweringDiagnosticsOf(source);
        var actual = diagnostics.Select(d => d.Id).Distinct().ToArray();

        Assert.True(
            expectedIds.SequenceEqual(actual),
            $"Expected [{string.Join(", ", expectedIds)}] but got:\n"
            + string.Join("\n", diagnostics.Select(d => d.ToString())));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("long")]
    [InlineData("char")]
    [InlineData("object")]
    [InlineData("int?")]
    public void A_type_with_no_gpu_representation_is_rejected(string type) =>
        AssertLowering($$"""
            package A

            shader S {
                var value: {{type}}
            }

            """, "RVN3001");

    [Fact]
    public void A_tuple_field_is_rejected() =>
        AssertLowering("""
            package A

            shader S {
                var pair: (int, int)
            }

            """, "RVN3001");

    [Fact]
    public void A_lambda_is_rejected() =>
        AssertLowering("""
            package A

            shader S {
                func Probe() {
                    val f = (x: int) => x
                }
            }

            """, "RVN3001");

    [Fact]
    public void A_local_function_is_rejected() =>
        AssertLowering("""
            package A

            shader S {
                func Probe() {
                    func Inner(): int {
                        return 1
                    }
                }
            }

            """, "RVN3002");

    [Fact]
    public void A_user_defined_operator_is_rejected() =>
        AssertLowering("""
            package A

            struct Vec {
                var x: float

                Vec operator +(a: Vec, b: Vec) {
                    return a
                }
            }

            """, "RVN3002");

    [Fact]
    public void A_switch_expression_is_rejected() =>
        AssertLowering("""
            package A

            shader S {
                func Probe(x: int): int {
                    return x switch {
                        1 => 2,
                        _ => 3
                    }
                }
            }

            """, "RVN3002");

    [Fact]
    public void An_abstract_member_with_no_body_is_reported() =>
        AssertLowering("""
            package A

            shader S {
                func Bodied() { }
            }

            """);

    [Fact]
    public void Valid_shaders_report_nothing() =>
        AssertLowering("""
            package A

            shader S {
                var tint: float4
                var albedo: Texture2D
                var linear: Sampler

                [PixelShader]
                func Pixel(uv: float2): float4 {
                    return albedo.Sample(linear, uv) * tint
                }
            }

            """);
}
