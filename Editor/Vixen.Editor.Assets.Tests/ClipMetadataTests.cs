// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Animation;
using Xunit;

namespace Tests;

/// <summary>The last of doc 34's seams: a project's own kind of clip metadata.</summary>
public sealed class ClipMetadataTests {
    /// <summary>
    ///     ⚠ <b>Registering a kind must never change whether it round-trips.</b> A project that adds
    ///     an extension and later removes the plugin has to get the same file back.
    /// </summary>
    [Fact]
    public async Task AKindNobodyReadsIsCarriedAndSaidSoAboutRatherThanDropped() {
        const string Yaml = """
            name: Reach
            duration: 1.0
            extensions:
              combat:
                cancelWindow: 0.4
                stagger: light
            """;

        var result = await Import(Yaml);

        // The only warning is the one every empty clip gets; nothing complains about the block.
        Assert.DoesNotContain(result.Diagnostics, entry => entry.Severity == ImportSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics, entry => entry.Message.Contains("combat", StringComparison.Ordinal) && entry.Severity == ImportSeverity.Warning);

        // Said so about — because "this kind is spelled wrong" and "this kind's plugin is not loaded"
        // look identical in a file.
        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Information && entry.Message.Contains("combat", StringComparison.Ordinal)
        );

        var clip = AnimationClipAsset.FromYaml(Yaml);

        Assert.Contains("cancelWindow", YamlWriter.Write(clip.Extensions["combat"]), StringComparison.Ordinal);
    }

    /// <summary>A registered kind is checked, which is the whole of what the seam adds.</summary>
    [Fact]
    public async Task ARegisteredKindIsCheckedAndABadOneFailsTheImport() {
        var good = await Import(
            "name: Reach\nduration: 1.0\nextensions:\n  notes:\n    - time: 0.4\n      text: weight on the back foot\n"
        );

        Assert.DoesNotContain(good.Diagnostics, entry => entry.Severity == ImportSeverity.Error);
        Assert.DoesNotContain(good.Diagnostics, entry => entry.Message.Contains("nothing in this build reads", StringComparison.Ordinal));

        var typo = await Import(
            "name: Reach\nduration: 1.0\nextensions:\n  notes:\n    - time: soon\n      text: weight on the back foot\n"
        );

        Assert.Contains(
            typo.Diagnostics,
            entry => entry.Severity == ImportSeverity.Error && entry.Message.Contains("not a number", StringComparison.Ordinal)
        );

        var silent = await Import("name: Reach\nduration: 1.0\nextensions:\n  notes:\n    - time: 0.4\n");

        Assert.Contains(silent.Diagnostics, entry => entry.Message.Contains("says nothing", StringComparison.Ordinal));
    }

    /// <summary>The second implementation, and it is nothing like the first.</summary>
    [Fact]
    public void ASecondKindPlugsIntoTheSameRegistry() {
        var registry = new ClipMetadataExtensions().Add(new BudgetTestExtension());

        Assert.Equal(1, registry.Count);
        Assert.Null(registry.For("notes"));

        var blocks = new Dictionary<string, YamlNode>(StringComparer.Ordinal) {
            ["budget"] = YamlReader.Read("bones: 40\n"),
            ["notes"] = YamlReader.Read("- text: whatever\n")
        };

        List<string> problems = [];
        List<string> unread = [];

        Assert.True(registry.Check(blocks, problems, unread));
        Assert.Empty(problems);

        // `notes` is not registered *in this registry*, which is what makes the set a set rather
        // than a global.
        Assert.Equal("notes", Assert.Single(unread));

        blocks["budget"] = YamlReader.Read("bones: far too many\n");

        Assert.False(registry.Check(blocks, problems));
        Assert.Contains(problems, problem => problem.Contains("a number of bones", StringComparison.Ordinal));
    }

    /// <summary>A per-clip budget a project's own build step would read. Nothing like a note.</summary>
    sealed class BudgetTestExtension : IClipMetadataExtension {
        public string Kind => "budget";

        public string Describe(YamlNode node) =>
            node is YamlMapping mapping && mapping["bones"] is YamlScalar bones ? $"{bones.Value} bones" : "no budget";

        public bool Validate(YamlNode node, ICollection<string> problems) {
            ArgumentNullException.ThrowIfNull(problems);

            if (node is not YamlMapping mapping || mapping["bones"] is not YamlScalar bones) {
                problems.Add("'budget' wants a 'bones' count.");
                return false;
            }

            if (int.TryParse(bones.Value, CultureInfo.InvariantCulture, out _)) {
                return true;
            }

            problems.Add($"'{bones.Value}' is not a number of bones.");
            return false;
        }
    }

    static async Task<ImportResult> Import(string text) {
        var path = new VirtualPath("/Assets/reach.vxanim");
        var files = new MemoryFileProvider();
        var importer = new AnimationClipImporter();

        files.Seed(path, text);

        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }
}
