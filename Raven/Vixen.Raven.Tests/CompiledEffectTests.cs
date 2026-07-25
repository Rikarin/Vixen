// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.CodeGen;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax.Diagnostics;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     <c>.rvnfx</c> — the compiled effect the runtime loads instead of compiling: every stage's
///     module, the reflection to bind it, and the provenance to know whether it is still valid.
/// </summary>
public class CompiledEffectTests {
    const string Source = """
                          package A

                          shader Lit {
                              [Permutation] val UseDetail: bool = false
                              [Permutation] val Unread: bool = true

                              var tint: float4
                              var albedo: Texture2D

                              [VertexShader]
                              func Vertex([Semantic("POSITION")] position: float3): float4 {
                                  return float4(position.x, position.y, position.z, 1.0f)
                              }

                              [PixelShader]
                              func Pixel(): float4 {
                                  if (UseDetail) {
                                      return tint * 2.0f
                                  }

                                  return tint
                              }
                          }

                          """;

    static CompiledEffect Build(
        string source = Source,
        string target = "spirv",
        PermutationValues? values = null
    ) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", values ?? PermutationValues.Empty, [tree]);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        var generated = TargetBackends.Create(target)!.Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        var shader = FindShader(module, "Lit");
        return CompiledEffect.Create(
            shader.Name,
            target,
            generated,
            ReflectionBuilder.Describe(shader, compilation.UsedPermutationKeys),
            values,
            [source]
        );
    }

    // --- Round trip ----------------------------------------------------------

    [Fact]
    public void An_effect_survives_a_write_and_read_unchanged() {
        var original = Build();
        var restored = CompiledEffectReader.Read(CompiledEffectWriter.Write(original));

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Target, restored.Target);
        Assert.Equal(original.SourceHash, restored.SourceHash);
        Assert.Equal(original.PermutationKey, restored.PermutationKey);
    }

    /// <summary>
    ///     Byte-exact, because these bytes are handed straight to a driver. A module that
    ///     round-tripped nearly correctly would fail somewhere far away from here.
    /// </summary>
    [Fact]
    public void Module_bytes_round_trip_exactly() {
        var original = Build();
        var restored = CompiledEffectReader.Read(CompiledEffectWriter.Write(original));

        Assert.Equal(original.Modules.Length, restored.Modules.Length);

        for (var i = 0; i < original.Modules.Length; i++) {
            Assert.Equal(original.Modules[i].Stage, restored.Modules[i].Stage);
            Assert.Equal(original.Modules[i].IsBinary, restored.Modules[i].IsBinary);
            Assert.Equal(original.Modules[i].Bytes, restored.Modules[i].Bytes);
        }
    }

    [Fact]
    public void Both_stages_are_carried_and_addressable_by_stage() {
        var effect = CompiledEffectReader.Read(CompiledEffectWriter.Write(Build()));

        Assert.Equal(2, effect.Modules.Length);
        Assert.NotNull(effect.ModuleFor(ShaderStage.Vertex));
        Assert.NotNull(effect.ModuleFor(ShaderStage.Pixel));
        Assert.Null(effect.ModuleFor(ShaderStage.Compute));
    }

    /// <summary>
    ///     A SPIR-V module must still be SPIR-V after the trip: the magic word is the first
    ///     thing a driver checks.
    /// </summary>
    [Fact]
    public void A_spirv_module_keeps_its_magic_word() {
        var effect = CompiledEffectReader.Read(CompiledEffectWriter.Write(Build()));
        var module = effect.ModuleFor(ShaderStage.Pixel)!;

        Assert.True(module.IsBinary);
        Assert.Equal([0x03, 0x02, 0x23, 0x07], module.Bytes.Take(4));
    }

    [Fact]
    public void A_source_target_round_trips_as_text() {
        var effect = CompiledEffectReader.Read(CompiledEffectWriter.Write(Build(target: "glsl")));
        var module = effect.ModuleFor(ShaderStage.Pixel)!;

        Assert.False(module.IsBinary);
        Assert.Contains("#version 450", module.AsText(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_reflection_survives_the_trip() {
        var effect = CompiledEffectReader.Read(CompiledEffectWriter.Write(Build()));
        var block = Assert.Single(effect.Reflection.Sets).Bindings[0];

        Assert.Equal("LitPerMaterialUniforms", block.Name);
        Assert.Equal(DescriptorType.UniformBuffer, block.Type);
        Assert.Equal(0, Assert.Single(block.Members, m => m.Name == "tint").Offset);
        Assert.Equal(["position"], effect.Reflection.VertexInputs.Select(i => i.Name));
    }

    // --- The cache key -------------------------------------------------------

    /// <summary>
    ///     The economy of the whole permutation system: a key that was declared but never read
    ///     is absent, so variants differing only in it share one artefact.
    /// </summary>
    [Fact]
    public void The_permutation_key_holds_only_the_keys_that_were_read() {
        var effect = Build(values: PermutationValues.Parse(["UseDetail=true", "Unread=false"]));

        Assert.Equal(["UseDetail"], effect.PermutationKey.Keys);
        Assert.Equal("true", effect.PermutationKey["UseDetail"]);
    }

    [Fact]
    public void An_unsupplied_key_records_that_the_default_was_used() {
        var effect = Build();

        Assert.Equal("default", effect.PermutationKey["UseDetail"]);
    }

    [Fact]
    public void Two_variants_of_the_same_key_produce_different_keys() {
        var off = Build(values: PermutationValues.Parse(["UseDetail=false"]));
        var on = Build(values: PermutationValues.Parse(["UseDetail=true"]));

        Assert.NotEqual(off.PermutationKey, on.PermutationKey);
    }

    [Fact]
    public void Varying_only_an_unread_key_produces_the_same_key() {
        var a = Build(values: PermutationValues.Parse(["Unread=true"]));
        var b = Build(values: PermutationValues.Parse(["Unread=false"]));

        Assert.Equal(a.PermutationKey, b.PermutationKey);
    }

    // --- Provenance ----------------------------------------------------------

    [Fact]
    public void The_source_hash_is_stable_for_the_same_source_and_changes_with_it() {
        var first = Build();
        var again = Build();

        Assert.NotEmpty(first.SourceHash);
        Assert.Equal(first.SourceHash, again.SourceHash);

        var edited = Build(Source.Replace("2.0f", "3.0f", StringComparison.Ordinal));
        Assert.NotEqual(first.SourceHash, edited.SourceHash);
    }

    // --- Rejecting bad input -------------------------------------------------

    [Fact]
    public void Something_that_is_not_an_effect_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => CompiledEffectReader.Read(Encoding.UTF8.GetBytes("not an effect")));

    [Fact]
    public void An_empty_file_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => CompiledEffectReader.Read([]));

    [Fact]
    public void A_future_version_is_rejected_rather_than_guessed_at() {
        var bytes = CompiledEffectWriter.Write(Build());
        bytes[CompiledEffectFormat.Magic.Length] = 99;

        var exception = Assert.Throws<InvalidDataException>(() => CompiledEffectReader.Read(bytes));
        Assert.Contains("99", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A truncated artefact is reported here rather than handed on as a short module, which
    ///     would surface as a driver error with no trace of the real cause.
    /// </summary>
    [Fact]
    public void A_truncated_effect_is_rejected() {
        var bytes = CompiledEffectWriter.Write(Build());

        Assert.Throws<InvalidDataException>(() => CompiledEffectReader.Read(bytes.AsSpan(0, bytes.Length - 16)));
    }

    [Fact]
    public void A_file_round_trips_through_disk() {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".rvnfx");

        try {
            var original = Build();
            CompiledEffectWriter.WriteFile(path, original);
            var restored = CompiledEffectReader.ReadFile(path);

            Assert.Equal(original.Modules[0].Bytes, restored.Modules[0].Bytes);
            Assert.Equal(original.SourceHash, restored.SourceHash);
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     The header is JSON on purpose: a shipped artefact should be inspectable without a
    ///     bespoke viewer.
    /// </summary>
    [Fact]
    public void The_header_is_readable_json_and_the_payload_is_raw() {
        var bytes = CompiledEffectWriter.Write(Build());
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"Name\":\"Lit\"", text, StringComparison.Ordinal);
        Assert.Contains("\"UniformBuffer\"", text, StringComparison.Ordinal);

        // Not base64: the SPIR-V words are appended verbatim after the header.
        Assert.DoesNotContain("\"Bytes\"", text, StringComparison.Ordinal);
    }
}
