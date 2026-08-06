// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>What this assembly may depend on, and — the half that decides the design — what may not depend on it.</summary>
/// <remarks>
///     <para>
///         <b>The direction is the whole point.</b> docs/plan/42 § D2:
///         <c>Vixen.Geometry.Remeshing</c> references this, never the reverse, because the unwrapper
///         has to run on meshes nobody remeshed. An edge the other way would make the general
///         unwrapper a feature of the remesher, which is exactly the scope doc 41 § D13 left standing
///         and this document exists to close.
///     </para>
///     <para>
///         ⚠ <b><c>MeshData</c> is out of reach and that is deliberate rather than incidental.</b>
///         It lives in <c>Vixen.Rendering</c>, one layer up. A seam is one shared position whose two
///         sides carry different coordinates — free in <see cref="EditMesh" />'s corner layer, and a
///         vertex split in any per-vertex structure. Being unable to reach the per-vertex one is what
///         keeps the split at the end, beside the code that uploads it.
///     </para>
/// </remarks>
public class UvLayeringTests {
    static readonly string[] Forbidden = [
        "Vixen.Gameplay", "Vixen.Editor", "Vixen.Platform.", "Vixen.Raven", "Vixen.Rendering", "Vixen.Engine"
    ];

    [Fact]
    public void TheUnwrapperReferencesNothingAboveIt() {
        var violations = References()
            .Where(name => Forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Vixen.Geometry.Uv references {string.Join(", ", violations)}. See docs/plan/42 § D2."
        );
    }

    [Fact]
    public void TheUnwrapperReferencesOnlyWhatItsThreeStagesNeed() {
        var allowed = new HashSet<string>(StringComparer.Ordinal) {
            // The mesh it unwraps, and the arithmetic.
            "Vixen.Geometry",
            "Vixen.Core.Mathematics",

            // The charter recurses per region and the packer rasterizes per island; both are a
            // parallel-for over work that writes only to its own index.
            "Vixen.Core.Threading",

            // ⚠ Transitive and unavoidable: Vixen.Core.Threading carries these two with it, so the
            // closure is five assemblies where docs/plan/42 § D2 names three. Stated here rather
            // than arriving unremarked, because the next reference to appear should have to argue
            // for itself.
            "Vixen.Core",
            "Vixen.Core.Diagnostics"
        };

        var unexpected = References()
            .Where(name => name.StartsWith("Vixen.", StringComparison.Ordinal))
            .Where(name => !allowed.Contains(name))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Vixen.Geometry.Uv has grown a reference to {string.Join(", ", unexpected)}. "
            + "If that is right, say so here and in the .csproj."
        );
    }

    /// <summary>
    ///     ⚠ The remesher may depend on this. Nothing else in <c>Core/</c> may, because an unwrapper
    ///     that something in the frame links is an unwrapper that has become a runtime feature —
    ///     which docs/plan/42 § "What this does not become" item 6 rules out.
    /// </summary>
    [Fact]
    public void OnlyTheRemesherDependsOnTheUnwrapper() {
        var dependents = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Where(name => name?.StartsWith("Vixen.", StringComparison.Ordinal) == true)
            .Where(name => name is not ("Vixen.Geometry.Uv" or "Vixen.Geometry.Uv.Tests" or "Vixen.Geometry.Remeshing"))
            .Where(
                name => AppDomain.CurrentDomain.GetAssemblies()
                    .First(assembly => assembly.GetName().Name == name)
                    .GetReferencedAssemblies()
                    .Any(reference => reference.Name == "Vixen.Geometry.Uv")
            )
            .ToList();

        Assert.True(dependents.Count == 0, $"{string.Join(", ", dependents)} depends on Vixen.Geometry.Uv.");
    }

    /// <summary>The same rule against the project file, because the compiled one cannot see an unused reference.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This exists because the reflection test above was sabotage-tested and did not
    ///         fail.</b> A <c>ProjectReference</c> to <c>Vixen.Rendering</c> was added to this
    ///         assembly's project file, the solution rebuilt, and all three facts still passed —
    ///         because Roslyn elides an assembly reference whose types nothing uses, so
    ///         <c>GetReferencedAssemblies</c> never sees it.
    ///     </para>
    ///     <para>
    ///         That matters more than it sounds. The failure mode a layering test is <i>for</i> is a
    ///         reference arriving with an unrelated feature and nobody noticing — and the window
    ///         between "the line is in the csproj" and "somebody uses a type from it" is exactly when
    ///         it is cheap to remove. The reflection test catches the second event; this catches the
    ///         first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <c>nuke CheckArchitecture</c> does not cover this either.</b> Its rules are
    ///         about folders — <c>Core/</c> may not reference <c>Editor/</c> and so on — so a
    ///         <c>Core/</c> assembly reaching sideways to another <c>Core/</c> assembly passes it.
    ///         The three layering tests in the AI stack have the same blind spot; if this shape
    ///         proves out it belongs in the gate, where it would cover all of them at once.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheProjectFileDeclaresOnlyTheSameReferences() {
        var declared = DeclaredReferences("Vixen.Geometry.Uv")
            .Where(name => !name.Equals("Vixen.Geometry", StringComparison.Ordinal))
            .Where(name => !name.Equals("Vixen.Core.Mathematics", StringComparison.Ordinal))
            .Where(name => !name.Equals("Vixen.Core.Threading", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            declared.Count == 0,
            $"Vixen.Geometry.Uv.csproj declares a reference to {string.Join(", ", declared)}. "
            + "docs/plan/42 § D2 names three, and the closure is already five."
        );
    }

    /// <summary>What a project file asks for, whether or not the compiler kept it.</summary>
    internal static IEnumerable<string> DeclaredReferences(string project) {
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
            // back the entire relative path and every comparison below silently misses. Found by
            // the sabotage test that motivated this fact in the first place.
            .Select(include => include.Replace('\\', '/'))
            .Select(include => Path.GetFileNameWithoutExtension(include.AsSpan()).ToString())
            .Where(name => name.Length > 0);
    }

    static IEnumerable<string> References() =>
        typeof(UvIsland).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name ?? string.Empty);
}
