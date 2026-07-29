// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>A throwaway project on disk, with an <see cref="EditorProject" /> open over it.</summary>
/// <remarks>
///     Real files, for <c>ProjectFixture</c>'s reason: everything worth testing about a document that
///     edits a sidecar is about what ends up in the file, and a fake filesystem would only prove the
///     fake works.
/// </remarks>
public sealed class EditorFixture : IDisposable {
    /// <summary>Where the project's directories are.</summary>
    public ProjectPaths Paths { get; }

    /// <summary>The project, already opened.</summary>
    public EditorProject Project { get; }

    /// <summary>Creates an empty project in the temporary directory.</summary>
    public EditorFixture() {
        Paths = new(Path.Combine(Path.GetTempPath(), "vixen-asset-editors", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Paths.Assets);

        Project = new(Paths);
        Project.Open();
    }

    /// <summary>Writes a file under <c>Assets/</c> and returns where it landed.</summary>
    /// <param name="relativePath">Where, under the project root.</param>
    /// <param name="content">What is in it.</param>
    /// <returns>The absolute path.</returns>
    public string Write(string relativePath, string content) {
        var absolute = Paths.Absolute(relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);

        return absolute;
    }

    /// <summary>Writes a file and a sidecar for it, and returns the absolute path.</summary>
    /// <param name="relativePath">Where, under the project root.</param>
    /// <param name="content">What is in the asset.</param>
    /// <param name="meta">What is in the sidecar, or <see langword="null" /> for none.</param>
    /// <returns>The absolute path.</returns>
    public string WriteAsset(string relativePath, string content, string? meta = null) {
        var absolute = Write(relativePath, content);

        if (meta is not null) {
            File.WriteAllText(absolute + ".meta", meta);
        }

        return absolute;
    }

    /// <summary>Reads a file back.</summary>
    /// <param name="path">Its absolute path.</param>
    /// <returns>The text.</returns>
    public static string Read(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public void Dispose() {
        try {
            if (Directory.Exists(Paths.Root)) {
                Directory.Delete(Paths.Root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }
}
