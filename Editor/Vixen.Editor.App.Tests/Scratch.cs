// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.App.Tests;

/// <summary>A temporary directory that goes away with the test.</summary>
/// <remarks>
///     ⚠ <b>For the suites that need to <i>see</i> the user's data directory</b> — to write a
///     preferences file and restart over it, or to assert what a recent-projects list persisted.
///     <c>EditorSession</c> makes and deletes one of its own when it is not given a directory, which
///     is the right default and is why most suites never name one; this is for the cases that have
///     to put something in it first.
/// </remarks>
sealed class Scratch : IDisposable {
    public Scratch() {
        Directory = Path.Combine(Path.GetTempPath(), "vixen-editor-scratch", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    /// <summary>Where it is.</summary>
    public string Directory { get; }

    /// <inheritdoc />
    public void Dispose() {
        try {
            System.IO.Directory.Delete(Directory, recursive: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A temp directory that would not go is not a failed test.
        }
    }
}
