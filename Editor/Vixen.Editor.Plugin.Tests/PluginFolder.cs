// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Vixen.Editor.Plugin.Tests;

/// <summary>A plugin on disk: a manifest, and an assembly compiled from C# the test wrote.</summary>
/// <remarks>
///     <para>
///         <b>The whole point is that the assembly is a real file that can be replaced.</b> A test
///         fixture project copied into the output directory could prove that the loader loads an
///         assembly; it could not prove that <c>Reload</c> picks up a <i>rebuild</i> of one, which
///         is the claim the collectible context exists to support.
///     </para>
///     <para>
///         ⚠ <b>Compiled against the assemblies this process has loaded</b>, which is what makes the
///         load context's shared-assembly rule testable: the plugin's <c>IEditorPlugin</c> is
///         genuinely the host's type, so a context that loaded its own copy of
///         <c>Vixen.Editor.Plugin.dll</c> would fail the cast rather than pass unnoticed.
///     </para>
/// </remarks>
sealed class PluginFolder : IDisposable {
    /// <summary>Everything loaded beside the test, which is what a plugin compiles against.</summary>
    static readonly ImmutableArray<MetadataReference> References = [
        .. ((string) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference) MetadataReference.CreateFromFile(path))
    ];

    readonly bool owned;

    /// <summary>Makes an empty root that plugin directories go under.</summary>
    /// <param name="root">
    ///     Where, or <see langword="null" /> for a fresh temporary one this fixture also deletes.
    ///     ⚠ <b>A supplied root is not deleted</b>, because the one case for supplying one is putting
    ///     the plugin inside a directory something else owns — an editor session's, so the editor
    ///     finds it at start-up — and a fixture that deleted that would take the session with it.
    /// </param>
    public PluginFolder(string? root = null) {
        owned = root is null;

        Root = root ?? Path.Combine(
            Path.GetTempPath(),
            "vixen-plugin-tests",
            Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture)
        );

        Directory.CreateDirectory(Root);
    }

    /// <summary>The folder a <see cref="PluginDiscovery.Scan" /> is pointed at.</summary>
    public string Root { get; }

    /// <summary>Writes a plugin's manifest, and compiles its assembly if there is any code.</summary>
    /// <param name="id">The plugin's id, which is also its folder and its assembly name.</param>
    /// <param name="source">The C#, or <c>null</c> for a manifest with no assembly beside it.</param>
    /// <param name="manifest">Extra manifest lines, or <c>null</c> for the ordinary ones.</param>
    /// <returns>The plugin's directory.</returns>
    public string Write(string id, string? source = null, string? manifest = null) {
        var directory = Path.Combine(Root, id);
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, PluginManifest.FileName),
            manifest ?? $"""
                         id: {id}
                         name: {id}
                         version: 1.0.0
                         api: {EditorApi.Version.ToString(2)}
                         assembly: {id}.dll

                         """
        );

        if (source is not null) {
            Compile(id, source, Path.Combine(directory, id + ".dll"));
        }

        return directory;
    }

    /// <summary>Rewrites a plugin's assembly in place, which is what a rebuild does.</summary>
    /// <param name="id">The plugin.</param>
    /// <param name="source">The new C#.</param>
    public void Rebuild(string id, string source) =>
        Compile(id, source, Path.Combine(Root, id, id + ".dll"));

    /// <inheritdoc />
    public void Dispose() {
        if (!owned) {
            return;
        }

        try {
            Directory.Delete(Root, recursive: true);
        } catch (IOException) {
            // A plugin whose context has not been collected still has its dependencies mapped, so
            // the folder may be locked on Windows. Losing a temp directory is not a test failure.
        }
    }

    /// <remarks>
    ///     ⚠ <b>Roslyn here is the package version, which is older than the compiler that builds the
    ///     repository.</b> `params ReadOnlySpan&lt;T&gt;` is the visible difference — a plugin source
    ///     that calls `element.Add&lt;TextBlock&gt;()` has to spell the two optional arguments out.
    ///     That is a limit of the test's own compiler and not of what a plugin may write.
    /// </remarks>
    static void Compile(string assemblyName, string source, string path) {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var result = compilation.Emit(path);

        Assert.True(
            result.Success,
            "The test's own plugin source did not compile:\n"
            + string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        );
    }
}
