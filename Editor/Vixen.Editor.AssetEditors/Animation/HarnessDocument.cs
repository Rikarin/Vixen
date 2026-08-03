// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>A declared variation run, open for editing and for running.</summary>
/// <remarks>
///     <para>
///         The thresholds are the reason the file exists: numbers written into a test are numbers the
///         person authoring the clip cannot see and will not believe. Editing them is what this
///         document is for; running the plan is what makes the numbers mean something while they are
///         being chosen.
///     </para>
///     <para>
///         ⚠ <b>The clip, the rig and the shapes are supplied, not loaded.</b> The plan names them by
///         path and this document cannot reach another asset — the same split
///         <see cref="ProxyShapeDocument" /> makes. A host that has a project wires
///         <see cref="Resolve" />; without one the plan is still editable and
///         <see cref="TryRun" /> says what it is missing rather than throwing.
///     </para>
/// </remarks>
public sealed class HarnessDocument : EditorDocument {
    /// <summary>What a declared run is written as.</summary>
    public const string Extension = HarnessPlanContent.Extension;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The declaration.</summary>
    public HarnessPlanContent Plan { get; }

    /// <summary>Why the file would not read, or <see langword="null" />.</summary>
    public string? LoadError { get; }

    /// <summary>How the plan's asset paths are turned into the things it names.</summary>
    /// <remarks>
    ///     Answers <see langword="null" /> for anything it cannot find, which is what
    ///     <see cref="TryRun" /> reports rather than guessing around.
    /// </remarks>
    public Func<HarnessPlanContent, HarnessInputs?>? Resolve { get; set; }

    /// <summary>The last run, or <see langword="null" /> if there has not been one.</summary>
    public VariationReport? Report { get; private set; }

    /// <summary>The plan the last run used, which is what a drill-down rebuilds against.</summary>
    public HarnessPlan? Ran { get; private set; }

    /// <summary>Raised after anything changes the plan or a run finishes.</summary>
    public event Action<HarnessDocument>? Changed;

    /// <summary>Opens a plan.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public HarnessDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        try {
            var text = AssetFile.Read(path);

            Plan = text.Trim().Length == 0 ? new() : YamlSerializer.Parse<HarnessPlanContent>(text);
        } catch (Exception exception) when (exception is YamlBindingException or YamlParseException) {
            Plan = new();
            LoadError = exception.Message;
        }

        if (Plan.Name.Length == 0) {
            Plan.Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>Changes one of the plan's numbers, undoably.</summary>
    /// <typeparam name="T">What kind of value.</typeparam>
    /// <param name="label">What the undo entry is called.</param>
    /// <param name="read">How to read the field.</param>
    /// <param name="write">How to write it.</param>
    /// <param name="value">What to write.</param>
    public void Set<T>(string label, Func<HarnessPlanContent, T> read, Action<HarnessPlanContent, T> write, T value) {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        var previous = read(Plan);

        if (EqualityComparer<T>.Default.Equals(previous, value)) {
            return;
        }

        Run("Edit " + label, () => write(Plan, value), () => write(Plan, previous));
    }

    /// <summary>Runs the plan, if everything it names could be found.</summary>
    /// <param name="why">What was missing, when it could not run.</param>
    /// <returns>Whether it ran.</returns>
    /// <remarks>
    ///     ⚠ <b>Synchronous, and the caller decides where from.</b> A run is seconds of solving on a
    ///     body range; a document that started a task would own a cancellation, a progress report and
    ///     a re-entrancy question that belong to whoever has a window.
    /// </remarks>
    public bool TryRun(out string why) {
        if (Resolve is not { } resolve) {
            why = "No project is bound, so the clip and the rig this plan names could not be loaded.";
            return false;
        }

        if (resolve(Plan) is not { } inputs) {
            why = $"'{Plan.Clip}' or '{Plan.Rig}' could not be loaded.";
            return false;
        }

        Ran = Plan.Resolve(inputs.Skeleton, inputs.Clip, inputs.Shapes, inputs.Ladder);
        Report = VariationHarness.Run(Ran);

        why = string.Empty;
        Changed?.Invoke(this);

        return true;
    }

    void Run(string label, Action apply, Action revert) {
        Stack.Execute(
            new DelegateCommand(
                label,
                _ => {
                    apply();
                    Changed?.Invoke(this);
                },
                _ => {
                    revert();
                    Changed?.Invoke(this);
                }
            )
        );
    }

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, YamlSerializer.ToYaml(Plan));
}

/// <summary>What a plan names, once somebody with a project has found it.</summary>
/// <param name="Skeleton">The rig.</param>
/// <param name="Clip">The clip, in its authored form.</param>
/// <param name="Shapes">Its proxy shapes, or <see langword="null" />.</param>
/// <param name="Ladder">The priority ladder its tags name, or <see langword="null" />.</param>
public readonly record struct HarnessInputs(
    Skeleton Skeleton,
    AnimationClipContent Clip,
    ProxyShapeSet? Shapes = null,
    PriorityLadder? Ladder = null
);
