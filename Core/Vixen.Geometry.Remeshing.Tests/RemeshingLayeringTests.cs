// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>Why a remesher is under <c>Core/</c> when doc 40 put inference under <c>Editor/</c>.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D2, and the contrast is the argument.</b> Doc 40 § D9's reasons for
///         keeping generation in an editor assembly — a shipped game infers nothing, the weights are
///         the author's to accept, the runtime must not grow — are all statements about
///         <i>models</i>. There is no model here. It is arithmetic over triangles, and it has four
///         callers that are not the editor: the asset compiler, the CLI, doc 24's blockout mode, and
///         a game's own build-time tooling.
///     </para>
///     <para>
///         ⚠ <b>Which makes the negative half the load-bearing one.</b> A remesher that acquired a
///         reference to graphics or to assets would be one a shipped game links, and
///         docs/plan/41 § "What this does not become" item 3 says no shipped game remeshes anything.
///     </para>
/// </remarks>
public class RemeshingLayeringTests {
    static readonly string[] Forbidden = [
        "Vixen.Gameplay", "Vixen.Editor", "Vixen.Platform.", "Vixen.Raven", "Vixen.Rendering", "Vixen.Engine",
        "Vixen.Assets", "Vixen.Graphics"
    ];

    [Fact]
    public void TheRemesherReferencesNothingAboveIt() {
        var violations = References()
            .Where(name => Forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Vixen.Geometry.Remeshing references {string.Join(", ", violations)}. See docs/plan/41 § D2."
        );
    }

    [Fact]
    public void TheRemesherReferencesOnlyWhatItsSevenStagesNeed() {
        var allowed = new HashSet<string>(StringComparer.Ordinal) {
            // The mesh kernel it hands quads to, and the arithmetic.
            "Vixen.Geometry",
            "Vixen.Core.Mathematics",

            // ⚠ One packer, not two. docs/plan/42 § D2 amends doc 41 § D13: the privileged charting
            // stays here, because a quantized quad patch *is* a rectangle and re-cutting it with a
            // general charter would be worse in every respect — but the merging and the packing are
            // calls into the unwrapper, so there is one margin rule rather than two.
            "Vixen.Geometry.Uv",

            // The field smoothing is a local operator over a deterministic graph colouring, which is
            // a parallel-for whose every index writes only to its own element.
            "Vixen.Core.Threading",

            // ⚠ Transitive and unavoidable: Vixen.Core.Threading carries these two, so the closure
            // is six assemblies where docs/plan/41 § D2 names four.
            "Vixen.Core",
            "Vixen.Core.Diagnostics"
        };

        var unexpected = References()
            .Where(name => name.StartsWith("Vixen.", StringComparison.Ordinal))
            .Where(name => !allowed.Contains(name))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Vixen.Geometry.Remeshing has grown a reference to {string.Join(", ", unexpected)}. "
            + "If that is right, say so here and in the .csproj."
        );
    }

    /// <summary>
    ///     ⚠ And the half that matters more: nothing in the frame depends on this. Import time and
    ///     edit time, and <c>Vixen.Rendering</c> never learns that it exists.
    /// </summary>
    [Fact]
    public void NothingLoadedAlongsideTheRemesherDependsOnIt() {
        var dependents = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("Vixen.", StringComparison.Ordinal) == true)
            .Where(assembly => assembly.GetName().Name != "Vixen.Geometry.Remeshing")
            .Where(assembly => assembly.GetName().Name != "Vixen.Geometry.Remeshing.Tests")
            .Where(
                assembly => assembly.GetReferencedAssemblies()
                    .Any(reference => reference.Name == "Vixen.Geometry.Remeshing")
            )
            .Select(assembly => assembly.GetName().Name)
            .ToList();

        Assert.True(dependents.Count == 0, $"{string.Join(", ", dependents)} depends on Vixen.Geometry.Remeshing.");
    }

    /// <summary>The same rule against the project file, because the compiled one cannot see an unused reference.</summary>
    /// <remarks>
    ///     ⚠ <b>The reflection facts above were sabotage-tested and did not fail.</b> Roslyn elides
    ///     an assembly reference whose types nothing uses, so <c>GetReferencedAssemblies</c> reports
    ///     a project file that asks for <c>Vixen.Rendering</c> as though it did not — and
    ///     <c>nuke CheckArchitecture</c> does not cover it either, because its rules are about
    ///     folders and this would be one <c>Core/</c> assembly reaching sideways to another. The
    ///     window between "the line is in the csproj" and "somebody uses a type from it" is exactly
    ///     when a reference is cheap to remove, so it is the window worth watching.
    /// </remarks>
    [Fact]
    public void TheProjectFileDeclaresOnlyTheSameReferences() {
        string[] expected = [
            "Vixen.Geometry", "Vixen.Geometry.Uv", "Vixen.Core.Mathematics", "Vixen.Core.Threading"
        ];

        var declared = DeclaredReferences("Vixen.Geometry.Remeshing")
            .Where(name => !expected.Contains(name, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            declared.Count == 0,
            $"Vixen.Geometry.Remeshing.csproj declares a reference to {string.Join(", ", declared)}. "
            + "docs/plan/41 § D2 names four, and the closure is already six."
        );
    }

    /// <summary>What a project file asks for, whether or not the compiler kept it.</summary>
    static IEnumerable<string> DeclaredReferences(string project) {
        // bin/Debug/net10.0 → the test project → Core/ → the repository.
        var root = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent?.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException($"No repository above {AppContext.BaseDirectory}.");

        var file = Path.Combine(root.FullName, "Core", project, project + ".csproj");

        Assert.True(File.Exists(file), $"{file} is not where a layering test expected to find it.");

        return XDocument.Load(file)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)

            // ⚠ MSBuild writes its separators as backslashes on every platform, and `Path` on a
            // Unix host does not treat one as a separator — so `GetFileNameWithoutExtension` hands
            // back the entire relative path and every comparison below silently misses.
            .Select(include => include.Replace('\\', '/'))
            .Select(include => Path.GetFileNameWithoutExtension(include.AsSpan()).ToString())
            .Where(name => name.Length > 0);
    }

    static IEnumerable<string> References() =>
        typeof(RemeshSettings).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name ?? string.Empty);
}
