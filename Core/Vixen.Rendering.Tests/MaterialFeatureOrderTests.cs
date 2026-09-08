// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Rendering.Materials;
using Xunit;

namespace Tests;

/// <summary>
///     The one ordering the material chain has an opinion about, and the inventory that keeps the
///     two halves of it saying the same thing.
/// </summary>
/// <remarks>
///     <para>
///         <b>The failure this exists ahead of is a plausible frame.</b> Every feature in the library
///         reads <c>d.uv</c> and writes a channel, so the order they run in is the author's business.
///         A feature that reads <c>d.uv</c> and <em>writes</em> it — parallax occlusion,
///         <a href="https://github.com/Rikarin/Vixen/issues/1065">#1065</a> — inverts that: composed
///         into the third slot it displaces the coordinate the two features before it already sampled
///         at, and half the material is parallaxed. No device reports it and no counter moves.
///     </para>
///     <para>
///         ⚠ <b>Written before the feature, which is the only moment it is cheap.</b> The library has
///         no coordinate-writing shader today, so the two inventories below are green over an empty
///         set — and that is exactly the state in which they are worth having, because the thing they
///         guard against arrives as a <em>new shader</em>. What is <em>not</em> allowed is for them to
///         be green because they stopped reading anything, which is what
///         <see cref="TheCoordinateWriteInventoryStillMatchesTheLibrary" /> is for.
///     </para>
/// </remarks>
public class MaterialFeatureOrderTests {
    /// <summary>A write to the surface coordinate, as a feature's body would spell one.</summary>
    /// <remarks>
    ///     ⚠ Assignment only — <c>=</c> and the compound forms — and anchored at the start of a
    ///     trimmed line, so <c>d.uv</c> read as an argument is not a write. The negative lookahead is
    ///     what keeps <c>d.uv == …</c> out, which is a comparison and the opposite of the thing.
    /// </remarks>
    static readonly Regex Write = new(
        @"^d\.uv\s*[-+*/]?=(?!=)",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant
    );

    /// <summary>A declaration that opens a body, of any of the three kinds the library has.</summary>
    /// <remarks>
    ///     ⚠ <c>struct</c> and <c>protocol</c> are here to <em>close</em> the previous shader rather
    ///     than to open anything. <c>MaterialDefaults.Begin</c> and <c>MaterialBlend.Mix</c> both
    ///     assign <c>d.uv</c>, they are structs, and attributing their lines to whichever shader was
    ///     declared above them would report two coordinate features that do not exist — a refutation
    ///     arrived at by not reading the file carefully, which is the failure mode this repository
    ///     files wrong issues from.
    /// </remarks>
    static readonly Regex Opens = new(
        @"^(?<kind>shader|struct|protocol)\s+(?<name>\w+)",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant
    );

    /// <summary>Every <c>.rvn</c> of the shipped library, as this assembly's own build copied them.</summary>
    /// <remarks>
    ///     Not a skip when they are missing, on <c>ComposeSlotInventoryTests</c>' terms: the shaders
    ///     are copied by this project's own <c>.csproj</c>, so their absence is a build that did not
    ///     happen rather than an environment this cannot run in — and a test that skipped would report
    ///     success on the day it stopped reading anything at all.
    /// </remarks>
    static string[] Shipped() {
        var root = Path.Combine(AppContext.BaseDirectory, "Shaders");

        Assert.True(Directory.Exists(root), $"the shipped shaders were not copied to {root}");

        return Directory.EnumerateFiles(root, "*.rvn", SearchOption.AllDirectories).ToArray();
    }

    /// <summary>Every shader in the shipped library whose body writes the surface coordinate.</summary>
    static HashSet<string> CoordinateShaders() {
        var writers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Shipped()) {
            var shader = string.Empty;

            foreach (var raw in File.ReadAllLines(file)) {
                var line = raw.Trim();

                // ⚠ Matched against the RAW line, so only a declaration at column zero opens or
                // closes anything. Against the trimmed line a *nested* struct closed its own
                // shader — `Terrain/ImpostorCapture.rvn` declares `struct Captured` four spaces in,
                // inside `shader ImpostorCapture`, so every line after it was attributed to no
                // shader at all. The sweep would have gone on passing while reading less and less
                // of the library, which is exactly what the remark above says must not happen.
                if (Opens.Match(raw) is { Success: true } opens) {
                    // Reset on a struct or a protocol, which is the half that matters: only a shader
                    // can be a feature, and a body that is not one has to end the shader above it.
                    shader = opens.Groups["kind"].Value is "shader" ? opens.Groups["name"].Value : string.Empty;
                    continue;
                }

                if (shader.Length > 0 && Write.IsMatch(line)) {
                    writers.Add(shader);
                }
            }
        }

        return writers;
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

        // The instrument: an empty roster is a reflection query that stopped matching, and it
        // satisfies every assertion below it while checking nothing. Fifteen rather than one, because
        // a query that found a single feature has also stopped working.
        Assert.True(features.Length >= 15, $"only {features.Length} material features were reflected");

        return features;
    }

    /// <summary>
    ///     A shader that writes the coordinate has a feature that declares it does.
    /// </summary>
    /// <remarks>
    ///     The silent direction. A feature whose shader displaces <c>d.uv</c> and whose C# says
    ///     <see cref="MaterialFeatureStage.Surface" /> is one the compiler will happily compose into
    ///     the third slot, which is the half-parallaxed surface this whole file is about.
    /// </remarks>
    [Fact]
    public void EveryShaderThatWritesTheCoordinateHasACoordinateFeature() {
        var declared = Features()
            .Where(feature => feature.Stage == MaterialFeatureStage.Coordinate)
            .Select(feature => feature.ShaderName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var shader in CoordinateShaders()) {
            Assert.True(
                declared.Contains(shader),
                $"'{shader}' writes d.uv and no IMaterialFeature naming it declares "
                + "MaterialFeatureStage.Coordinate — so MaterialCompiler will compose it wherever the "
                + "material's list happens to put it, and every feature ahead of it has already "
                + "sampled at the coordinate it replaces"
            );
        }
    }

    /// <summary>And the other direction, which is where a rename lands.</summary>
    /// <remarks>
    ///     A feature declaring <see cref="MaterialFeatureStage.Coordinate" /> for a shader that writes
    ///     no coordinate refuses materials for nothing — the same rule failing the other way, and
    ///     reachable by renaming the shader in the <c>.rvn</c> without touching the C#.
    /// </remarks>
    [Fact]
    public void EveryCoordinateFeatureNamesAShaderThatWritesTheCoordinate() {
        var writers = CoordinateShaders();

        foreach (var feature in Features().Where(feature => feature.Stage == MaterialFeatureStage.Coordinate)) {
            Assert.True(
                writers.Contains(feature.ShaderName),
                $"'{feature.GetType().Name}' declares MaterialFeatureStage.Coordinate and "
                + $"'{feature.ShaderName}' writes no coordinate in the shipped library — so it "
                + "constrains where a material may put it and buys nothing for the constraint"
            );
        }
    }

    /// <summary>
    ///     And the pattern above still matches the library, on the day nothing is a writer.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both inventories above are green over an empty set today, so this is what makes them
    ///     evidence.</b> Ask what they print on the day the regex stops matching: success, twice, for
    ///     the same reason they print success now. The library assigns <c>d.uv</c> in three places —
    ///     <c>MaterialDefaults.Reset</c>, <c>MaterialDefaults.Begin</c> and <c>MaterialBlend.Mix</c> —
    ///     and every one of them is inside a <c>struct</c>, which is why the set of shader writers is
    ///     empty and the set of raw writes is not.
    /// </remarks>
    [Fact]
    public void TheCoordinateWriteInventoryStillMatchesTheLibrary() {
        var writes = Shipped()
            .SelectMany(File.ReadAllLines)
            .Count(line => Write.IsMatch(line.Trim()));

        Assert.True(writes >= 3, $"the coordinate-write pattern matched {writes} lines in the library");
    }

    /// <summary>⚠ A struct nested inside a shader does not end it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The apparatus, not the pattern — and it was the half that was already wrong.</b>
    ///         <c>Opens</c> was matched against the <em>trimmed</em> line, so a <c>struct</c>
    ///         declared inside a shader body closed its own shader and every line after it was
    ///         attributed to nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A floor on how much the walk reads cannot see this and I tried one first</b>: the
    ///         difference is 33 lines out of 13 278, and the shader keeps the lines <em>above</em> its
    ///         nested struct so it stays in any set-of-shaders count. Both numbers are also
    ///         maintenance debt that rots on the next library edit. What discriminates is the one
    ///         file that has the shape — so this reads it by name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it breaks to silent green, which is why it is worth a case at all.</b> A walk
    ///         attributing nothing finds no coordinate writers, and "no shader writes the coordinate"
    ///         is what the library says today — so both inventories above would pass while reading
    ///         less and less.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AStructInsideAShaderDoesNotEndIt() {
        var file = Shipped()
            .SingleOrDefault(one => Path.GetFileName(one) == "ImpostorCapture.rvn");

        Assert.True(
            file is not null,
            "Terrain/ImpostorCapture.rvn is the library's one shader carrying a nested struct, and "
            + "it is what makes this case a claim. If it has moved, find another and say so here."
        );

        var lines = File.ReadAllLines(file!);
        var nested = Array.FindIndex(lines, line => line.StartsWith("    struct ", StringComparison.Ordinal));

        Assert.True(nested > 0, "ImpostorCapture.rvn no longer declares an indented struct.");

        var shader = string.Empty;
        var after = 0;

        for (var index = 0; index < lines.Length; index++) {
            if (Opens.Match(lines[index]) is { Success: true } opens) {
                shader = opens.Groups["kind"].Value is "shader" ? opens.Groups["name"].Value : string.Empty;
                continue;
            }

            if (index > nested && shader.Length > 0 && lines[index].Trim().Length > 0) {
                after++;
            }
        }

        // ⚠ Under the trimmed match this is exactly zero: the nested struct cleared the shader and
        // nothing in the rest of the file belonged to one.
        Assert.True(
            after > 0,
            "every line after the nested struct was attributed to no shader, so the walk stops "
            + "reading a shader body the moment one declares a struct inside itself."
        );
    }

    /// <summary>A feature whose shader displaces the coordinate the rest of the chain samples at.</summary>
    /// <remarks>
    ///     Local to this file because the library has no such shader yet — which is the point. It is
    ///     the shape <a href="https://github.com/Rikarin/Vixen/issues/1065">#1065</a> will take, and
    ///     the rule is tested against it rather than against a feature that has already shipped
    ///     wrongly.
    /// </remarks>
    sealed record DisplaceFeature : IMaterialFeature {
        public string ShaderName => "ParallaxSurface";

        public MaterialFeatureStage Stage => MaterialFeatureStage.Coordinate;

        public void Compile(MaterialCompilationContext context) => context.Set("scale", 0.05f);
    }

    /// <summary>A coordinate feature behind a feature that samples is refused.</summary>
    /// <remarks>
    ///     Refused rather than moved: the composition would still compile, and what it compiles to is
    ///     a surface displaced from slot three onwards. The message has to name both features, because
    ///     "this is in the wrong place" is only actionable beside what it is in the wrong place
    ///     relative to.
    /// </remarks>
    [Fact]
    public void ACoordinateFeatureBehindASamplingFeatureIsRejected() {
        var compilation = MaterialCompiler.Compile(
            new() { Features = [new TexturedMetalRoughnessFeature(), new DisplaceFeature()] }
        );

        Assert.True(compilation.Failed);

        var diagnostic = Assert.Single(compilation.Errors);

        Assert.Equal(MaterialDiagnosticId.CoordinateFeatureOutOfOrder, diagnostic.Id);
        Assert.Contains("ParallaxSurface", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("TexturedMetalRoughnessSurface", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>And in front of them it compiles, into the first slot the chain calls.</summary>
    /// <remarks>
    ///     The half that shows the rule is an ordering rule and not a ban: the same two features in
    ///     the other order are a material, and the coordinate feature is what
    ///     <c>CompositeSurface.first</c> holds — which is the slot that runs before the one that
    ///     samples.
    /// </remarks>
    [Fact]
    public void ACoordinateFeatureInFrontOfTheChainCompiles() {
        var compilation = MaterialCompiler.Compile(
            new() { Features = [new DisplaceFeature(), new TexturedMetalRoughnessFeature()] }
        );

        Assert.False(compilation.Failed);
        Assert.Empty(compilation.Errors);

        Assert.Equal("ParallaxSurface", compilation.Material!.Composition.Resolve("CompositeSurface.first"));
    }

    /// <summary>
    ///     Two of them in front of the chain is allowed, which is what "before the samplers" means.
    /// </summary>
    /// <remarks>
    ///     ⚠ The rule is not "slot zero". A tiling transform and a parallax march are two coordinate
    ///     writers that compose — the second displaces what the first tiled — and a rule written as an
    ///     index would have refused the pair while permitting nothing safer.
    /// </remarks>
    [Fact]
    public void TwoCoordinateFeaturesInFrontOfTheChainCompile() {
        var compilation = MaterialCompiler.Compile(
            new() { Features = [new DisplaceFeature(), new TileFeature(), new TexturedMetalRoughnessFeature()] }
        );

        Assert.False(compilation.Failed);
        Assert.Empty(compilation.Errors);
    }

    /// <summary>A second coordinate feature, so the pair can be composed at all.</summary>
    /// <remarks>
    ///     A distinct shader name rather than a second <see cref="DisplaceFeature" />, because two
    ///     slots holding one shader is the duplicate-feature refusal and would prove the wrong thing.
    /// </remarks>
    sealed record TileFeature : IMaterialFeature {
        public string ShaderName => "UvTileSurface";

        public MaterialFeatureStage Stage => MaterialFeatureStage.Coordinate;

        public void Compile(MaterialCompilationContext context) => context.Set("tiling", 2f);
    }

    /// <summary>And a chain of surface features alone is never refused for order.</summary>
    /// <remarks>
    ///     The false-positive half. Every shipped material is this shape, so a rule that could fire on
    ///     one would have broken every material in the project — the failure
    ///     <c>MaterialCompiler.OptionalSlots</c> records happening twice, arrived at from the other
    ///     side.
    /// </remarks>
    [Fact]
    public void AChainOfSurfaceFeaturesIsNotRefused() {
        var compilation = MaterialCompiler.Compile(
            new() {
                Features = [
                    new TexturedMetalRoughnessFeature(),
                    new TexturedNormalMapFeature(),
                    new TexturedOrmFeature(),
                    new EmissiveFeature()
                ]
            }
        );

        Assert.False(compilation.Failed);
        Assert.Empty(compilation.Errors);
    }
}
