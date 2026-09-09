// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Editor.Assets;
using Xunit;

namespace Vixen.AssetCompiler.Tests;

/// <summary>Doc 36 § Part 6: the same asset, imported here and in a worker, coming out the same.</summary>
/// <remarks>
///     <para>
///         <b>The failure this ends is a difference between two paths that should agree.</b>
///         <c>BuiltInImporters.Create()</c> folds in <c>ImporterContributions.Default</c>, and in a
///         worker process that set is empty because nothing there loaded the plugin. So an asset only
///         a plugin can import was imported by the editor and refused by the pool, with the
///         difference being which process happened to run it — a content build producing different
///         bytes than the editor showed.
///     </para>
///     <para>
///         ⚠ <b>The plugin is compiled here rather than referenced.</b> A fixture project the test
///         assembly referenced would sit in the worker's own directory and load into its default
///         context, so the test would pass with the worker told nothing — which is the shape of the
///         bug, not of the fix. An assembly in a temporary folder can only reach the worker by being
///         named on its command line.
///     </para>
///     <para>
///         ⚠ <b>The importer uppercases rather than copying</b>, so a run that fell through to
///         <c>RawImporter</c> would produce the source bytes and fail the assertion. An importer that
///         wrote its input verbatim would be indistinguishable from the fallback, and the test would
///         pass with none of this wired.
///     </para>
///     <para>
///         ⚠ <b>Every plugin this file compiles gets a name of its own.</b>
///         <c>TypeRegistry.Register</c> refuses a second type claiming one alias, and two settings
///         types compiled under one name are two types however they were loaded — so a shared alias
///         would make these tests fail each other depending on which ran first.
///     </para>
/// </remarks>
public sealed class PluginImporterTests : IDisposable {
    /// <summary>Everything loaded beside the test, which is what the plugin compiles against.</summary>
    static readonly ImmutableArray<MetadataReference> References = [
        .. ((string) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference) MetadataReference.CreateFromFile(path))
    ];

    readonly string project = Path.Combine(
        Path.GetTempPath(),
        $"vixen-plugin-importer-{Environment.ProcessId}-{Guid.NewGuid():N}"
    );

    public PluginImporterTests() {
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "Plugin"));
        File.WriteAllText(Path.Combine(project, "Assets", "gadget.widget"), "the quick brown fox");
    }

    public void Dispose() {
        if (!Directory.Exists(project)) {
            return;
        }

        try {
            Directory.Delete(project, recursive: true);
        } catch (IOException) {
            // A loaded plugin's dependencies stay mapped until its context is collected, which on
            // Windows can hold the folder open. Losing a temp directory is not a test failure.
        }
    }

    /// <summary>The claim: one asset, two processes, the same bytes.</summary>
    [Fact]
    public async Task APluginsImporterProducesTheSameArtefactInAWorkerAsItDoesHere() {
        var contributions = PluginImporters.Load([WriteLibrary("Widget", ".widget")]);

        Assert.Equal("WidgetImporter", Assert.Single(contributions.All).Name);

        var here = await new InProcessImportExecutor(
            BuiltInImporters.Create(contributions),
            new PhysicalFileProvider(project, isReadOnly: true)
        ).ExecuteAsync(Job("WidgetImporter"), Cancellation);

        using var pool = new CompilerPool(project, workers: 1, contributed: contributions);

        var there = await pool.ExecuteAsync(Job("WidgetImporter"), Cancellation);

        Assert.True(here.Succeeded, Why(here));
        Assert.True(there.Succeeded, Why(there));

        Assert.Equal("THE QUICK BROWN FOX", Text(here));
        Assert.Equal("THE QUICK BROWN FOX", Text(there));
        Assert.Equal(Assert.Single(here.Artifacts).Type, Assert.Single(there.Artifacts).Type);
        Assert.Equal(here.FileDependencies, there.FileDependencies);
    }

    /// <summary>
    ///     The other half of the same assertion, and the reason the one above is not vacuous: a pool
    ///     told about no plugin refuses the asset by name. This is what every build did before the
    ///     coordinator started naming its plugin assemblies, and it is what the first assertion would
    ///     still be measuring if the arguments never reached the worker.
    /// </summary>
    [Fact]
    public async Task AWorkerToldAboutNoPluginRefusesTheAssetThatPluginsImporterClaims() {
        using var pool = new CompilerPool(project, workers: 1, contributed: new ImporterContributions());

        var result = await pool.ExecuteAsync(Job("WidgetImporter"), Cancellation);

        Assert.False(result.Succeeded);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("WidgetImporter", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     A worker that cannot load what it was told to load must not import with a different set —
    ///     the disagreement is the whole defect — so it refuses to start, and the pool says so
    ///     instead of waiting for a pipe that will never open.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Before this, a worker that exited before connecting hung its coordinator for
    ///     ever.</b> Nothing could exit that early until a worker had work to do at start-up, so a
    ///     wait on the pipe alone had never yet been wrong; loading a plugin is that work.
    /// </remarks>
    [Fact]
    public async Task APoolWhoseWorkerExitsBeforeConnectingFailsRatherThanWaiting() {
        // A command that is certainly present, certainly not a worker, and certainly exits.
        using var pool = new CompilerPool(project, workers: 1, ["dotnet", "--list-runtimes"]);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.ExecuteAsync(Job("RawImporter"), Cancellation)
        );

        Assert.Contains("before it connected", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An assembly that has moved since the coordinator loaded it is refused by name rather than
    ///     quietly leaving the worker one importer short.
    /// </summary>
    [Fact]
    public void AnImporterAssemblyThatIsNotThereIsRefusedByName() {
        var gone = Path.Combine(project, "Plugin", "gone.dll");

        var failure = Assert.Throws<InvalidOperationException>(() => PluginImporters.Load([gone]));

        Assert.Contains(gone, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ An importer with no file behind it cannot be named on a command line, so it is reported
    ///     rather than dropped: an asset it claims would fall through to the fallback in a worker and
    ///     succeed as a byte blob, which is the silent half of the same defect.
    /// </summary>
    [Fact]
    public void AnImporterFromAnAssemblyWithNoFileIsReportedAsUnreachable() {
        var contributions = new ImporterContributions();

        using (contributions.Add(FromMemory("Ghost", ".ghost"))) {
            Assert.Empty(PluginImporters.AssembliesBehind(contributions));
            Assert.Equal(["GhostPlugin.GhostImporter"], PluginImporters.Missing(contributions));
        }
    }

    /// <summary>A plugin with one importer, and the module initializer a real plugin's generator writes.</summary>
    /// <remarks>
    ///     ⚠ <b>The descriptor is registered by hand because this compilation has no generator
    ///     driver.</b> A plugin built by <c>dotnet build</c> gets
    ///     <c>Vixen.Core.Reflection.Generator</c> through its own <c>ProjectReference</c>, and this
    ///     initializer is what that generator emits. Without a descriptor an importer has no
    ///     <c>Name</c>, which is the one thing <c>PluginImporters</c> reads eagerly so that the
    ///     failure names the assembly rather than arriving from inside the registry.
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

          [DataContract("{{name}}Importer")]
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

    static CSharpCompilation Compilation(string name, string extension) =>
        CSharpCompilation.Create(
            name + "Plugin",
            [CSharpSyntaxTree.ParseText(Source(name, extension), new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

    static void Succeeded(EmitResult result) =>
        Assert.True(
            result.Success,
            "The test's own plugin source did not compile:\n"
            + string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        );

    /// <summary>Compiles a plugin to a file, which is the only way one can reach a worker.</summary>
    string WriteLibrary(string name, string extension) {
        var path = Path.Combine(project, "Plugin", name + "Plugin.dll");

        Succeeded(Compilation(name, extension).Emit(path));
        return path;
    }

    /// <summary>
    ///     Compiles a plugin into memory and loads it from there, which is an assembly with no
    ///     <c>Location</c> — the one shape a coordinator cannot name to a worker.
    /// </summary>
    static IAssetImporter FromMemory(string name, string extension) {
        using var bytes = new MemoryStream();

        Succeeded(Compilation(name, extension).Emit(bytes));
        bytes.Position = 0;

        var assembly = new AssemblyLoadContext(name + "-in-memory").LoadFromStream(bytes);
        var type = assembly.GetType($"{name}Plugin.{name}Importer", throwOnError: true)!;

        Assert.Empty(assembly.Location);

        return (IAssetImporter) Activator.CreateInstance(type)!;
    }

    static ImportJob Job(string importer) =>
        new(
            AssetId.New(),
            importer,
            new VirtualPath("/Assets/gadget.widget"),
            string.Empty,
            "Windows",
            EnforceDeclaredReads: true
        );

    static string Text(ExecutedImport import) =>
        Encoding.UTF8.GetString(Assert.Single(import.Artifacts).Content.Span);

    static string Why(ExecutedImport import) =>
        string.Join(
            "\n",
            import.Diagnostics.Select(diagnostic =>
                string.Create(CultureInfo.InvariantCulture, $"{diagnostic.Severity}: {diagnostic.Message}")
            )
        );

    static CancellationToken Cancellation => TestContext.Current.CancellationToken;
}
