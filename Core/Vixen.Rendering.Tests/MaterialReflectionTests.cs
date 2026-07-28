// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Vixen.Rendering;
using Vixen.Rendering.Materials;
using Xunit;

namespace Tests;

/// <summary>
///     The material compiler's predicted parameter names, against what Raven actually emitted.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="MaterialCompiler" /> works out what a composed feature's parameters will be
///         called without a compiler in the process — it has to, because a material is authored,
///         edited and serialised on machines that never compile a shader, and because a shipping
///         build must be able to build the key that finds a baked effect without linking Raven at
///         all. The cost of that is a rule written down in two places, and a rule written down twice
///         is a rule that drifts.
///     </para>
///     <para>
///         So this is the oracle. <c>Raven/Library/Pipeline/ForwardPlus.reflect.json</c> is the
///         reflection Raven produces for the pass composed the way the engine's default material
///         composes it, regenerated and compared by <c>LibraryReflectionTests</c> on the compiler's
///         side. Here, the same names are predicted and held against it. Change the qualification
///         rule in either project and one of the two tests fails.
///     </para>
///     <para>
///         The prefix is the difference between the two lists and it is not drift: reflection names a
///         parameter as the shader sees it, and the engine qualifies every key by the shader that
///         owns it — <c>ForwardPlusKeys.World</c> is <c>"ForwardPlus.world"</c>. So the comparison
///         strips exactly one shader name and nothing else.
///     </para>
/// </remarks>
public class MaterialReflectionTests {
    /// <summary>The material the checked-in reflection was described under.</summary>
    /// <remarks>
    ///     Kept in step with <c>LibraryReflectionTests.PublishedComposition</c> by this test failing
    ///     when it is not.
    /// </remarks>
    static MaterialDescriptor Published =>
        new() { Features = [new MetalRoughnessFeature()], Shading = new StandardShading() };

    /// <summary>The parameter names in the checked-in reflection, as the shader sees them.</summary>
    static string[] Reflected() {
        using var document = JsonDocument.Parse(File.ReadAllText(ReflectionPath()));

        return document.RootElement.GetProperty("Parameters")
            .EnumerateArray()
            .Select(parameter => parameter.GetProperty("Name").GetString()!)
            .ToArray();
    }

    /// <summary>
    ///     Every parameter the composition contributed is one the compiler predicted.
    /// </summary>
    /// <remarks>
    ///     The direction that catches a missing value: a parameter in the shader that the material
    ///     never writes takes the shader's declared default, which is a plausible-looking image
    ///     rather than an error. Only the composed ones — the pass's own uniforms are the render
    ///     features' to fill, not the material's.
    /// </remarks>
    [Fact]
    public void EveryComposedParameterInTheShaderIsOneTheCompilerWrites() {
        var material = Compile();
        var written = material.Parameters.Keys.Select(key => key.Name).ToHashSet(StringComparer.Ordinal);

        var composed = Reflected()
            .Where(name => name.StartsWith(MaterialCompiler.ChainShader + ".", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(composed);

        foreach (var name in composed) {
            Assert.Contains($"{material.ShaderName}.{name}", written);
        }
    }

    /// <summary>
    ///     Every composed parameter the compiler writes is one the shader has.
    /// </summary>
    /// <remarks>
    ///     The other direction, and the one that catches a name predicted wrongly: a value written
    ///     under a name no layout asks for is dropped in silence, so the material would look
    ///     unlit-but-plausible rather than fail.
    /// </remarks>
    [Fact]
    public void EveryComposedParameterTheCompilerWritesIsInTheShader() {
        var material = Compile();
        var reflected = Reflected().ToHashSet(StringComparer.Ordinal);
        var prefix = material.ShaderName + ".";

        var written = material.Parameters.Keys
            .Select(key => key.Name)
            .Where(name => name.StartsWith(prefix + MaterialCompiler.ChainShader, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(written);

        foreach (var name in written) {
            Assert.Contains(name[prefix.Length..], reflected);
        }
    }

    /// <summary>
    ///     The composition the compiler produces is the one the reflection was described under.
    /// </summary>
    /// <remarks>
    ///     Names are only half of it: predicting them correctly for a composition nobody compiles is
    ///     no use. This pins the other half — that a default material asks for the chain, with a
    ///     metal-roughness surface in its first slot and the standard shading model, which is exactly
    ///     what <c>LibraryReflectionTests</c> compiled.
    /// </remarks>
    [Fact]
    public void TheCompositionIsTheOneTheReflectionWasDescribedUnder() {
        var composition = Compile().Composition;

        Assert.Equal(MaterialCompiler.ChainShader, composition.Resolve("surface"));
        Assert.Equal("MetalRoughnessSurface", composition.Resolve($"{MaterialCompiler.ChainShader}.first"));
        Assert.Equal("StandardShading", composition.Resolve("shading"));
    }

    static Material Compile() {
        var compilation = MaterialCompiler.Compile(Published);

        Assert.False(
            compilation.Failed,
            string.Join("\n", compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    /// <summary>
    ///     The checked-in reflection, found by walking up to the repository root.
    /// </summary>
    /// <remarks>
    ///     Walked rather than counted, because the number of directories between a test assembly and
    ///     the root is a build-configuration detail — one that changes under <c>dotnet publish</c>,
    ///     and that a fixed <c>..\..\..\..</c> would encode into a test that then fails for a reason
    ///     having nothing to do with materials.
    /// </remarks>
    static string ReflectionPath() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", "Pipeline", "ForwardPlus.reflect.json");

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Raven/Library/Pipeline/ForwardPlus.reflect.json was not found above "
            + $"'{AppContext.BaseDirectory}'. Regenerate it with VIXEN_REGENERATE=1 in Vixen.Raven.Tests."
        );
    }
}
