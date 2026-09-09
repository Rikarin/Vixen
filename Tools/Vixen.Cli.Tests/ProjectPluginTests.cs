// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.CommandLine;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Plugin;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>
///     Issue 1160: an asset a plugin's importer claims, imported from the command line rather than
///     from the editor, coming out as the plugin's artefact instead of as bytes.
/// </summary>
/// <remarks>
///     <para>
///         <b>The failure this ends is silent and exits zero.</b> <c>BuiltInImporters.Create()</c>
///         folds in <c>ImporterContributions.Default</c>, and nothing in this process had ever put
///         anything in it — <c>PluginHost</c> has no caller outside <c>EditorApplication</c>. So the
///         asset fell through to <c>RawImporter</c>, succeeded as a chunk called <c>Blob</c> that no
///         typed reader resolves, and said nothing about it.
///     </para>
///     <para>
///         ⚠ <b>The plugin is compiled here rather than referenced</b>, on
///         <c>PluginImporterTests</c>' terms. A fixture project this assembly referenced would
///         already be in the default load context, so the test would pass with none of this wired —
///         which is the shape of the bug rather than of the fix. An assembly in the project's own
///         <c>Plugins/</c> folder can only reach the registry by being discovered and loaded.
///     </para>
///     <para>
///         ⚠ <b>The importer uppercases rather than copying.</b> An importer that wrote its input
///         verbatim would be indistinguishable from the fallback, and the assertion would hold with
///         the plugin never loaded.
///     </para>
///     <para>
///         ⚠ <b>One plugin id, one extension and one registry alias per test.</b>
///         <c>TypeRegistry.Register</c> refuses a second type claiming an alias and never forgets
///         one, so two tests compiling a settings type under a shared name would fail each other
///         depending on which ran first — and these run in one process.
///     </para>
///     <para>
///         ⚠ <b>The fixture's settings type carries no data-contract attribute, and does not need
///         one.</b> What an importer's <c>Name</c> reads is the descriptor in <c>TypeRegistry</c>,
///         which a real plugin's generator emits from the attribute and which the module
///         initializer below writes by hand — this compilation has no generator driver to run. The
///         attribute would be inert here, and a project applying one while naming neither
///         registration generator is what <c>CheckArchitecture</c>'s
///         <c>DataContractGeneratorRule</c> refuses.
///     </para>
/// </remarks>
public sealed class ProjectPluginTests : IDisposable {
    /// <summary>Everything loaded beside the test, which is what the plugin compiles against.</summary>
    static readonly ImmutableArray<MetadataReference> References = [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
    ];

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-cli-plugin-tests", Guid.NewGuid().ToString("N"));

    public ProjectPluginTests() => Directory.CreateDirectory(Path.Combine(root, "Assets"));

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A plugin assembly stays mapped until its context is collected, which on Windows holds
            // the folder open. Losing a temporary directory is not a test failure.
        }
    }

    /// <summary>The claim: <c>vixen import</c> uses the importer the editor would have used.</summary>
    [Fact]
    public async Task AnAssetAPluginClaimsIsImportedByThatPluginsImporter() {
        Plugin("gizmo", ".gizmo");
        Asset("part.gizmo", "the quick brown fox");

        var (code, output) = await Run("import", "--verbose");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("(GizmoImporter)", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The other half, and the reason the first is not vacuous: the same project with its
    ///     <c>Plugins/</c> folder taken away imports the same file as raw bytes and says nothing
    ///     about it. This is what every command-line content build did.
    /// </summary>
    [Fact]
    public async Task TheSameAssetWithNoPluginFallsThroughToTheRawImporter() {
        Asset("part.doodad", "the quick brown fox");

        var (code, output) = await Run("import", "--verbose");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("(RawImporter)", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Bytes rather than a name, which is what "the same artefact as the editor" actually
    ///     claims: the shipped chunk carries what the plugin's importer wrote and not what was on
    ///     disk. <c>RawImporter</c> would have put the source text here, so this assertion cannot be
    ///     satisfied by the fallback.
    /// </summary>
    [Fact]
    public async Task TheShippedChunkCarriesWhatThePluginsImporterWrote() {
        Plugin("widget", ".widget");
        Asset("part.widget", "the quick brown fox", address: "ui/part", group: "UiCore");
        Group("UiCore");

        var (code, _) = await Run("content", "build");

        Assert.Equal(ExitCode.Success, code);

        var bundle = File.ReadAllBytes(
            Directory.GetFiles(Path.Combine(root, "Build", Project.HostTarget.Replace('/', '-')), "*.bundle").Single()
        );

        Assert.True(Holds(bundle, "THE QUICK BROWN FOX"), "The bundle does not carry the plugin's artefact.");
        Assert.False(Holds(bundle, "the quick brown fox"), "The bundle carries the source bytes, so RawImporter ran.");
    }

    /// <summary>
    ///     A plugin whose manifest says it is off is off here too. Discovery describes what is on
    ///     disk without judging it, so a build that ignored the switch would import with a set the
    ///     editor does not have — the same disagreement, with the sign reversed.
    /// </summary>
    [Fact]
    public async Task APluginTheManifestDisablesContributesNothing() {
        Plugin("dormant", ".dormant", enabled: false);
        Asset("part.dormant", "the quick brown fox");

        var (_, output) = await Run("import", "--verbose");

        Assert.Contains("(RawImporter)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DormantImporter", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A plugin the manifest names an assembly for that is not there is said out loud. Silence
    ///     is the failure this whole file is about, and "the plugin folder was there but empty" is
    ///     the shape a half-built plugin takes.
    /// </summary>
    [Fact]
    public async Task APluginWithNoAssemblyBesideItIsReported() {
        Directory.CreateDirectory(Path.Combine(root, ProjectPlugins.Folder, "absent"));

        File.WriteAllText(
            Path.Combine(root, ProjectPlugins.Folder, "absent", PluginManifest.FileName),
            $"""
             id: absent
             name: Absent
             version: 1.0.0
             api: {EditorApi.Version.ToString(2)}
             assembly: absent.dll

             """
        );

        Asset("notes.txt", "just a file");

        var (code, output) = await Run("import");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("absent: absent.dll is not in", output, StringComparison.Ordinal);
        Assert.Contains("imported as raw bytes", output, StringComparison.Ordinal);
    }

    /// <summary>Whether a bundle carries a run of bytes anywhere in it.</summary>
    /// <remarks>
    ///     ⚠ A bundle is a container and the chunk is somewhere inside it. Searching the whole file
    ///     rather than parsing it is deliberate: what is being asserted is which bytes were written,
    ///     and a test that read the container through the same reader the writer used would agree
    ///     with the writer whatever either of them did.
    /// </remarks>
    static bool Holds(byte[] bundle, string text) =>
        bundle.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text).AsSpan()) >= 0;

    /// <summary>Writes a plugin into the project's own <c>Plugins/</c> folder, manifest and all.</summary>
    /// <param name="id">The plugin's id, its folder, its assembly name and the stem of its type names.</param>
    /// <param name="extension">What its importer claims.</param>
    /// <param name="enabled">Whether the manifest says it is on.</param>
    void Plugin(string id, string extension, bool enabled = true) {
        var directory = Path.Combine(root, ProjectPlugins.Folder, id);
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, PluginManifest.FileName),
            $"""
             id: {id}
             name: {id}
             version: 1.0.0
             api: {EditorApi.Version.ToString(2)}
             assembly: {id}.dll
             enabled: {(enabled ? "true" : "false")}

             """
        );

        var name = char.ToUpperInvariant(id[0]) + id[1..];

        var result = CSharpCompilation.Create(
                id,
                [CSharpSyntaxTree.ParseText(Source(name, extension), new CSharpParseOptions(LanguageVersion.Latest))],
                References,
                new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
            )
            .Emit(Path.Combine(directory, id + ".dll"));

        Succeeded(result);
    }

    /// <summary>A plugin with one importer, and the module initializer a real plugin's generator writes.</summary>
    /// <remarks>
    ///     ⚠ <b>The descriptor is registered by hand because this compilation has no generator
    ///     driver.</b> A plugin built by <c>dotnet build</c> gets
    ///     <c>Vixen.Core.Reflection.Generator</c> through its own <c>ProjectReference</c>, and this
    ///     initializer is what that generator emits. Without a descriptor the importer has no
    ///     <c>Name</c>, which is the one thing <c>PluginImporters</c> reads eagerly so that the
    ///     failure names the assembly rather than arriving from four frames inside the registry.
    /// </remarks>
    static string Source(string name, string extension) =>
        $$"""
          using System;
          using System.IO;
          using System.Runtime.CompilerServices;
          using System.Text;
          using System.Threading;
          using System.Threading.Tasks;
          using Vixen.Core;
          using Vixen.Core.Reflection;
          using Vixen.Core.Yaml.Meta;
          using Vixen.Editor.Assets;

          namespace {{name}}Plugin;

          public sealed record {{name}}ImportSettings : IImportSettings {
              public int Version { get; init; } = 1;
          }

          [Importer("{{extension}}")]
          public sealed class {{name}}Importer : AssetImporter<{{name}}ImportSettings> {
              public override int Version => 1;

              protected override async ValueTask<ImportResult> ImportAsync(
                  ImportContext context,
                  {{name}}ImportSettings settings,
                  CancellationToken cancellationToken
              ) {
                  await using var source = await context.OpenSourceAsync(cancellationToken);
                  using var reader = new StreamReader(source);
                  var text = await reader.ReadToEndAsync(cancellationToken);

                  context.Write(SubAssetId.Main, "{{name}}", Encoding.UTF8.GetBytes(text.ToUpperInvariant()));
                  return context.Finish();
              }
          }

          public static class {{name}}Registration {
              [ModuleInitializer]
              internal static void Register() =>
                  TypeRegistry.Register(
                      new TypeDescriptor(
                          typeof({{name}}ImportSettings),
                          "{{name}}Importer",
                          TypeTraits.DataContract,
                          [],
                          () => new {{name}}ImportSettings()
                      )
                  );
          }
          """;

    static void Succeeded(EmitResult result) =>
        Assert.True(
            result.Success,
            "The test's own plugin source did not compile:\n"
            + string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        );

    void Asset(string relativePath, string content, string? address = null, string? group = null) {
        var absolute = Path.Combine(root, "Assets", relativePath);
        File.WriteAllText(absolute, content);

        if (address is null && group is null) {
            return;
        }

        AssetMetaFile.WriteFile(
            AssetMetaFile.PathFor(absolute),
            new() { Guid = AssetId.New(), Addressable = new() { Address = address, Group = group, Labels = [] } }
        );
    }

    void Group(string name) =>
        File.WriteAllText(
            Path.Combine(root, "Assets", $"{name}.vxgroup"),
            YamlSerializer.ToYaml(new AddressableGroup { Name = name })
        );

    async Task<(ExitCode Code, string Output)> Run(params string[] args) {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };

        var parsed = VixenCommand.Create(output, error).Parse([.. args, "--project", root]);

        Assert.Empty(parsed.Errors);

        var code = await parsed.InvokeAsync(null, TestContext.Current.CancellationToken);

        return ((ExitCode)code, output.ToString());
    }
}
