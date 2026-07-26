// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     Which target features a module needs, so the host can gate a pipeline on them.
/// </summary>
public class CapabilityTests {
    static IrModule LowerWith(string source, PermutationValues values) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", values, [tree]);
        var semantic = compilation.GetDiagnostics();
        Assert.True(
            semantic.Count == 0,
            "Expected no semantic diagnostics, got:\n" + string.Join("\n", semantic.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return module;
    }

    static IReadOnlyCollection<string> Capabilities(string source) =>
        IrCapabilities.Of(LowerWith(source, PermutationValues.Empty));

    [Fact]
    public void A_plain_float_shader_requires_nothing() =>
        Assert.Empty(
            Capabilities(
                """
                package A

                shader S {
                    var tint: float4

                    [PixelShader]
                    func Pixel(): float4 {
                        return tint
                    }
                }

                """
            )
        );

    [Fact]
    public void Double_maths_requires_Float64() =>
        Assert.Contains(
            IrCapability.Float64,
            Capabilities(
                """
                package A

                shader S {
                    var scale: double

                    [PixelShader]
                    func Pixel(): float4 {
                        var doubled = scale * 2.0
                        return float4(0.0f, 0.0f, 0.0f, 0.0f)
                    }
                }

                """
            )
        );

    [Theory]
    [InlineData("Texture3D", IrCapability.Texture3D)]
    [InlineData("TextureCube", IrCapability.TextureCube)]
    public void An_exotic_texture_requires_its_capability(string type, string capability) =>
        Assert.Contains(
            capability,
            Capabilities(
                $$"""
                  package A

                  shader S {
                      var map: {{type}}
                      var tint: float4

                      [PixelShader]
                      func Pixel(): float4 {
                          return tint
                      }
                  }

                  """
            )
        );

    [Fact]
    public void A_plain_2D_texture_requires_nothing() =>
        Assert.Empty(
            Capabilities(
                """
                package A

                shader S {
                    var map: Texture2D
                    var tint: float4

                    [PixelShader]
                    func Pixel(): float4 {
                        return tint
                    }
                }

                """
            )
        );

    [Fact]
    public void A_compute_stage_requires_Compute() =>
        Assert.Contains(
            IrCapability.Compute,
            Capabilities(
                """
                package A

                shader S {
                    var tint: float4

                    [ComputeShader(8, 8, 1)]
                    func Main() {
                    }
                }

                """
            )
        );

    /// <summary>
    ///     The reason this is collected from the lowered IR rather than from the symbols: a
    ///     variant that folds away its <c>double</c> maths must not ask the host for
    ///     <c>Float64</c> it will never use.
    /// </summary>
    [Fact]
    public void A_capability_behind_a_false_permutation_is_not_required() {
        const string Source = """
                              package A

                              shader S {
                                  [Permutation] val HighPrecision: bool = false

                                  var tint: float4
                                  var scale: float

                                  [PixelShader]
                                  func Pixel(): float4 {
                                      if (HighPrecision) {
                                          var precise = 2.0
                                          return tint * float(precise)
                                      }

                                      return tint * scale
                                  }
                              }

                              """;

        Assert.DoesNotContain(
            IrCapability.Float64,
            IrCapabilities.Of(LowerWith(Source, PermutationValues.Empty))
        );

        Assert.Contains(
            IrCapability.Float64,
            IrCapabilities.Of(LowerWith(Source, PermutationValues.Parse(["HighPrecision=true"])))
        );
    }

    /// <summary>
    ///     A pipeline is gated per shader, so one shader needing a feature must not make
    ///     another look as though it does.
    /// </summary>
    [Fact]
    public void Capabilities_are_reported_per_shader_as_well_as_per_module() {
        var module = LowerWith(
            """
            package A

            shader Plain {
                var tint: float4

                [PixelShader]
                func Pixel(): float4 {
                    return tint
                }
            }

            shader Precise {
                var scale: double
                var tint: float4

                [PixelShader]
                func Pixel(): float4 {
                    var doubled = scale * 2.0
                    return tint
                }
            }

            """,
            PermutationValues.Empty
        );

        Assert.Empty(IrCapabilities.Of(FindShader(module, "Plain")));
        Assert.Contains(IrCapability.Float64, IrCapabilities.Of(FindShader(module, "Precise")));

        // The module needs whatever any of its shaders needs.
        Assert.Contains(IrCapability.Float64, IrCapabilities.Of(module));
    }

    [Fact]
    public void The_reported_set_is_sorted_and_free_of_duplicates() {
        var capabilities = Capabilities(
            """
            package A

            shader S {
                var volume: Texture3D
                var sky: TextureCube
                var other: Texture3D
                var tint: float4

                [PixelShader]
                func Pixel(): float4 {
                    return tint
                }
            }

            """
        );

        Assert.Equal([IrCapability.Texture3D, IrCapability.TextureCube], capabilities);
    }
}
