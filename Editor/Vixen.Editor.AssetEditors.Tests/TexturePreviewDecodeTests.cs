// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.Assets.Textures;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The import panel's preview decode does not stand in the way of the artist saving the file.</summary>
/// <remarks>
///     <b><a href="https://github.com/Rikarin/Vixen/issues/1471">#1471</a>, which is #1326 and #1432
///     one assembly along.</b> <see cref="TextureLadder.TryDecode(string, out string?)" /> decoded off
///     <c>File.OpenRead</c>, which shares for reading only, and held that handle for the whole
///     decode — so on Windows a paint program saving over the texture while its import panel
///     decoded the preview got a sharing violation. Share modes are advisory on Linux and macOS, so
///     there the save test passes against the old read too.
/// </remarks>
public sealed class TexturePreviewDecodeTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("vixen-preview-decode-").FullName;

    /// <inheritdoc />
    public void Dispose() {
        try {
            Directory.Delete(folder, recursive: true);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }

    /// <summary>⚠ Saving over a texture while its preview decodes succeeds, and the preview still comes back.</summary>
    [Fact]
    public void A_save_over_the_file_during_its_preview_decode_is_not_refused() {
        var file = Path.Combine(folder, "Saved.png");

        File.WriteAllBytes(file, [1, 2, 3, 4]);

        var picture = TextureLadder.TryDecode(file, [new SavingDecoder(file)], out var reason);

        Assert.Null(reason);
        Assert.NotNull(picture);
        Assert.Equal([5, 6, 7, 8], File.ReadAllBytes(file));
    }

    /// <summary>A truncated PNG through the real codec is a sentence, not a throw.</summary>
    /// <remarks>
    ///     StbImageSharp answers a PNG whose header is cut short with
    ///     <c>InvalidOperationException("unknown image type")</c>. This site's net already named that
    ///     type; the test keeps it named once the codec's answer is translated at the read.
    /// </remarks>
    [Fact]
    public void A_truncated_png_is_a_sentence() {
        var file = Path.Combine(folder, "Torn.png");

        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47, 1, 2]);

        Assert.Null(TextureLadder.TryDecode(file, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
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
