// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Editor.Assets.Textures;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The gate for the one defect this repository has now shipped three times: an importer that
///     declares <c>[Importer]</c> and is absent from <see cref="BuiltInImporters.Create()" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>The shape of it.</b> <c>[Importer]</c> is a declaration nothing scans for — deliberately,
///         for the reason <see cref="ImporterRegistry" /> gives — and <c>BuiltInImporters.Create()</c>
///         is a hand-written list. Nothing joined the two, so writing an importer, attributing it,
///         documenting it and testing it left it unreachable, and the only symptom was
///         <c>RawImporter</c> quietly claiming the extension: a byte blob under the type name
///         <c>"Blob"</c>, no diagnostic anywhere, and a runtime reader that finds nothing it can
///         resolve.
///     </para>
///     <para>
///         ⚠ <b>Three times, and the second and third happened after the warning was written.</b>
///         <c>.vxwaves</c> was the first and left a comment in <c>BuiltInImporters.cs</c> saying
///         exactly what to watch for; <c>.cube</c> and the four AI formats (<c>.vxbt</c>,
///         <c>.vxgoap</c>, <c>.vxquery</c>, <c>.vxutility</c>) went in afterwards and every one of
///         them was missed. A comment is read by whoever is already looking at the file, which is
///         never the person adding a class three directories away. This is the same statement made
///         where it fails a build.
///     </para>
///     <para>
///         ⚠ <b>A test over the constructed registry rather than a <c>CheckArchitecture</c> rule, and
///         the difference is not stylistic.</b> <c>CheckArchitecture</c> runs without compiling
///         anything — it parses <c>.csproj</c> XML — so a rule there could only pattern-match the
///         source text of <c>Create()</c>, which asserts what the method <em>appears</em> to say. This
///         asserts what <c>TryGetForFile</c> actually returns, which is the property that was
///         violated. The distinction has teeth: a chain entry written as a factory call, a
///         registration inside a conditional, or an extension shadowed by another importer's list all
///         pass a text rule and fail here.
///     </para>
///     <para>
///         ⚠ <b>Reflection here is not the assembly scan <see cref="ImporterRegistry" /> refuses.</b>
///         That refusal is about the shipped product: a trimmed publish has deleted the metadata, and
///         a scan makes "which importers does this build have" depend on which assemblies happen to
///         be loaded. A test assembly is neither trimmed nor shipped, and it is asking a different
///         question — not "what should the registry contain" but "does the hand-written list still
///         agree with what was written". The registry stays told; the list stays checked.
///     </para>
/// </remarks>
public sealed class EveryAttributedImporterTests {
    /// <summary>
    ///     ⚠ <b>Scoped to the assembly that defines <see cref="BuiltInImporters" />, and the scope is
    ///     the design decision.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An <c>[Importer]</c> in a project script or a plugin is <em>supposed</em> to register by
    ///         attribute — <c>EditorScripts</c> scans a game author's compiled scripts and folds them in
    ///         through <c>ImporterContributions</c>, which is the whole contract of a contributed
    ///         importer. Requiring those in a hand-written list would be requiring the engine to know
    ///         about a game's formats.
    ///     </para>
    ///     <para>
    ///         The test fakes are the other half of the same argument: <c>RivalTextureImporter</c> in
    ///         <c>ImporterContributionTests</c> and the <c>.pal</c> pair in <c>ImporterTests</c> and
    ///         <c>ImporterRegistryTests</c> exist to be refused or to be contributed, and a rule that
    ///         demanded them in the built-in list would be a rule the suite testing the rule violates.
    ///     </para>
    ///     <para>
    ///         Both fall out of one line rather than a marker attribute or an opt-out list. The
    ///         built-ins are exactly the importers that ship in the assembly the list lives in; project
    ///         scripts compile into the game's own assembly and the fakes into the test assembly. An
    ///         opt-out list would be a second hand-maintained list, which is the thing that failed.
    ///     </para>
    /// </remarks>
    static readonly Assembly BuiltIn = typeof(BuiltInImporters).Assembly;

    /// <summary>The gate.</summary>
    /// <remarks>
    ///     ⚠ <b>Compared by type and not by name.</b> An importer's <c>Name</c> comes from its settings
    ///     type's <c>[DataContract]</c> through <c>TypeRegistry</c>, so reading it needs an instance and
    ///     a populated registry — and if two importers ever shared a name, <c>ImporterRegistry.Add</c>
    ///     would already have thrown. The type is the identity that cannot drift.
    /// </remarks>
    [Fact]
    public void EveryImporterInThisAssemblyIsRegistered() {
        // ⚠ A contribution set of its own, not ImporterContributions.Default: the default is
        // process-wide and ImporterContributionTests mutates it, so reading it here would race.
        var missing = Unregistered(BuiltInImporters.Create(new ImporterContributions()));

        Assert.True(
            missing.Count == 0,
            $"{string.Join(", ", missing.Select(type => type.Name))} "
            + $"{(missing.Count == 1 ? "carries [Importer] and is" : "carry [Importer] and are")} absent from "
            + $"BuiltInImporters.Create(), so the extensions {(missing.Count == 1 ? "it claims" : "they claim")} fall "
            + "through to RawImporter and become byte blobs no runtime reader resolves — with no error anywhere. Add "
            + $"{(missing.Count == 1 ? "it" : "them")} to the list."
        );
    }

    /// <summary>
    ///     And that each claimed extension reaches <em>that</em> importer, rather than merely reaching
    ///     one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The second half of the failure, which membership alone does not cover.</b> An importer
    ///     can be in the list and still never see its files, if another importer's extension list
    ///     grew to overlap — <c>ImporterRegistry.Add</c> refuses that with both names, so this is the
    ///     assertion that the refusal is what happens rather than something subtler. It also states
    ///     the property an author actually depends on: open a <c>.cube</c>, get the LUT importer.
    /// </remarks>
    [Fact]
    public void EveryClaimedExtensionReachesItsOwnImporter() {
        var registry = BuiltInImporters.Create(new ImporterContributions());

        foreach (var type in Attributed(BuiltIn)) {
            foreach (var extension in Claimed(type)) {
                Assert.True(
                    registry.TryGetForFile("Assets/anything" + extension, out var importer),
                    $"Nothing at all claims '{extension}', which {type.Name} declares."
                );

                Assert.True(
                    importer.GetType() == type,
                    $"'{extension}' is declared by {type.Name} and the registry hands it to "
                    + $"{importer.GetType().Name}."
                );
            }
        }
    }

    /// <summary>
    ///     ⚠ The gate, sabotaged on purpose, so that "it passes" keeps meaning something.
    /// </summary>
    /// <remarks>
    ///     A rule whose subject is "a list is complete" is green on the day it is written and green
    ///     forever if the rule is wrong — there is no failing case in the tree to calibrate it
    ///     against once the list is fixed. So one is built here: a registry holding a single importer,
    ///     which every other attributed type is missing from. If <see cref="Unregistered" /> ever
    ///     stops seeing them — an empty reflection query, a filter that excludes everything — this
    ///     goes red and the gate above does not.
    /// </remarks>
    [Fact]
    public void TheGateSeesAnImporterThatIsNotInTheList() {
        var crippled = new ImporterRegistry().Add(new TextureImporter()).AddFallback(new RawImporter());

        var missing = Unregistered(crippled);

        Assert.Contains(typeof(CubeLutImporter), missing);
        Assert.Contains(typeof(Ai.BehaviorTreeImporter), missing);
        Assert.DoesNotContain(typeof(TextureImporter), missing);
        Assert.DoesNotContain(typeof(RawImporter), missing);
    }

    /// <summary>Which attributed importers a registry does not hold.</summary>
    /// <param name="registry">The registry.</param>
    /// <returns>The types that declare <c>[Importer]</c> and are not in it.</returns>
    static IReadOnlyList<Type> Unregistered(ImporterRegistry registry) {
        var held = registry.Importers.Select(importer => importer.GetType()).ToHashSet();

        return [.. Attributed(BuiltIn).Where(type => !held.Contains(type))];
    }

    /// <summary>Every concrete importer in an assembly that declares which files it claims.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <returns>The types.</returns>
    static IEnumerable<Type> Attributed(Assembly assembly) =>
        assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(type => typeof(IAssetImporter).IsAssignableFrom(type))
            .Where(type => Attribute.IsDefined(type, typeof(ImporterAttribute)))
            .OrderBy(type => type.Name, StringComparer.Ordinal);

    /// <summary>The extensions a type's attribute claims, read without constructing it.</summary>
    /// <param name="type">The importer type.</param>
    /// <returns>The extensions.</returns>
    static IReadOnlyList<string> Claimed(Type type) =>
        ((ImporterAttribute) Attribute.GetCustomAttribute(type, typeof(ImporterAttribute))!).Extensions;
}
