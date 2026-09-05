// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     ⚠ <b>Which <c>libassimp</c> the binding opens, asked of the binding rather than believed.</b>
/// </summary>
/// <remarks>
///     <para>
///         <c>Ultz.Native.Assimp</c> 6.0.2 — a dependency of <c>Silk.NET.Assimp</c> — ships
///         <b>two</b> majors of the native in every Linux and macOS RID: <c>libassimp.so.5</c> beside
///         <c>libassimp.so.6</c>, <c>libassimp.5.dylib</c> beside <c>libassimp.6.dylib</c>. Windows
///         ships one unversioned <c>Assimp64.dll</c> and has no choice to make. Only one of each pair
///         is ever <c>dlopen</c>ed, and 44 251 540 bytes of the other travelled in every
///         <c>Vixen.Sdk</c> until <c>Vixen.Sdk.csproj</c> stopped packing it.
///     </para>
///     <para>
///         ⚠ <b>The saving was filed against the wrong major.</b>
///         <a href="https://github.com/Rikarin/Vixen/issues/624">#624</a> names
///         "<c>libassimp.so.5</c> plus <c>libassimp.5.dylib</c>" as the copy nothing loads, and it is
///         the one that does: <c>Silk.NET.Assimp</c> 2.23.0 binds Assimp <b>5</b>'s C ABI.
///         <c>ci.yml</c> already said so in a comment — it installs Ubuntu's <c>libassimp5</c> and
///         warns that a 6 "would load and then be wrong in ways a signature cannot catch" — and the
///         issue was filed against a byte table rather than against that sentence. Excluding the 5
///         would have produced a package that restores, installs and runs, and throws
///         <c>FileNotFoundException</c> out of <c>Assimp.GetApi()</c> the first time anybody imported
///         a model.
///     </para>
///     <para>
///         ⚠ <b>So the number in the exclusion is held to the binding and not to a comment.</b> This
///         is the half a pack test cannot do: <c>Vixen.Sdk.Tests</c> can say that the file named in
///         the <c>Exclude</c> is absent from the package, and only the binding can say whether that
///         was the right file. The day <c>Silk.NET.Assimp</c> moves to Assimp 6, this goes red and
///         names the soname to keep — rather than the package quietly shipping without it.
///     </para>
/// </remarks>
public class AssimpSonameTests {
    /// <summary>Every library name the binding will ask a platform for.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off <c>AssimpLibraryNameContainer</c>, which is <c>internal</c></b> — so the
    ///     lookup is by reflection and the first assertion is that it was found at all. A reflective
    ///     probe that silently matched nothing would agree with any exclusion this file could hold,
    ///     which is the failure mode every walk in this repository has had to be given an anchor for.
    /// </remarks>
    static IReadOnlyList<string> Names() {
        var container = typeof(Silk.NET.Assimp.Assimp).Assembly
            .GetTypes()
            .SingleOrDefault(type => type.Name == "AssimpLibraryNameContainer");

        Assert.True(
            container is not null,
            "Silk.NET.Assimp no longer has an AssimpLibraryNameContainer, so this check read nothing. "
            + "The names it held are what Tools/Vixen.Sdk/Vixen.Sdk.csproj's Assimp exclusion is "
            + "keyed on, and they have to be re-established before that exclusion can be trusted."
        );

        var instance = Activator.CreateInstance(container!, nonPublic: true);

        // ⚠ Every one of them is a `string[]` and not a `string`: a platform may name several
        // candidates and the loader tries each. Assimp names exactly one per platform today, which is
        // why the assertions below can be about a single soname — but reading the property as a
        // string would have found nothing at all and said "the binding names no library", which is
        // the shape of wrong answer this file exists to refuse.
        var names = container!
            .GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.FlattenHierarchy
            )
            .Where(property => property.PropertyType == typeof(string[]) && property.GetGetMethod(true) is not null)
            .SelectMany(property =>
                (string[]?)property.GetValue(property.GetGetMethod(true)!.IsStatic ? null : instance) ?? []
            )
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();

        Assert.NotEmpty(names);

        return names;
    }

    /// <summary>The binding asks for Assimp 5 on every platform that ships a versioned name.</summary>
    /// <remarks>
    ///     <c>Windows64</c> and <c>Windows86</c> are unversioned and <c>IOS</c> is
    ///     <c>__Internal</c>; what is asserted is the three that carry a soname, because they are the
    ///     three RID families whose package holds a pair.
    /// </remarks>
    [Fact]
    public void The_binding_asks_for_assimp_5_wherever_the_name_carries_a_major() {
        var names = Names();

        Assert.Contains("libassimp.so.5", names);
        Assert.Contains("libassimp.5.dylib", names);
        Assert.DoesNotContain("libassimp.so.6", names);
        Assert.DoesNotContain("libassimp.6.dylib", names);
    }

    /// <summary>And the SDK does not exclude anything the binding will go looking for.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion is over the names, not over a version number</b>, so it survives the
    ///     exclusion being rewritten in any spelling: what it refuses is a package that drops a file
    ///     <c>Assimp.GetApi()</c> asks for by name. The second half — that the <i>other</i> major is
    ///     excluded — is deliberately not asserted here, because "we could save more" is a smaller
    ///     claim than "the tool still works" and belongs in the pack test that can see the package.
    /// </remarks>
    [Fact]
    public void The_sdk_excludes_no_soname_the_binding_opens() {
        var project = File.ReadAllText(SdkProject());

        foreach (var name in Names()) {
            Assert.DoesNotContain(
                "/" + name + ";",
                project.Replace('\\', '/'),
                StringComparison.Ordinal
            );

            Assert.DoesNotContain(
                "/" + name + "\"",
                project.Replace('\\', '/'),
                StringComparison.Ordinal
            );
        }
    }

    /// <summary>Where <c>Vixen.Sdk.csproj</c> is, found by walking up from the test binary.</summary>
    /// <remarks>
    ///     ⚠ Up from the assembly and never down from the repository root: <c>.claude/worktrees</c>
    ///     holds a full checkout per parallel agent, so a search from above finds another branch's
    ///     copy of this file and reports on a tree nobody is editing.
    /// </remarks>
    static string SdkProject() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Tools", "Vixen.Sdk", "Vixen.Sdk.csproj");

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        Assert.Fail($"Tools/Vixen.Sdk/Vixen.Sdk.csproj was not found above '{AppContext.BaseDirectory}'.");
        return string.Empty;
    }
}
