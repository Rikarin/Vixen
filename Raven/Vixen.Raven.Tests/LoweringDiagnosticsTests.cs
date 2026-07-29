// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     Phase 3: constructs the binder accepts but a GPU cannot represent are caught
///     at the IR boundary rather than in a backend.
/// </summary>
/// <remarks>
///     The list is short because most of what used to land here — lambdas, nullable
///     types, <c>string</c>, <c>char</c>, <c>long</c>, <c>object</c> — was removed
///     from the language instead. What remains is either implementable and not
///     implemented yet, or a type built structurally from parts.
/// </remarks>
public class LoweringDiagnosticsTests {
    [Fact]
    public void An_ordinary_member_reports_nothing() =>
        AssertLowering(
            """
            package A

            shader S {
                func Bodied() { }
            }

            """
        );

    [Fact]
    public void Valid_shaders_report_nothing() =>
        AssertLowering(
            """
            package A

            shader S {
                var tint: float4
                var albedo: Texture2D
                var linear: Sampler

                [FragmentShader]
                func Fragment(uv: float2): float4 {
                    return albedo.Sample(linear, uv) * tint
                }
            }

            """
        );

    static void AssertLowering(string source, params string[] expectedIds) {
        var diagnostics = LoweringDiagnosticsOf(source);
        var actual = diagnostics.Select(d => d.Id).Distinct().ToArray();

        Assert.True(
            expectedIds.SequenceEqual(actual),
            $"Expected [{string.Join(", ", expectedIds)}] but got:\n"
            + string.Join("\n", diagnostics.Select(d => d.ToString()))
        );
    }
}
