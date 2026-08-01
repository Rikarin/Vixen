// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The order the files arrive in is not part of the program.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this is here to stop happening again.</b> Lowering used to declare a shader's
///         storage and lower its code in one pass, so a derived shader lowered before its base found
///         no <c>globals</c> entry for the base's buffer and reported <c>RVN3002</c> on
///         <c>buffer.Length</c> — code that is correct, in a program that compiles, failing because
///         of where its two halves sat in a list.
///     </para>
///     <para>
///         ⚠ <b>And the list is not sorted anywhere it comes from a directory.</b>
///         <c>Directory.GetFiles</c> returns filesystem order, so the shipped library compiled on the
///         APFS machine it was written on and failed on the ext4 CI runner — around fifty tests, on
///         one leg, against a shader nobody had touched. That is the whole reason this file asserts
///         <em>both</em> orders rather than one: a test that only ever ran the order that worked is
///         exactly what was already there.
///     </para>
/// </remarks>
public sealed class SourceOrderTests {
    /// <summary>A base shader whose buffer a derived shader's inherited code reads the length of.</summary>
    const string Base = """
        package A

        struct Item {
            var value: float
        }

        shader Storage {
            var items: Buffer<Item>

            func Count(): int => items.Length
        }

        """;

    /// <summary>The shader that inherits it, and so lowers a copy of <c>Count</c> against its storage.</summary>
    const string Derived = """
        package A

        shader Reader : Storage {
            [FragmentShader]
            [Semantic("SV_Target")]
            func Fragment(): float4 => float4(float(Count()))
        }

        """;

    [Fact]
    public void A_base_declared_after_its_derived_shader_still_has_storage() {
        // The order that used to fail: the derived shader is lowered first, so the base's `items`
        // was not yet a global when the inherited copy of `Count` asked where it lived.
        AssertLowersCleanly(Derived, Base);
    }

    [Fact]
    public void A_base_declared_before_its_derived_shader_still_has_storage() {
        AssertLowersCleanly(Base, Derived);
    }

    /// <summary>Lowers the trees in the order given and asserts nothing was reported.</summary>
    static void AssertLowersCleanly(params string[] sources) {
        var trees = sources
            .Select((source, index) => SyntaxTree.ParseText(source, path: $"Source{index}.rvn"))
            .ToArray();

        foreach (var tree in trees) {
            Assert.Empty(tree.Diagnostics);
        }

        var compilation = Compilation.Create("Test", trees);

        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.IsError);

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);

        var errors = bag.Where(d => d.IsError).ToArray();

        Assert.True(
            errors.Length == 0,
            "Lowering reported:\n" + string.Join("\n", errors.Select(d => d.ToString()))
        );
    }
}
