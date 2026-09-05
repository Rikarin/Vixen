// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

/// <summary>
///     What an AOT probe project has to say about itself for <c>CheckAot</c> and <c>CheckAotIos</c>
///     to mean anything, read out of the project file.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>XML rather than a substring search, and that is the whole of why this type exists.</b>
///         Both checks used to ask <c>project.Contains("&lt;PublishAot&gt;true&lt;/PublishAot&gt;")</c>
///         of the file as a string, so <c>&lt;!-- &lt;PublishAot&gt;true&lt;/PublishAot&gt; --&gt;</c>
///         satisfied them exactly as the live declaration did — and commenting a property out is the
///         one edit somebody actually makes while debugging a probe. A comment is not an element, so
///         reading the file as XML makes that blindness impossible by construction rather than by a
///         rule someone has to remember (#808).
///     </para>
///     <para>
///         ⚠ <b>Only the unconditional groups count</b>, for the same reason one level along: a
///         property in a <c>&lt;PropertyGroup Condition="…"/&gt;</c> that never evaluates reads
///         identically to one that always does, and MSBuild is the only thing that knows which. What
///         is asked of each element here is therefore the question these checks can answer honestly
///         without evaluating the project — is it declared where it is unconditionally in force —
///         and a probe that grows a genuine condition is meant to fail this and be looked at.
///     </para>
///     <para>
///         Element names are matched on their local name, so a project written with the legacy
///         MSBuild namespace reads the same as an SDK-style one. Groups nested inside a
///         <c>&lt;Target&gt;</c> or a <c>&lt;Choose&gt;</c> are not top-level elements and so are
///         excluded by the same walk that excludes conditioned ones.
///     </para>
/// </remarks>
static class AotProbeContract {
    /// <summary>A property the probe must declare, and what its absence would mean.</summary>
    /// <param name="Property">The element name.</param>
    /// <param name="Value">The value it has to carry.</param>
    /// <param name="Why">What stops being checked when it is gone, worded for the failure.</param>
    internal sealed record Required(string Property, string Value, string Why);

    /// <summary>
    ///     The four properties that are the whole of what makes a probe's publish an ahead-of-time
    ///     one that reports its warnings.
    /// </summary>
    internal static IReadOnlyList<Required> AheadOfTime { get; } = [
        new("PublishAot", "true", "the publish is a framework-dependent one and ILC never runs"),
        new("TreatWarningsAsErrors", "true", "a C# warning no longer fails the publish"),
        new("ILLinkTreatWarningsAsErrors", "true", "an ILC trim or AOT warning no longer fails the publish"),
        new("TrimmerSingleWarn", "false", "ILC collapses a whole assembly's warnings into one line")
    ];

    /// <summary>
    ///     Reads a probe project, failing with the file's name rather than with an XML parser's idea
    ///     of where column 41 is.
    /// </summary>
    internal static XDocument Read(string projectXml, string name) {
        try {
            return XDocument.Parse(projectXml);
        } catch (System.Xml.XmlException exception) {
            throw new InvalidOperationException($"{name} is not well-formed XML: {exception.Message}", exception);
        }
    }

    /// <summary>
    ///     Every <see cref="AheadOfTime" /> property the project no longer declares unconditionally,
    ///     each worded as the failure it is.
    /// </summary>
    internal static IReadOnlyList<string> MissingAheadOfTimeProperties(XDocument project, string name) =>
        AheadOfTime
            .Where(entry => !Unconditional(project, "PropertyGroup", entry.Property)
                .Any(element => string.Equals(element.Value.Trim(), entry.Value, StringComparison.Ordinal))
            )
            .Select(entry => $"{name} no longer declares {entry.Property}={entry.Value}, so {entry.Why}.")
            .ToList();

    /// <summary>
    ///     The assemblies the probe references, named as the last segment of each project path.
    /// </summary>
    internal static IReadOnlyList<string> ReferencedAssemblies(XDocument project) =>
        Includes(project, "ProjectReference")
            .Select(include => include.Split('\\', '/')[^1])
            .Select(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? file[..^".csproj".Length]
                : file
            )
            .ToList();

    /// <summary>The assemblies the probe asks ILC to compile whole.</summary>
    internal static IReadOnlyList<string> RootedAssemblies(XDocument project) => Includes(project, "TrimmerRootAssembly");

    static IReadOnlyList<string> Includes(XDocument project, string item) =>
        Unconditional(project, "ItemGroup", item)
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!.Trim())
            .ToList();

    /// <summary>
    ///     Every <paramref name="element" /> in a top-level, unconditioned <paramref name="group" />
    ///     that is itself unconditioned.
    /// </summary>
    static IEnumerable<XElement> Unconditional(XDocument project, string group, string element) =>
        (project.Root?.Elements() ?? [])
        .Where(candidate => Named(candidate, group) && !Conditioned(candidate))
        .SelectMany(candidate => candidate.Elements().Where(child => Named(child, element) && !Conditioned(child)));

    static bool Named(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.Ordinal);

    static bool Conditioned(XElement element) => element.Attribute("Condition") is not null;
}
