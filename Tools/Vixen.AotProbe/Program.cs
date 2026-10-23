// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// The gate's subject is the assemblies, not this file. Every runtime assembly is rooted in the
// project file, so ILC compiles all of them and reports everything it cannot compile ahead of
// time — which is the question the phase asks.
//
// ⚠ And with `--smoke` this file is the half of the gate that is about the binary rather than the
// compiler (#1255). `CheckAot` runs the native binary it just published with that argument and
// fails unless it exits 0 having printed `SmokePassed` exactly. "ILC did not complain" and "the
// engine works ahead of time" are different claims: #1240 measured a publish that warned once and
// then answered a reflective question wrongly at run time, with nothing in the file's size or shape
// to say so. Each check below is closed-form — it names the answer it must get — so a binary that
// runs and computes the wrong thing exits non-zero rather than printing a counter.

using Vixen.Core;
using Vixen.Ui;

if (args is ["--smoke"]) {
    return Smoke.Run();
}

Console.WriteLine($"Vixen AOT probe: {ObjectId.Empty}");
return 0;

/// <summary>The closed-form checks <c>CheckAot</c> runs the native binary for.</summary>
static class Smoke {
    /// <summary>What the binary prints when every check held. <c>CheckAot</c> matches it exactly.</summary>
    public const string Passed = "SmokePassed";

    /// <summary>Runs every check, printing each failure, and answers the process exit code.</summary>
    public static int Run() {
        var failures = new List<string>();

        ABaseTypesPropertiesReachALeafThatDeclaresNone(failures);

        foreach (var failure in failures) {
            Console.Error.WriteLine($"SmokeFailed: {failure}");
        }

        if (failures.Count > 0) {
            return 1;
        }

        Console.WriteLine(Passed);
        return 0;
    }

    /// <summary>
    ///     ⚠ #1240's exact shape, which a NativeAOT publish once got wrong while every file-level
    ///     check passed: <c>UiPropertyRegistry.Of</c> on a leaf that declares no property must answer
    ///     its base's property and <see cref="UiElement" />'s own, from two links up and another
    ///     assembly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two types live in this assembly because it is the one that is not rooted.</b>
    ///         The SDK adds the entry assembly as a <c>TrimmerRootAssembly</c> with
    ///         <c>RootMode="EntryPoint"</c>, which ILC's targets exclude from <c>--root</c>, so only
    ///         what <c>Main</c> reaches survives — and nothing here names <see cref="ProbeBase" />.
    ///         Its class constructor is reachable only through the registry's
    ///         <c>Type.BaseType</c> walk, which is what is under test. Declared inside a rooted
    ///         assembly, the constructor would be kept whatever the walk did.
    ///     </para>
    ///     <para>
    ///         The registration is what the <c>[UiProperty]</c> generator emits — a static field
    ///         initialised by <see cref="UiPropertyRegistry.Register" /> under an explicit static
    ///         constructor — written out because the probe takes no analyzer reference, and an
    ///         analyzer here would be one more assembly <c>CheckAot</c> asks to be rooted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The suppression is a filed defect, not a convenience</b> (#1524). The first
    ///         publish of this method was <c>IL3050</c> on <c>System.Enum.GetValues(Type)</c> at both
    ///         calls: <c>Of</c>'s <c>DynamicallyAccessedMemberTypes.All</c> reaches a nested enum of
    ///         <see cref="UiElement" /> and through it <c>System.Enum</c>. No other caller of
    ///         <c>Of</c> is ever published, so this probe is the first place it could show. What is
    ///         under test here is the answer at run time, which the warning does not change; delete
    ///         the attribute when #1524 is fixed.
    ///     </para>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "#1524: Of's All annotation reaches System.Enum.GetValues(Type) through a nested enum of UiElement."
    )]
    static void ABaseTypesPropertiesReachALeafThatDeclaresNone(List<string> failures) {
        var leaf = UiPropertyRegistry.Of(typeof(ProbeLeaf)).Select(key => key.Name).ToHashSet(StringComparer.Ordinal);
        var element = UiPropertyRegistry.Of(typeof(UiElement)).Select(key => key.Name).ToList();

        // The instrument first: an element with no properties would make the subset check vacuous.
        if (element.Count == 0) {
            failures.Add("UiPropertyRegistry.Of(typeof(UiElement)) answered nothing, so the check below would hold vacuously.");
        }

        if (!leaf.Contains(ProbeBase.WeightName)) {
            failures.Add(
                $"UiPropertyRegistry.Of(typeof(ProbeLeaf)) lacks {ProbeBase.WeightName}, its base's only property: "
                + "the walk did not run ProbeBase's class constructor in this binary (#1240)."
            );
        }

        foreach (var name in element.Where(name => !leaf.Contains(name))) {
            failures.Add($"UiPropertyRegistry.Of(typeof(ProbeLeaf)) lacks UiElement's {name}.");
        }
    }
}

/// <summary>A base that declares one property, and is named by nothing but its leaf's base list.</summary>
class ProbeBase : UiElement {
    /// <summary>Its only property's name.</summary>
    public const string WeightName = "ProbeWeight";

    /// <summary>The registration, as the generator writes it.</summary>
    public static readonly UiPropertyKey WeightProperty = UiPropertyRegistry.Register(
        WeightName,
        typeof(ProbeBase),
        typeof(int),
        false,
        static element => ((ProbeBase) element).Weight,
        static (element, value) => ((ProbeBase) element).Weight = (int) value!
    );

    /// <summary>Explicit, so the class is not <c>beforefieldinit</c> — the generator's reason.</summary>
    static ProbeBase() { }

    /// <summary>The property.</summary>
    public int Weight { get; set; } = 11;
}

/// <summary>Declares nothing, so nothing registers for it: the shape #1240 answered wrongly.</summary>
sealed class ProbeLeaf : ProbeBase;
