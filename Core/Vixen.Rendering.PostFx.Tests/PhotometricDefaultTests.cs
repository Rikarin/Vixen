// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Tests;

/// <summary>
///     The one shape three separate investigations have had: a photometric quantity authored as a tint.
/// </summary>
/// <remarks>
///     <para>
///         <b>What went wrong three times in two days.</b> <c>!Water</c>'s <c>sunColour</c> was
///         <c>1.0 0.72 0.42</c> against a sun of <c>(20683, 12745, 3774)</c>; <c>!VolumetricFog</c>'s
///         was <c>(1, 0.9, 0.7)</c> used as an illuminance against a scene lit at 90 000 lux; and
///         <c>!Fog</c>'s two lerp targets were <c>(0.5, 0.6, 0.7)</c> and <c>(1, 0.9, 0.7)</c> in a
///         frame whose radiance is cd/m². Each pass was <em>correct</em> — the integration, the
///         slicing, the falloff, the bindings — and each was arithmetically indistinguishable from a
///         pass that never ran. Nothing failed. There is no picture of the bug.
///     </para>
///     <para>
///         ⚠ <b>Why this is a test and not a lint.</b> A tint and a radiance are both a
///         <c>Vector3</c> in C# and both a <c>float3</c> in Raven, and the two languages carry no
///         units — so no analyser can tell <c>VignetteRenderer.VignetteColour</c>, which is genuinely
///         a colour, from <c>FogRenderer.Colour</c>, which is genuinely a luminance. Every purely
///         syntactic rule that catches the second flags the first, and a rule with an exemption list
///         is the list, not the rule.
///     </para>
///     <para>
///         What <em>is</em> machine-checkable is the sentence the author wrote. Both fixes ended up
///         saying "as a radiance in cd/m²" or "as an illuminance in lux" in the doc comment, because
///         that is the only place the unit can be recorded — so this makes that sentence load-bearing:
///         say it, and the value you shipped is checked against it. It cannot catch a member nobody
///         documented, and it is not claimed to. It catches the regression, and it makes the phrase
///         worth typing.
///     </para>
/// </remarks>
public class PhotometricDefaultTests {
    /// <summary>The sentence a member has to contain to be claiming a unit.</summary>
    /// <remarks>
    ///     ⚠ Matched against the doc comment's text with its tags dropped and its whitespace collapsed,
    ///     because these sentences are written across wrapped lines and inside <c>&lt;b&gt;</c>.
    /// </remarks>
    static readonly Regex Photometric = new(
        @"(radiance|radiances)\s+in\s+cd/m²|(illuminance|illuminances)\s+in\s+lux",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    /// <summary>
    ///     Nothing that says it is a radiance or an illuminance ships with a default inside [0, 1].
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One is not a small radiance, it is fifteen stops.</b> A clear sky is thousands of
    ///     cd/m² and a midday sun is ninety thousand lux, so a value a colour picker could produce is
    ///     not a subtle version of the effect — it is the effect switched off, in a way that leaves
    ///     every counter, every binding and every test reporting success.
    /// </remarks>
    [Fact]
    public void Nothing_documented_as_a_radiance_or_an_illuminance_defaults_to_a_tint() {
        var checkedMembers = 0;
        var failures = new List<string>();

        foreach (var assembly in new[] { typeof(FogRenderer).Assembly, typeof(RenderLight).Assembly }) {
            foreach (var (name, _) in Documented(assembly)) {
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
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // ⚠ And that the scan found anything at all. A regex that stopped matching — a rephrasing, a
        // different micro sign, an assembly that stopped shipping its XML — would turn this into a
        // test that passes by looking at nothing, which is the failure mode of every lint driven off
        // text. The floor is the count the three fixed passes contribute between them.
        Assert.True(
            checkedMembers >= 4,
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
    static IEnumerable<(string Name, string Text)> Documented(Assembly assembly) {
        var path = Path.ChangeExtension(assembly.Location, ".xml");

        if (!File.Exists(path)) {
            yield break;
        }

        foreach (var member in XDocument.Load(path).Descendants("member")) {
            if (member.Attribute("name")?.Value is not { } name || name[0] is not ('P' or 'F')) {
                continue;
            }

            var text = Regex.Replace(member.Value, @"\s+", " ");

            if (Photometric.IsMatch(text)) {
                yield return (name, text);
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
        } catch (Exception exception) when (exception is MissingMethodException or TargetInvocationException) {
            return null;
        }

        return value is Vector3 vector ? ($"{type.Name}.{member}", vector) : null;
    }
}
