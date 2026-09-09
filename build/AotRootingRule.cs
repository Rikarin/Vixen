// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
///     Which runtime assemblies <c>CheckAot</c> actually asks its question about, as a function of
///     the project files rather than of a paragraph in a README.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gate covers 29 of 95 runtime assemblies and its README said it covered them
///         all</b> (<a href="https://github.com/Rikarin/Vixen/issues/506">#506</a>). The README is
///         corrected, and a corrected paragraph is a measurement somebody took once — the thing this
///         file adds is that the measurement now fails when it stops being true. A new assembly under
///         <c>Core/</c> or <c>Platform/</c> joins the uncovered set silently today; after this it
///         has to be rooted or written down.
///     </para>
///     <para>
///         ⚠ <b>This is not the check <c>AssertProbeRootsEveryAssemblyItReferences</c> already
///         makes, and the difference is the whole issue.</b> That one compares the probe against
///         <em>itself</em> — every <c>ProjectReference</c> has a <c>TrimmerRootAssembly</c> — so a
///         probe referencing three assemblies and rooting the same three passes it. This compares
///         the probe against the <em>tree</em>, which is the only comparison that can notice an
///         assembly nobody added.
///     </para>
///     <para>
///         <b>Rooted and merely present are different things.</b> A framework-dependent publish of
///         the probe writes 51 managed assemblies, so ILC does compile whatever <c>Main</c> reaches
///         inside many of the 66 — and reading the publish output as coverage is exactly the mistake
///         the probe's README warns about. Only a <c>TrimmerRootAssembly</c> asks "is this assembly
///         publishable ahead of time" rather than "is one path through it clean", so only roots count
///         here.
///     </para>
///     <para>
///         ⚠ <b>Expansion is real work rather than a list edit, which is why the ledger is a list of
///         admissions and not a list of exclusions.</b> <c>ILLinkTreatWarningsAsErrors</c> makes each
///         newly rooted assembly a fresh set of IL2xxx/IL3xxx findings and a build break until every
///         one is fixed. The list can only shrink, so the expansion has a scoreboard; nothing here
///         requires it to shrink today.
///     </para>
///     <para>
///         <b>A pure function so that its answer can be read without running the gate</b> — which
///         matters more here than anywhere else in <c>build/</c>, because the gate this belongs to is
///         an ILC publish measured in minutes and forbidden on a loaded machine. <c>CheckAot</c> is
///         one caller and <c>AotRootingRuleTests</c> is the other.
///     </para>
/// </remarks>
static class AotRootingRule {
    /// <summary>The ledger of runtime assemblies the probe does not root.</summary>
    public const string LedgerFile = "Tools/Vixen.AotProbe/NotRooted.txt";

    /// <summary>The two folders whose non-test <c>net10.0</c> projects a shipped game links.</summary>
    /// <remarks>
    ///     ⚠ <c>Gameplay/</c> is in the RUNTIME profile too and is deliberately not here: #506
    ///     measured <c>Core/</c> and <c>Platform/</c>, and widening the subject set while adding the
    ///     rule would change the number the issue is about in the same commit that gates it. A
    ///     later batch can add it and read the diff.
    /// </remarks>
    public static readonly string[] Layers = ["Core", "Platform"];

    /// <summary>
    ///     The one target framework that means "this ships inside a desktop game and the desktop
    ///     probe can root it".
    /// </summary>
    /// <remarks>
    ///     ⚠ Generators are <c>netstandard2.1</c> and the <c>-ios</c>, <c>-android</c> and
    ///     <c>-browser</c> heads are their own publishes — <c>CheckAotIos</c> is the gate over the
    ///     iOS one — so a plain string comparison is what keeps seventeen projects that this probe
    ///     could not root if it wanted to out of the count.
    /// </remarks>
    public const string RuntimeFramework = "net10.0";

    static readonly Regex Framework = new(
        @"<TargetFrameworks?>([^<]*)</TargetFrameworks?>",
        RegexOptions.CultureInvariant
    );

    /// <summary>Every runtime assembly a shipped game links, by name.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Assembly names, ordered.</returns>
    public static List<string> RuntimeAssemblies(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var names = new List<string>();

        foreach (var layer in Layers) {
            var directory = Path.Combine(root, layer);

            if (!Directory.Exists(directory)) {
                continue;
            }

            foreach (var project in Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories)) {
                var path = project.Replace('\\', '/');

                if (path.Contains("/bin/", StringComparison.Ordinal)
                    || path.Contains("/obj/", StringComparison.Ordinal)) {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(project);

                if (name.EndsWith(".Tests", StringComparison.Ordinal)) {
                    continue;
                }

                var declared = Framework.Match(File.ReadAllText(project));

                if (declared.Success
                    && string.Equals(
                        declared.Groups[1].Value.Trim(),
                        RuntimeFramework,
                        StringComparison.Ordinal
                    )) {
                    names.Add(name);
                }
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Every runtime assembly the probe does not root.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="probe">The probe's project file.</param>
    /// <returns>Assembly names, ordered.</returns>
    public static List<string> Unrooted(string root, string probe) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(probe);

        var rooted = new HashSet<string>(AotProbeProjectFile.RootedAssemblies(probe), StringComparer.Ordinal);

        return RuntimeAssemblies(root).Where(name => !rooted.Contains(name)).ToList();
    }

    /// <summary>The assembly names the ledger admits are not rooted.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The names, in file order, with blank and <c>#</c> lines dropped.</returns>
    /// <remarks>
    ///     One name per line, and everything after the first space on a line is why it has not been
    ///     rooted yet — the arrangement <c>docs/ComponentGeneratorExempt.txt</c> uses. A line with no
    ///     reason is still read.
    /// </remarks>
    public static List<string> Ledger(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var path = Path.Combine(root, LedgerFile.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path)) {
            return [];
        }

        var names = new List<string>();

        foreach (var line in File.ReadAllLines(path)) {
            var text = line.Trim();

            if (text.Length == 0 || text.StartsWith('#')) {
                continue;
            }

            var space = text.IndexOf(' ', StringComparison.Ordinal);
            names.Add(space < 0 ? text : text[..space]);
        }

        return names;
    }

    /// <summary>What the gate reports.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="probe">The probe's project file.</param>
    /// <returns>One message per violation; empty when the tree agrees with the ledger.</returns>
    /// <remarks>
    ///     ⚠ <b>A ledger line for an assembly that is rooted now is a violation too</b>, which is
    ///     what makes the list only ever shrink — the property <c>docs/WhitespaceExempt.txt</c> and
    ///     <c>docs/ComponentGeneratorExempt.txt</c> have. Without it the scoreboard stops counting
    ///     the moment somebody does the work.
    /// </remarks>
    public static List<string> Violations(string root, string probe) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(probe);

        var unrooted = Unrooted(root, probe);
        var ledger = Ledger(root);
        var violations = new List<string>();

        foreach (var name in unrooted) {
            if (!ledger.Contains(name, StringComparer.Ordinal)) {
                violations.Add(
                    $"{name} is a net10.0 runtime assembly that Tools/Vixen.AotProbe does not root, so CheckAot "
                    + "asks nothing about it — ILC compiles only what Main happens to reach inside it, which is "
                    + "the coverage the probe's README says proves nothing. Add a TrimmerRootAssembly and its "
                    + $"ProjectReference and fix what ILC then reports, or add a line to {LedgerFile} saying why "
                    + "not yet."
                );
            }
        }

        foreach (var name in ledger) {
            if (!unrooted.Contains(name, StringComparer.Ordinal)) {
                violations.Add(
                    $"{name} is listed in {LedgerFile} and no longer needs to be — the probe roots it now, or the "
                    + "project is gone or is no longer a net10.0 runtime assembly. Remove the line; the list can "
                    + "only shrink."
                );
            }
        }

        return violations;
    }
}
