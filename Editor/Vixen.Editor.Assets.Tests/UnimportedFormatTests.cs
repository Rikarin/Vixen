// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     That the fallback says something about an extension somebody meant something by, and says
///     nothing about one nobody did.
/// </summary>
/// <remarks>
///     <para>
///         <b>The defect this is the instrument for.</b> <see cref="RawImporter" /> takes anything
///         nothing else claimed, writes a chunk called <c>Blob</c>, and succeeds. That is correct for
///         a CSV and wrong for a <c>.vxfont</c> the editor's own Create menu wrote — and from outside
///         the two are the same three facts: succeeded, one artefact, no diagnostics. Seven formats
///         have shipped through that hole.
///     </para>
///     <para>
///         ⚠ <b>Both halves are asserted, because only one of them is the gate.</b> "It warns about
///         <c>.vxfont</c>" is satisfied by an importer that warns about everything, which would make
///         every project's log two hundred lines of noise and teach its reader to skip
///         <c>VX1001</c> — the exact failure <c>Vixen.Cli</c>'s <c>DiagnosticWriter</c> remarks name.
///         So <see cref="AFormatNobodyDeclaredIsStillTakenInSilence" /> asserts the silence that makes
///         the sentence worth reading.
///     </para>
/// </remarks>
public sealed class UnimportedFormatTests {
    /// <summary>Runs the fallback over a file with the given extension.</summary>
    /// <param name="extension">The extension, with its dot.</param>
    /// <returns>What it produced.</returns>
    static async Task<ImportResult> ImportAsFallback(string extension) {
        var path = new VirtualPath("/Assets/Thing" + extension);
        var files = new MemoryFileProvider();

        files.Seed(path, "whatever is in it");

        // ⚠ A contribution set of its own, not ImporterContributions.Default: the default is
        // process-wide and ImporterContributionTests mutates it, so reading it here would race.
        var registry = BuiltInImporters.Create(new ImporterContributions());

        Assert.True(registry.TryGetForFile(path.ToString(), out var importer));

        // The property under test only means anything if the fallback is what claimed it. An
        // extension that has since grown a real importer would otherwise be "tested" against
        // somebody else's diagnostics.
        Assert.IsType<RawImporter>(importer);

        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }

    /// <summary>Every extension the table names is one no real importer claims.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what stops the table going stale, and it is the half that will fail first.</b>
    ///     The moment somebody writes the <c>FontImporter</c> doc 08's table promises, <c>.ttf</c>
    ///     stops reaching the fallback — and a row still saying "there is none" would then be a lie
    ///     printed against nothing, which is worse than no row. Whoever adds the importer is told to
    ///     delete the row in the same change.
    /// </remarks>
    [Fact]
    public void EveryExtensionTheTableNamesStillFallsThroughToTheFallback() {
        var registry = BuiltInImporters.Create(new ImporterContributions());

        foreach (var extension in UnimportedFormats.Extensions) {
            Assert.True(registry.TryGetForFile("Assets/Thing" + extension, out var importer), extension);

            Assert.True(
                importer is RawImporter,
                $"'{extension}' is in UnimportedFormats and is now claimed by {importer.GetType().Name}. Delete "
                + "its row: a sentence saying nothing imports this, printed by an importer that does, is worse "
                + "than no sentence."
            );
        }
    }

    /// <summary>A format handled elsewhere is a note, not a warning.</summary>
    /// <remarks>
    ///     ⚠ <b>An <c>.rvn</c> under <c>Assets/</c> is a supported arrangement, not a mistake.</b>
    ///     <c>EditorEffects</c> enumerates exactly that directory for shaders and compiles what it
    ///     finds, so a project full of them is correct today — and a warning per file would be a
    ///     clean build with fifty warnings on it. What the reader is owed is the fact that the asset
    ///     database is not what read it.
    /// </remarks>
    [Theory]
    [InlineData(".rvn", "ShaderImporter")]
    [InlineData(".vxml", "MarkupImporter")]
    [InlineData(".vcss", "StyleImporter")]
    [InlineData(".cs", "ScriptImporter")]
    [InlineData(".ttf", "FontImporter")]
    [InlineData(".otf", "FontImporter")]
    [InlineData(".woff2", "FontImporter")]
    public async Task AFormatDocEightPromisesAnImporterForSaysWhatReadsItInstead(string extension, string promised) {
        var result = await ImportAsFallback(extension);

        // Still bytes, still addressable: the sentence is added and nothing is taken away.
        Assert.True(result.Succeeded);
        Assert.Equal("Blob", Assert.Single(result.Artifacts).Type);

        var diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal(ImportSeverity.Information, diagnostic.Severity);
        Assert.Contains(promised, diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>A format nothing at all handles is a warning, and names what is unreachable.</summary>
    /// <remarks>
    ///     ⚠ <b>Five of these are asset kinds this editor's own Create menu writes</b> —
    ///     <c>EditorWorlds.BuiltInAssetKinds</c> has a line for each — and
    ///     <c>CreateAssetMenuAttribute</c>'s remarks state the contract they break: "a file with an
    ///     extension that an importer claims". Authored by the editor, unreadable by the build, and
    ///     until now silent about it.
    /// </remarks>
    [Theory]
    [InlineData(".vxfont")]
    [InlineData(".vxanimgraph")]
    [InlineData(".vxseq")]
    [InlineData(".vxmixer")]
    [InlineData(".vxshadergraph")]
    [InlineData(".vxplacement")]
    public async Task AFormatNothingHandlesIsAWarning(string extension) {
        var result = await ImportAsFallback(extension);

        // ⚠ Succeeded, deliberately. A build that failed on these would fail on every project that
        // has ever opened the Create menu, and the point is to name the gap rather than to close the
        // editor over it.
        Assert.True(result.Succeeded);

        var diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal(ImportSeverity.Warning, diagnostic.Severity);
        Assert.Contains(extension, diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>And the fallback is still a shrug for a format nobody declared.</summary>
    /// <remarks>
    ///     ⚠ <b>The control, and the assertion that keeps the others meaningful.</b> A table that
    ///     matched everything would make every one of the theories above pass while destroying the
    ///     thing <see cref="RawImporter" /> is for: shipping a CSV, a licence file or a format the
    ///     engine has never heard of, without ceremony. If this goes red, the diagnostics above are
    ///     noise rather than information.
    /// </remarks>
    [Theory]
    [InlineData(".csv")]
    [InlineData(".txt")]
    [InlineData(".license")]
    [InlineData(".sqlite")]
    public async Task AFormatNobodyDeclaredIsStillTakenInSilence(string extension) {
        var result = await ImportAsFallback(extension);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("Blob", Assert.Single(result.Artifacts).Type);
    }

    /// <summary>The bytes are the author's, whatever was said about them.</summary>
    [Fact]
    public async Task TheBlobIsStillTheFileVerbatim() {
        var path = new VirtualPath("/Assets/Theme.vcss");
        var files = new MemoryFileProvider();
        const string Sample = ".button { color: red }";

        files.Seed(path, Sample);

        var registry = BuiltInImporters.Create(new ImporterContributions());

        Assert.True(registry.TryGetForFile(path.ToString(), out var importer));

        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");
        var result = await importer.ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(Sample, Encoding.UTF8.GetString(Assert.Single(result.Artifacts).Content.Span));
    }
}
