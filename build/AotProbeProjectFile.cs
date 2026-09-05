// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

/// <summary>
///     What an AOT probe's project file declares, read as XML rather than as a string.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The two checks over these files used to be substring tests, and a substring test
///         cannot see an XML comment.</b> <c>&lt;!-- &lt;PublishAot&gt;true&lt;/PublishAot&gt; --&gt;</c>
///         satisfied <c>project.Contains("&lt;PublishAot&gt;true&lt;/PublishAot&gt;")</c> exactly as
///         the live declaration did — and commenting a property out while debugging a probe is the
///         one edit somebody actually makes. On iOS that is the whole enforcement: <c>CheckAotIos</c>
///         asserts nothing about its output (#634), so these four properties are all it has.
///     </para>
///     <para>
///         The same blindness reached the rooting comparison: a commented-out
///         <c>TrimmerRootAssembly</c> counted as a root MSBuild does not have, so an assembly
///         covered by nothing but what <c>Main</c> reaches read as fully rooted.
///     </para>
///     <para>
///         ⚠ And a third case neither a substring test nor a naive XML read catches: a property
///         inside a <c>PropertyGroup</c> with a <c>Condition</c> that never evaluates looks
///         identical to one in the unconditional group. Everything here therefore ignores an
///         element under any conditioned group, and ignores a conditioned element — MSBuild might
///         still apply it, so this is the conservative direction: the check can only ever under-count
///         what is declared, and under-counting fails the gate rather than passing it.
///     </para>
///     <para>
///         Element names are matched by local name so that a project written with the legacy
///         MSBuild XML namespace reads the same as one without it. Both probes are written without.
///     </para>
/// </remarks>
static class AotProbeProjectFile {
    /// <summary>
    ///     The assembly names of the probe's unconditional <c>ProjectReference</c> items, taken from
    ///     the last segment of each path.
    /// </summary>
    public static IReadOnlyList<string> ReferencedAssemblies(string path) =>
        UnconditionalItems(path, "ProjectReference")
            .Select(include => include.Split('\\', '/')[^1])
            .Select(file => file.EndsWith(".csproj", StringComparison.Ordinal) ? file[..^".csproj".Length] : file)
            .ToList();

    /// <summary>The assembly names of the probe's unconditional <c>TrimmerRootAssembly</c> items.</summary>
    public static IReadOnlyList<string> RootedAssemblies(string path) =>
        UnconditionalItems(path, "TrimmerRootAssembly");

    /// <summary>
    ///     Whether the project declares the property with that value, unconditionally and outside
    ///     any comment.
    /// </summary>
    public static bool DeclaresProperty(string path, string property, string value) =>
        Unconditional(XDocument.Load(path).Root, property)
            .Any(element => string.Equals(element.Value.Trim(), value, StringComparison.Ordinal));

    static IReadOnlyList<string> UnconditionalItems(string path, string item) =>
        Unconditional(XDocument.Load(path).Root, item)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrEmpty(include))
            .Select(include => include!)
            .ToList();

    /// <summary>
    ///     Every element with that local name whose whole chain of ancestors — and which itself —
    ///     carries no <c>Condition</c>.
    /// </summary>
    static IEnumerable<XElement> Unconditional(XElement? root, string localName) =>
        root is null
            ? []
            : root.Descendants()
                .Where(element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))
                .Where(
                    element => element.AncestorsAndSelf()
                        .All(ancestor => ancestor.Attribute("Condition") is null)
                );
}
