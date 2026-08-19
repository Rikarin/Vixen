// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Vixen.Audio.Backend.WebAudio;
using Vixen.Graphics.WebGPU.Browser;
using Xunit;

namespace Vixen.Platform.Web.Tests;

/// <summary>
///     The one thing every browser binding gets wrong in the same way: where its JavaScript module
///     is fetched from.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the regression test for a defect that shipped in all three bindings at once
///         and that nothing in the repository could see.</b>
///         <c>JSHost.ImportAsync</c> takes a URL and hands it to a dynamic <c>import()</c> issued
///         from the <em>runtime's own module</em>, which <c>Microsoft.NET.Sdk.WebAssembly</c>
///         publishes into <c>_framework/</c>. The three <c>vixen-*.js</c> files are content files
///         and are published to the <em>site root</em>. So the obvious-looking
///         <c>"./vixen-platform.js"</c> asked for <c>_framework/vixen-platform.js</c>, which is not
///         there, and the failure arrived as <c>TypeError: Failed to fetch dynamically imported
///         module</c> thrown from inside <c>WebPlatform.CreateAsync</c> — that is, the default
///         configuration of the platform could never start.
///         (docs/plan/spikes/web-head/RESULT.md § 1.)
///     </para>
///     <para>
///         <b>It is testable here only because the constants were moved into files that hold nothing
///         else.</b> The rest of each interop class is <c>[JSImport]</c>, which needs the browser
///         runtime pack; the <c>*Interop.Module.cs</c> files need nothing, so this project — plain
///         <c>net10.0</c>, in <c>Vixen.slnx</c>, run by <c>nuke Test</c> on all three CI legs —
///         links them as source. No browser, no <c>wasm-tools</c>, no publish.
///     </para>
///     <para>
///         <b>What it cannot know is that the SDK's runtime directory is called <c>_framework</c>,
///         and that content files really do land beside the page.</b> Those are facts about the
///         WebAssembly SDK, not about this code, and asserting them here would only restate the
///         assumption. <c>nuke PublishWeb</c> checks them against a head the SDK actually published,
///         which is the other half of this invariant and the reason that target exists.
///     </para>
/// </remarks>
public class BrowserModuleUrlTests {
    /// <summary>
    ///     Where the browser resolves a binding's relative module URL from.
    /// </summary>
    /// <remarks>
    ///     A dynamic <c>import()</c> resolves against the module that issued it, and the module that
    ///     issues this one is the .NET runtime's, in <c>_framework/</c>. The host and the file name
    ///     are arbitrary; the directory is not, and is what every assertion below turns on.
    /// </remarks>
    static readonly Uri RuntimeModule = new("https://vixen.invalid/_framework/dotnet.runtime.js");

    /// <summary>The three browser bindings: project directory, module name, default module URL.</summary>
    public static TheoryData<string, string, string> Bindings => new() {
        { "Vixen.Platform.Web", WebInterop.ModuleName, WebInterop.DefaultModuleUrl },
        { "Vixen.Audio.Backend.WebAudio", WebAudioInterop.ModuleName, WebAudioInterop.DefaultModuleUrl },
        { "Vixen.Graphics.WebGPU.Browser", WebGpuInterop.ModuleName, WebGpuInterop.DefaultModuleUrl }
    };

    /// <summary>
    ///     The instrument, checked before anything is measured with it: a single dot resolves into
    ///     <c>_framework/</c>, which is precisely the bug.
    /// </summary>
    /// <remarks>
    ///     Without this, a resolution helper that quietly ignored its base would make every
    ///     assertion below pass against the broken value as happily as against the fixed one.
    /// </remarks>
    [Fact]
    public void ASingleDotResolvesIntoTheRuntimeDirectory() {
        var resolved = new Uri(RuntimeModule, "./vixen-platform.js");

        Assert.Equal("/_framework/vixen-platform.js", resolved.AbsolutePath);
    }

    /// <summary>
    ///     Each default module URL, resolved the way the browser will resolve it, names a file at the
    ///     site root.
    /// </summary>
    [Theory]
    [MemberData(nameof(Bindings))]
    public void TheDefaultModuleUrlResolvesToTheSiteRoot(string project, string moduleName, string moduleUrl) {
        var resolved = new Uri(RuntimeModule, moduleUrl);

        // First, and separately, because this is the failure that has actually happened and it
        // deserves the sentence rather than a string diff.
        Assert.False(
            resolved.AbsolutePath.StartsWith("/_framework/", StringComparison.Ordinal),
            $"{project}'s default module URL '{moduleUrl}' resolves to '{resolved.AbsolutePath}'. "
            + "JSHost.ImportAsync resolves against the runtime's module in _framework/, and "
            + $"{moduleName}.js is a content file at the site root, so this fetches nothing and "
            + "surfaces as 'TypeError: Failed to fetch dynamically imported module' from inside "
            + "CreateAsync. It wants ../ and not ./ — see docs/plan/spikes/web-head/RESULT.md."
        );

        // And then the whole of it: the right directory and the file the module name implies.
        Assert.Equal($"/{moduleName}.js", resolved.AbsolutePath);
    }

    /// <summary>
    ///     The file each default module URL names is one the project actually ships.
    /// </summary>
    /// <remarks>
    ///     A URL that resolves to the right <em>place</em> and the wrong <em>name</em> fails exactly
    ///     as loudly and exactly as late, so the name is checked against the file on disk rather than
    ///     against a second copy of the string.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Bindings))]
    public void TheFileTheDefaultModuleUrlNamesIsShipped(string project, string moduleName, string moduleUrl) {
        var fileName = Path.GetFileName(new Uri(RuntimeModule, moduleUrl).AbsolutePath);
        var shipped = Path.Combine(RepositoryRoot(), "Platform", project, "wwwroot", fileName);

        Assert.True(
            File.Exists(shipped),
            $"{project}'s default module URL '{moduleUrl}' names {fileName}, and there is no such "
            + $"file at '{shipped}'. The module name is '{moduleName}'; one of the two moved."
        );
    }

    /// <summary>
    ///     Each project publishes that file to the site root rather than beside the assembly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>contentFiles/any/any/</c> is what puts it at the root of a consuming head's
    ///         content, which is the layout <c>../</c> is correct for. This reads the project file
    ///         because the packaging decision <em>is</em> in the project file and there is nowhere
    ///         else it could be observed from a test — and because that line and the constant have to
    ///         change together, which is the failure this catches.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Bindings))]
    public void TheProjectPacksThatFileForTheSiteRoot(string project, string moduleName, string moduleUrl) {
        var fileName = Path.GetFileName(new Uri(RuntimeModule, moduleUrl).AbsolutePath);
        var projectFile = Path.Combine(RepositoryRoot(), "Platform", project, $"{project}.csproj");
        var text = File.ReadAllText(projectFile);

        var item = text.IndexOf($"wwwroot\\{fileName}", StringComparison.Ordinal);

        Assert.True(
            item >= 0,
            $"{project}.csproj does not ship wwwroot\\{fileName}, which its module '{moduleName}' "
            + "is imported from. Nothing would copy it, and the import would 404."
        );

        var declaration = text[item..];
        var end = declaration.IndexOf("/>", StringComparison.Ordinal);

        Assert.True(end >= 0, $"the <None> item for {fileName} in {project}.csproj is not closed.");

        declaration = declaration[..end];

        Assert.True(
            declaration.Contains("contentFiles/any/any/", StringComparison.Ordinal),
            $"{project}.csproj ships wwwroot\\{fileName} but does not pack it to "
            + "contentFiles/any/any/, which is what puts it at the root of a consuming head's "
            + $"content. Its module URL '{moduleUrl}' is written for that layout, so wherever this "
            + $"lands instead, the import will 404. The declaration reads:{Environment.NewLine}"
            + declaration
        );

        // The value and not merely the attribute: CopyToOutputDirectory="Never" is spelled the same
        // as not asking for a copy at all, and produces the same published page — one that 404s on
        // its own binding. Anything that is not "Never" copies.
        Assert.True(
            declaration.Contains("CopyToOutputDirectory", StringComparison.Ordinal)
            && !declaration.Contains("CopyToOutputDirectory=\"Never\"", StringComparison.Ordinal),
            $"{project}.csproj does not copy wwwroot\\{fileName} to its output, so a project "
            + $"reference — which is how the samples and the spike head consume it — gets no "
            + $"{fileName} at all. The declaration reads:{Environment.NewLine}{declaration}"
        );
    }

    /// <summary>The repository root, found by walking up rather than by counting directories.</summary>
    static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            if (File.Exists(Path.Combine(directory.FullName, "Vixen.slnx"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
