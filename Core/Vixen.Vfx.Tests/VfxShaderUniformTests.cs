// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Syntax;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     The one place the emitter and the host have to agree byte for byte.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="VfxShaderUniforms" /> is a C# struct whose field order claims to be the
///         declaration order of the emitted shader's push-constant block. Nothing enforces that: a
///         field inserted in the middle of either one compiles perfectly and moves every value after
///         it to the wrong offset, which on a device shows up as an effect running at somebody
///         else's frame rate rather than as an error.
///     </para>
///     <para>
///         So the claim is checked against the compiler rather than asserted in a comment, and it is
///         checked by name: each member's reflected offset against <see cref="Marshal.OffsetOf" />
///         for the field that fills it. Comparing both to a list of literal numbers would pass just
///         as happily with both of them wrong in the same way.
///     </para>
/// </remarks>
public class VfxShaderUniformTests {
    /// <summary>Which C# field fills which push constant. The whole contract, as a table.</summary>
    static readonly (string Shader, string Host)[] Members = [
        ("deltaTime", nameof(VfxShaderUniforms.DeltaTime)),
        ("seed", nameof(VfxShaderUniforms.Seed)),
        ("first", nameof(VfxShaderUniforms.First)),
        ("particleCount", nameof(VfxShaderUniforms.ParticleCount)),
        ("time", nameof(VfxShaderUniforms.Time))
    ];

    static VfxCompiledGraph Graph() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(60f)],
            [
                new(VfxOpcode.PositionInSphere, new Vector4(0f, 0f, 0f, 1f)),
                new(VfxOpcode.SetLifetime, new Vector4(1f, 2f, 0f, 0f))
            ],
            [new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)), new(VfxOpcode.Integrate)],
            256
        );

    [Fact]
    public void The_push_constant_block_is_the_struct_the_host_writes() {
        var shader = VfxShaderEmitter.Emit(Graph(), "Effect");
        var tree = SyntaxTree.ParseText(shader.Source, path: "Effect.rvn");

        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Vfx", tree);

        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.Empty(bag.ToArray());

        var kernel = Assert.Single(module.Shaders, entry => entry.Name == shader.UpdateShader);
        var block = Assert.Single(ReflectionBuilder.Describe(kernel, compilation.UsedPermutationKeys).PushConstants);

        // Same members, in the same places, and no sixth one that the host would never write.
        Assert.Equal(Members.Length, block.Members.Length);

        foreach (var (declared, field) in Members) {
            var member = Assert.Single(block.Members, entry => entry.Name == declared);

            Assert.Equal((int)Marshal.OffsetOf<VfxShaderUniforms>(field), member.Offset);
        }

        Assert.Equal(VfxShaderUniforms.Size, block.Size);
        Assert.Equal(VfxShaderUniforms.Size, Marshal.SizeOf<VfxShaderUniforms>());
    }

    /// <summary>
    ///     Both kernels take the same block, which is what lets one pipeline layout serve both.
    /// </summary>
    [Fact]
    public void Both_kernels_declare_the_same_block() {
        var shader = VfxShaderEmitter.Emit(Graph(), "Effect");
        var tree = SyntaxTree.ParseText(shader.Source, path: "Effect.rvn");
        var compilation = Compilation.Create("Vfx", tree);
        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.Empty(bag.ToArray());

        var blocks = new[] { shader.InitializeShader, shader.UpdateShader }
            .Select(name => Assert.Single(module.Shaders, entry => entry.Name == name))
            .Select(kernel => Assert.Single(ReflectionBuilder.Describe(kernel, compilation.UsedPermutationKeys).PushConstants))
            .ToArray();

        Assert.Equal(
            blocks[0].Members.Select(member => (member.Name, member.Offset)),
            blocks[1].Members.Select(member => (member.Name, member.Offset))
        );

        Assert.Equal(blocks[0].Size, blocks[1].Size);
    }

    /// <summary>
    ///     Every attribute buffer is in set 0, because a pipeline layout numbers its sets by position
    ///     and set 2 would mean two empty layouts that exist only to be counted past.
    /// </summary>
    [Fact]
    public void The_attribute_buffers_are_all_in_the_first_set() {
        var shader = VfxShaderEmitter.Emit(Graph(), "Effect");
        var tree = SyntaxTree.ParseText(shader.Source, path: "Effect.rvn");
        var compilation = Compilation.Create("Vfx", tree);
        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.Empty(bag.ToArray());

        var kernel = Assert.Single(module.Shaders, entry => entry.Name == shader.UpdateShader);
        var reflection = ReflectionBuilder.Describe(kernel, compilation.UsedPermutationKeys);
        var set = Assert.Single(reflection.Sets);

        Assert.Equal(0, set.Set);

        // And in declaration order, which is what lets the host bind by index rather than by name.
        Assert.Equal(
            shader.Bindings.Select(binding => binding.Name),
            set.Bindings.Select(binding => binding.Name)
        );

        Assert.Equal(
            Enumerable.Range(0, shader.Bindings.Count),
            set.Bindings.Select(binding => binding.Binding)
        );
    }
}
