// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Rendering.Materials;
using Xunit;

namespace Tests;

/// <summary>
///     One chain, one base workflow — and the inventory that keeps the C# predicate honest about
///     which shaders those are.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1123">#1123</a>, whose whole point is
///         that the defect compiles.</b> <c>TexturedMetalRoughnessSurface</c> and
///         <c>TexturedMaterialLayersSurface</c> are both <see cref="MaterialFeatureStage.Surface" />,
///         both <em>assign</em> the same fields of <c>MaterialData</c>, and both compose into
///         <c>CompositeSurface</c> slots. Listed together the composition resolves, the material
///         compiles, and at run time the later slot writes over the earlier one's albedo: a fully lit
///         surface of the wrong material, with nothing anywhere saying which of the two won.
///     </para>
///     <para>
///         ⚠ <b>So the negative fixture is a chain that compiled clean the day before this rule</b> —
///         <see cref="EachOfThatPairCompilesCleanOnItsOwn" /> is what shows the refusal is about the
///         <em>pair</em> rather than about either feature, which is the difference between a rule and
///         a rule satisfied by exactly the defect it was meant to catch.
///     </para>
///     <para>
///         ⚠ <b>And the predicate lives on the features rather than in a list in the compiler</b>, so
///         the thing that can rot is a <em>new</em> base workflow whose C# forgets to declare itself.
///         A list cannot see that and neither can a test that reads the same list, which is why the
///         two inventories below read the shipped <c>.rvn</c> instead.
///     </para>
/// </remarks>
public class MaterialBaseSurfaceTests {
    /// <summary>An assignment to the surface's diffuse colour, at the start of a trimmed line.</summary>
    /// <remarks>
    ///     ⚠ Assignment only, and the negative lookahead keeps <c>d.diffuseColor == …</c> out — the
    ///     comparison is the opposite of the thing. Compound forms are deliberately <em>not</em>
    ///     matched: <c>d.diffuseColor *= …</c> is a contribution, which is what a base workflow is
    ///     not.
    /// </remarks>
    static readonly Regex Assigns = new(
        @"^d\.diffuseColor\s*=(?!=)",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant
    );

    /// <summary>Any mention of the field, for telling an assignment from a read-modify-write.</summary>
    static readonly Regex Mentions = new(
        @"\bd\.diffuseColor\b",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant
    );

    /// <summary>A declaration that opens a body, of any of the three kinds the library has.</summary>
    /// <remarks>
    ///     ⚠ <c>struct</c> and <c>protocol</c> are here to <em>close</em> the previous shader rather
    ///     than to open anything — <c>MaterialFeatureOrderTests.Opens</c>' rule, and it is load
    ///     bearing for exactly the same reason: <c>MaterialBlend.Mix</c> and
    ///     <c>MaterialDefaults.Reset</c> both assign <c>d.diffuseColor</c>, both are structs, and
    ///     attributing their lines to whichever shader was declared above them would name two base
    ///     surfaces that do not exist.
    /// </remarks>
    static readonly Regex Opens = new(
        @"^(?<kind>shader|struct|protocol)\s+(?<name>\w+)",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant
    );

    /// <summary>Every <c>.rvn</c> of the shipped library, as this assembly's own build copied them.</summary>
    /// <remarks>
    ///     <c>MaterialFeatureOrderTests.Shipped</c>'s reason for refusing rather than skipping: the
    ///     shaders are copied by this project's <c>.csproj</c>, so their absence is a build that did
    ///     not happen — and a test that skipped would report success on the day it read nothing.
    /// </remarks>
    static string[] Shipped() {
        var root = Path.Combine(AppContext.BaseDirectory, "Shaders");

        Assert.True(Directory.Exists(root), $"the shipped shaders were not copied to {root}");

        return Directory.EnumerateFiles(root, "*.rvn", SearchOption.AllDirectories).ToArray();
    }

    /// <summary>Each shipped shader's body, by name, with comments dropped.</summary>
    static Dictionary<string, List<string>> Bodies() {
        var bodies = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Shipped()) {
            var shader = string.Empty;

            foreach (var raw in File.ReadAllLines(file)) {
                // ⚠ Against the RAW line, so only a declaration at column zero opens or closes
                // anything: a struct nested inside a shader body would otherwise end it, and every
                // line after it would be attributed to no shader at all.
                if (Opens.Match(raw) is { Success: true } opens) {
                    shader = opens.Groups["kind"].Value is "shader" ? opens.Groups["name"].Value : string.Empty;

                    if (shader.Length > 0) {
                        bodies.TryAdd(shader, []);
                    }

                    continue;
                }

                var line = raw.Trim();

                if (shader.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) {
                    continue;
                }

                bodies[shader].Add(line);
            }
        }

        return bodies;
    }

    /// <summary>Every shipped shader that decides what the surface is made of.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Assigning <c>d.diffuseColor</c> is not enough on its own and this is the case
    ///         that proves it.</b> <c>TexturedOrmSurface</c> assigns it too — and it is emphatically
    ///         not a base workflow: it reads the albedo back out of the surface, re-splits it by the
    ///         map's metalness and writes the result. What separates the two is the <em>read</em>. A
    ///         base workflow says what the albedo is; everything else says what to do to the albedo
    ///         that is already there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So an inventory that matched the assignment alone would call the ORM feature a
    ///         base surface and refuse every textured material in the tree</b> — a rule failing the
    ///         other way, which is the shape this repository files as "verify the instrument first".
    ///     </para>
    /// </remarks>
    static HashSet<string> BaseShaders() {
        var bases = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (shader, lines) in Bodies()) {
            var assigned = false;
            var read = false;

            foreach (var line in lines) {
                if (Assigns.IsMatch(line)) {
                    assigned = true;
                    read |= Mentions.IsMatch(line[(line.IndexOf('=') + 1)..]);

                    continue;
                }

                read |= Mentions.IsMatch(line);
            }

            if (assigned && !read) {
                bases.Add(shader);
            }
        }

        return bases;
    }

    /// <summary>Every material feature this assembly ships, one instance each.</summary>
    /// <remarks>
    ///     Reflected rather than listed, for the reason the shader side is read rather than listed:
    ///     the failure arrives as a <em>new feature</em>, and a list is satisfied by editing the list.
    /// </remarks>
    static IMaterialFeature[] Features() {
        var features = typeof(MetalRoughnessFeature).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .Where(type => typeof(IMaterialFeature).IsAssignableFrom(type))
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => (IMaterialFeature)Activator.CreateInstance(type)!)
            .ToArray();

        // The instrument: an empty roster satisfies every assertion below while checking nothing.
        Assert.True(features.Length >= 15, $"only {features.Length} material features were reflected");

        return features;
    }

    /// <summary>The chain #1118 warned about, which is the one an assembler actually produced.</summary>
    static MaterialDescriptor Layered() =>
        new() {
            Features = [
                new TexturedMaterialLayersFeature { Layers = [new(), new()] },
                new TexturedMetalRoughnessFeature()
            ]
        };

    /// <summary>Two base workflows in one chain is an error naming both of them.</summary>
    [Fact]
    public void TwoBaseSurfacesInOneChainAreRefused() {
        var compilation = MaterialCompiler.Compile(Layered());

        var refusal = Assert.Single(
            compilation.Diagnostics.Where(diagnostic => diagnostic.Id == MaterialDiagnosticId.TwoBaseSurfaces)
        );

        Assert.True(refusal.IsError, "two base surfaces draw the wrong material, so it is not a caution");
        Assert.Contains("TexturedMetalRoughnessSurface", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("TexturedMaterialLayersSurface", refusal.Message, StringComparison.Ordinal);
        Assert.Null(compilation.Material);
    }

    /// <summary>⚠ And that is the <em>only</em> thing wrong with it, which is what makes it a fixture.</summary>
    /// <remarks>
    ///     <b>A rule no material can violate is satisfied by exactly the defect it was meant to
    ///     catch.</b> If the pair above were refused for some other reason — a duplicate, a bad map
    ///     name, a slot ceiling — the new diagnostic would be decoration on a material that was
    ///     already being rejected, and removing it from the compiler would change nothing. It is not:
    ///     each feature compiles clean on its own, and together the only error is this one.
    /// </remarks>
    [Fact]
    public void EachOfThatPairCompilesCleanOnItsOwn() {
        foreach (var feature in Layered().Features) {
            var alone = MaterialCompiler.Compile(new() { Features = [feature!] });

            Assert.NotNull(alone.Material);
            Assert.Empty(alone.Diagnostics.Where(diagnostic => diagnostic.IsError));
        }

        Assert.All(
            MaterialCompiler.Compile(Layered()).Diagnostics.Where(diagnostic => diagnostic.IsError),
            diagnostic => Assert.Equal(MaterialDiagnosticId.TwoBaseSurfaces, diagnostic.Id)
        );
    }

    /// <summary>A base workflow beside every contributing feature is not what this refuses.</summary>
    /// <remarks>
    ///     ⚠ <b>The id-named negative.</b> Contributing features are the ordinary chain — a normal
    ///     map, an ORM map, an emissive, a clear coat — and every one of them <em>reads</em> the
    ///     surface a base workflow wrote. A rule that fired here would refuse every textured material
    ///     in the tree, which is the same defect facing the other way.
    /// </remarks>
    [Fact]
    public void ABaseSurfaceBesideContributingFeaturesIsNotRefused() {
        var contributing = Features()
            .Where(feature => !feature.IsBaseSurface)
            .Where(feature => feature.Stage != MaterialFeatureStage.Coordinate)
            .Take(3)
            .ToArray();

        Assert.Equal(3, contributing.Length);

        var compilation = MaterialCompiler.Compile(
            new() { Features = [new TexturedMetalRoughnessFeature(), .. contributing] }
        );

        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Id == MaterialDiagnosticId.TwoBaseSurfaces
        );
    }

    /// <summary>A shader that decides the surface has a feature that declares it does.</summary>
    /// <remarks>
    ///     The silent direction, and the one a new workflow arrives through. A feature whose shader
    ///     assigns the albedo outright and whose C# leaves
    ///     <see cref="IMaterialFeature.IsBaseSurface" /> at false is one this rule composes beside
    ///     every other base surface with nothing reported — the picture #1123 is about, reintroduced
    ///     through the fix for it.
    /// </remarks>
    [Fact]
    public void EveryShaderThatDecidesTheSurfaceHasABaseFeature() {
        var declared = Features()
            .Where(feature => feature.IsBaseSurface)
            .Select(feature => feature.ShaderName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var shader in BaseShaders()) {
            Assert.True(
                declared.Contains(shader),
                $"'{shader}' assigns d.diffuseColor without reading it — it decides what the surface "
                + "is made of — and no IMaterialFeature naming it declares IsBaseSurface, so "
                + "MaterialCompiler will compose it beside another base workflow and the later slot "
                + "will write over the earlier one's albedo"
            );
        }
    }

    /// <summary>And the other direction, which is where a rename lands.</summary>
    /// <remarks>
    ///     A feature declaring itself a base workflow for a shader that merely contributes refuses
    ///     materials for nothing — reachable by renaming the shader in the <c>.rvn</c> without
    ///     touching the C#, which is how the two halves of every name in this file drift.
    /// </remarks>
    [Fact]
    public void EveryBaseFeatureNamesAShaderThatDecidesTheSurface() {
        var bases = BaseShaders();

        foreach (var feature in Features().Where(feature => feature.IsBaseSurface)) {
            Assert.True(
                bases.Contains(feature.ShaderName),
                $"'{feature.GetType().Name}' declares IsBaseSurface and '{feature.ShaderName}' does "
                + "not assign d.diffuseColor outright in the shipped library — so it refuses chains "
                + "that would have drawn correctly"
            );
        }
    }

    /// <summary>⚠ The read is what separates a base workflow from a feature that re-splits the albedo.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The apparatus rather than the rule, and it is the half that would have been
    ///         wrong.</b> <c>TexturedOrmSurface</c> assigns <c>d.diffuseColor</c> — an inventory
    ///         matching the assignment alone would call it a base workflow, and
    ///         <see cref="EveryShaderThatDecidesTheSurfaceHasABaseFeature" /> would then demand a
    ///         declaration that would refuse every baked material in the tree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it breaks to silent green.</b> Ask what the two inventories print on the day
    ///         <see cref="Assigns" /> stops matching anything: success, both of them, over an empty
    ///         set. So this reads the discriminating file by name and asserts both sides of the
    ///         classification rather than a count.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AFeatureThatReadsTheAlbedoBackIsNotABaseSurface() {
        var bodies = Bodies();

        Assert.True(
            bodies.TryGetValue("TexturedOrmSurface", out var orm),
            "the shipped library no longer declares TexturedOrmSurface, so this discriminates nothing"
        );

        Assert.Contains(orm!, line => Assigns.IsMatch(line));
        Assert.DoesNotContain(BaseShaders(), shader => shader == "TexturedOrmSurface");

        // The other side of the same classification: the five that do decide the surface are there,
        // so the exclusion above is a judgement rather than an empty set.
        Assert.Equal(5, BaseShaders().Count);
    }
}
