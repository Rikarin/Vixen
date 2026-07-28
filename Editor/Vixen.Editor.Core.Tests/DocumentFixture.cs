// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Core.Tests;

/// <summary>A project with nothing on disk, for the tests that are about the model and not the files.</summary>
/// <remarks>
///     Constructing an <see cref="EditorProject" /> reads nothing — that is <see cref="EditorProject.Open" />
///     — so the document model can be exercised against a path that does not exist, which is what
///     keeps these tests measured in microseconds.
/// </remarks>
public static class ModelFixture {
    /// <summary>A project over a path nobody will touch.</summary>
    /// <returns>The project.</returns>
    public static EditorProject Project() => new(new(Path.Combine(Path.GetTempPath(), "vixen-model-tests")));
}

/// <summary>A document that counts how often it was asked to write itself back.</summary>
public sealed class TestDocument : EditorDocument {
    /// <summary>How many times <see cref="EditorDocument.Save" /> reached the deriving type.</summary>
    public int Saves { get; private set; }

    /// <summary>Opens a document in a project.</summary>
    /// <param name="project">The project.</param>
    /// <param name="title">What the tab says.</param>
    /// <param name="asset">The asset it edits.</param>
    public TestDocument(EditorProject project, string title = "Untitled", AssetId asset = default)
        : base(project, asset, title) {
    }

    /// <inheritdoc />
    protected override void SaveCore() => Saves++;
}

/// <summary>An object with one coalescing property and one that is not.</summary>
public sealed class Knob : EditorObject {
    /// <summary>A value a slider would drag: consecutive edits collapse.</summary>
    public EditorProperty<float> Amount { get; }

    /// <summary>A value a dropdown would set: two edits are two decisions.</summary>
    public EditorProperty<string> Label { get; }

    /// <summary>Creates the object.</summary>
    /// <param name="document">Where its edits are recorded, if anywhere.</param>
    public Knob(EditorDocument? document) : base(document) {
        Amount = Property("Amount", 0f);
        Label = Property("Label", "none", coalescesEdits: false);
    }
}
