// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Textures;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>A bake's picture read answers every unreadable file with a sentence, and holds no file open across a decode.</summary>
/// <remarks>
///     <b><a href="https://github.com/Rikarin/Vixen/issues/1471">#1471</a>.</b>
///     <c>TextureGraphRunner.Decoded</c> had both of #1432's defects: it filtered on five exception
///     types, which misses StbImageSharp's <c>InvalidOperationException("unknown image type")</c> for
///     a truncated PNG, and it decoded off <c>File.OpenRead</c>, whose read-only share was held for
///     the length of the decode.
/// </remarks>
public sealed class TextureGraphRunnerDecodeTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("vixen-cli-decode-").FullName;

    /// <inheritdoc />
    public void Dispose() {
        try {
            Directory.Delete(folder, recursive: true);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }

    /// <summary>⚠ A truncated PNG through the codec the bake uses is the sentence the remark promises, not a throw.</summary>
    [Fact]
    public void A_truncated_png_is_a_sentence_rather_than_a_throw() {
        var file = Path.Combine(folder, "Torn.png");

        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47, 1, 2]);

        var (picture, unreadable) = TextureGraphRunner.Decoded(file, ImageDecoders.BuiltIn);

        Assert.Null(picture);
        Assert.False(string.IsNullOrEmpty(unreadable), "a refusal with no sentence names nothing for the build log.");
    }

    /// <summary>⚠ Saving over a picture while the bake decodes it succeeds.</summary>
    /// <remarks>Share modes are advisory on Linux and macOS, so there this passes against the old read too.</remarks>
    [Fact]
    public void A_save_over_the_file_during_its_decode_is_not_refused() {
        var file = Path.Combine(folder, "Saved.png");

        File.WriteAllBytes(file, [1, 2, 3, 4]);

        var (picture, unreadable) = TextureGraphRunner.Decoded(file, [new SavingDecoder(file)]);

        Assert.Null(unreadable);
        Assert.NotNull(picture);
        Assert.Equal([5, 6, 7, 8], File.ReadAllBytes(file));
    }

    /// <summary>A decoder that saves over its own file mid-decode, as an artist's paint program would.</summary>
    /// <param name="path">The file to write over.</param>
    sealed class SavingDecoder(string path) : IImageDecoder {
        /// <inheritdoc />
        public IReadOnlyList<string> Extensions { get; } = [".png"];

        /// <inheritdoc />
        public TextureData Decode(Stream stream, string extension) {
            File.WriteAllBytes(path, [5, 6, 7, 8]);

            return new(PixelFormat.Rgba8UNorm, 2, 2, levelCount: 1);
        }
    }
}
