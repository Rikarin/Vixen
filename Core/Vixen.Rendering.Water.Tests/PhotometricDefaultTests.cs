// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Vixen.Core.Mathematics;
using Vixen.Rendering.Water;
using Xunit;

namespace Tests;

/// <summary>
///     <c>Vixen.Rendering.PostFx.Tests.PhotometricDefaultTests</c>, over the assembly that had the
///     defect first.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it is here and not there.</b> That test scans <c>Vixen.Rendering</c> and
///         <c>Vixen.Rendering.PostFx</c> — the two assemblies its own project references — and water
///         was outside it, which is how <c>!Water</c> came to be the pass the rule was written for
///         and the one it did not cover. Making it cover this one from over there means the PostFx
///         tests referencing the water stack, and through it the ECS, the engine, the terrain
///         renderer and the kernel: a dependency inverted so a regex could reach one more assembly.
///     </para>
///     <para>
///         ⚠ <b>So the scan is duplicated rather than shared, and that is deliberate.</b> There is no
///         test-support assembly between these two projects to put it in, and inventing one to hold
///         forty lines of reflection would be a package in the graph for ever. What matters is that
///         the <em>sentence</em> is shared — a doc comment saying "as a radiance in cd/m²" or "as an
///         illuminance in lux" is checked wherever it is written — and a copy of the check is what
///         makes that true in an assembly the original cannot see. If a third assembly needs it, that
///         is the point to extract one.
///     </para>
///     <para>
///         What went wrong here: <c>WaterRenderer.SunColour</c> shipped <c>(1, 0.96, 0.9)</c> against
///         a sun of <c>(20683, 12745, 3774)</c> lux and <c>SkyColour</c> shipped
///         <c>(0.35, 0.45, 0.6)</c> against a sky of thousands of cd/m². Task #119 fixed the symptom
///         by adding <see cref="WaterRenderer.LightFrom" /> and having one sample call it; the
///         defaults stayed, so every host that did not call it integrated the volume correctly and
///         tonemapped it to the same black as a pass that never ran.
///     </para>
/// </remarks>
public class PhotometricDefaultTests {
    /// <summary>The sentence a member has to contain to be claiming a unit.</summary>
    /// <remarks>
    ///     ⚠ Matched against the doc comment's text with its tags dropped and its whitespace collapsed,
    ///     because these sentences are written across wrapped lines and inside <c>&lt;b&gt;</c>. Kept
    ///     character-for-character the same as the PostFx copy — two regexes that drifted would be two
    ///     rules, and an author would have no way to know which one their sentence had to satisfy.
    /// </remarks>
    static readonly Regex Photometric = new(
        @"(radiance|radiances)\s+in\s+cd/m²|(illuminance|illuminances)\s+in\s+lux",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    /// <summary>
    ///     Nothing in the water assembly that says it is a radiance or an illuminance ships with a
    ///     default inside [0, 1].
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One is not a small radiance, it is fifteen stops.</b> A clear sky is thousands of
    ///     cd/m² and a midday sun is ninety thousand lux, so a value a colour picker could produce is
    ///     not a subtle version of the effect — it is the effect switched off, in a way that leaves
    ///     every counter, every binding and every test reporting success.
    /// </remarks>
    [Fact]
    public void Nothing_documented_as_a_radiance_or_an_illuminance_defaults_to_a_tint() {
        var assembly = typeof(WaterRenderer).Assembly;
        var checkedMembers = 0;
        var failures = new List<string>();

        foreach (var name in Documented(assembly)) {
            if (Resolve(assembly, name) is not { } found) {
                continue;
            }

            var (member, value) = found;

            checkedMembers++;

            if (Math.Max(value.X, Math.Max(value.Y, value.Z)) > 1f) {
                continue;
            }

            failures.Add(
                $"{member} is documented as a photometric quantity and defaults to {value}, "
                + "which is a tint."
            );
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // ⚠ And that the scan found anything at all. A regex that stopped matching — a rephrasing, a
        // different micro sign, an assembly that stopped shipping its XML — would turn this into a
        // test that passes by looking at nothing, which is the failure mode of every lint driven off
        // text. The floor is what the node and the document contribute between them: the pass's sun
        // and sky, the underwater sky, and the three the two assets carry.
        Assert.True(
            checkedMembers >= 6,
            $"Only {checkedMembers} documented photometric default(s) were found and checked. The "
            + "phrase this scans for is what makes the unit machine-checkable, so finding none means "
            + "the scan is broken rather than that the tree is clean."
        );
    }

    /// <summary>Every member of an assembly whose doc comment claims a photometric unit.</summary>
    /// <remarks>
    ///     From the XML the compiler emits beside the assembly, which is the only place a doc comment
    ///     survives to run time. An assembly with no XML beside it yields nothing rather than throwing:
    ///     that is a build configuration, not a defect in the tree.
    /// </remarks>
    static IEnumerable<string> Documented(Assembly assembly) {
        var path = Path.ChangeExtension(assembly.Location, ".xml");

        if (!File.Exists(path)) {
            yield break;
        }

        foreach (var member in XDocument.Load(path).Descendants("member")) {
            if (member.Attribute("name")?.Value is not { } name || name[0] is not ('P' or 'F')) {
                continue;
            }

            if (Photometric.IsMatch(Regex.Replace(member.Value, @"\s+", " "))) {
                yield return name;
            }
        }
    }

    /// <summary>The declared default of a documented member, where one can be read at all.</summary>
    /// <remarks>
    ///     ⚠ Null for everything that is not a <c>Vector3</c> initialised by a parameterless
    ///     constructor, and deliberately so. A scalar exposure, a member on a type that needs a device
    ///     to exist, and a nullable overlay whose default is "unset" are all outside what a default
    ///     can be wrong about — the claim under test is about a number that reaches a shader when
    ///     nobody sets it.
    /// </remarks>
    static (string Member, Vector3 Value)? Resolve(Assembly assembly, string name) {
        var qualified = name[2..];
        var split = qualified.LastIndexOf('.');

        if (split < 0 || assembly.GetType(qualified[..split]) is not { } type) {
            return null;
        }

        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null) {
            return null;
        }

        var member = qualified[(split + 1)..];

        object? value;

        try {
            var instance = Activator.CreateInstance(type);

            value = type.GetProperty(member)?.GetValue(instance) ?? type.GetField(member)?.GetValue(instance);

            (instance as IDisposable)?.Dispose();
        } catch (Exception exception) when (exception is MissingMethodException or TargetInvocationException) {
            return null;
        }

        return value is Vector3 vector ? ($"{type.Name}.{member}", vector) : null;
    }
}
