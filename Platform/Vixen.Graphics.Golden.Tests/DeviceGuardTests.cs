// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Reflection;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>This suite's own device guard, asserted rather than copy-pasted and hoped for.</summary>
/// <remarks>
///     <para>
///         Every class here that opens a device carries its own five-line copy of the same guard —
///         open, or fail when <c>VIXEN_REQUIRE_VULKAN</c> promised a device and skip when it did not.
///         ⚠ <b>Carrying it was optional.</b> There is no base class and no analyzer, so a fixture
///         added without the guard does not skip on a machine with no driver: it fails there, and on
///         the CI leg that installed lavapipe it is indistinguishable from a guarded one. Nothing in
///         the tree would have said so.
///     </para>
///     <para>
///         That is this repository's oldest failure class pointed at its own instrument. The mirror
///         image has already been paid for here: eighteen golden files that <i>passed</i> rather than
///         skipped without a device, green for as long as nobody read the skip count. Ask what this
///         assembly prints on the day a fixture forgets the guard, and until this file existed the
///         answer was "whatever that fixture happened to do".
///     </para>
///     <para>
///         Three classes legitimately carry no guard —
///         <see cref="DistanceFieldDeviceTests" />, <see cref="ScreenProbeUpsampleCompileTests" /> and
///         <see cref="SharedUiShaderTests" /> compile shaders and read the plan back rather than
///         drawing. They are not exempted by name: they are simply classes that never reach
///         <c>Fixture.TryOpen</c>, and the day one of them draws, it names the door and this fails.
///     </para>
///     <para>
///         ⚠ <b>What makes the source check complete is the second test, not the first.</b> Matching
///         text for <c>Fixture.TryOpen</c> would be worth nothing if a fixture could get a device
///         some other way — so <see cref="OnlyTheFixtureOpensADevice" /> holds the door count at one.
///         <c>VulkanDevice</c>'s constructor is private and <c>TryCreate</c> is the only way through
///         it, so between the two tests every device in this assembly comes from a guarded caller.
///     </para>
///     <para>
///         The third test is the same argument about the machine that <i>has</i> a driver:
///         <see cref="EveryClassThatOpensADeviceIsSerialised" /> holds every device opener in the one
///         collection xunit will not run in parallel with itself.
///     </para>
///     <para>
///         ⚠ <b>The fourth is that argument again about the validation counter, and it is the one
///         that had been lost fifty-four times over.</b> <c>Fixture.TryOpen</c> resets
///         <c>VulkanDiagnostics</c> at the door precisely so that everything a fixture <em>builds</em>
///         is covered by the <c>ErrorCount</c> check it makes later (#1174). A second
///         <c>VulkanDiagnostics.Reset()</c> anywhere downstream throws that window away — the check
///         then reads a counter the harness zeroed after its own setup, and reports health it never
///         measured. #1186 named five such files; there were twenty-seven, and the fix is only stable
///         if the count is held at one. <see cref="OnlyTheFixtureResetsTheValidationCounter" /> holds
///         it.
///     </para>
/// </remarks>
public sealed class DeviceGuardTests {
    /// <summary>The door: the one call that produces a device, spelled as callers spell it.</summary>
    const string Door = "Fixture.TryOpen";

    /// <summary>The environment promise, which turns a skip into a failure.</summary>
    const string Promise = "VIXEN_REQUIRE_VULKAN";

    /// <summary>The stand-aside, without which a driverless machine passes rather than skips.</summary>
    const string Skip = "Assert.Skip";

    /// <summary>The call behind the door, which nothing but the fixture may make.</summary>
    const string Creation = "VulkanDevice.TryCreate";

    /// <summary>The counter reset, which nothing but the fixture's door may make.</summary>
    const string Zeroing = "VulkanDiagnostics.Reset";

    /// <summary>The collection every device opener belongs to, because two devices is one too many.</summary>
    const string Serialised = "Vulkan";

    /// <summary>This file, which is the one source that names all three needles and opens nothing.</summary>
    /// <remarks>
    ///     ⚠ Excluded by name, and it is the only exclusion. A check that reads source for a literal
    ///     matches its own definition of that literal — this class would otherwise report itself as
    ///     both a device opener and the second door, and it is neither.
    /// </remarks>
    const string Self = nameof(DeviceGuardTests) + ".cs";

    /// <summary>Every class that opens a device also checks the promise and skips.</summary>
    /// <remarks>
    ///     The census is reflection over this assembly rather than a directory walk, so a class the
    ///     walk failed to reach cannot pass by not being looked at; the guard itself is read out of
    ///     the source, because it is a shape in the source and no attribute records it.
    /// </remarks>
    [Fact]
    public void EveryClassThatOpensADeviceCarriesTheGuard() {
        var classes = TestClasses();

        // ⚠ The instrument, first. A census that found nothing agrees with itself perfectly, and a
        // renamed door would empty `opens` below while leaving every assertion in this test true.
        Assert.True(
            classes.Count >= 50,
            $"only {classes.Count} test classes were found in this assembly, and there are dozens. "
            + "The census is broken, not the suite."
        );

        var opens = new List<string>();

        foreach (var (type, source) in classes) {
            var name = type.Name;
            var text = File.ReadAllText(source);

            if (!text.Contains(Door, StringComparison.Ordinal)) {
                continue;
            }

            opens.Add(name);

            Assert.True(
                text.Contains(Promise, StringComparison.Ordinal),
                $"{name} calls `{Door}` and never names `{Promise}`, so on a machine with no driver it "
                + "stands aside silently instead of failing the leg that promised a device. Copy the "
                + "guard from GoldenImageTests."
            );

            Assert.True(
                text.Contains(Skip, StringComparison.Ordinal),
                $"{name} calls `{Door}` and never calls `{Skip}`, so without a device it returns green "
                + "rather than skipping — which is the eighteen-passing-goldens failure, again."
            );
        }

        Assert.True(
            opens.Count >= 40,
            $"only {opens.Count} classes were seen calling `{Door}`, and most of this suite draws. "
            + "The door has been renamed and this test now checks nothing."
        );
    }

    /// <summary>The fixture is the only thing in this assembly that creates a device.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the test above is a naming convention.</b> A fixture that reached for
    ///     <c>VulkanDevice.TryCreate</c> itself would open a device, never name the door, and be
    ///     waved through — unguarded, and looking exactly like the three that legitimately draw
    ///     nothing.
    /// </remarks>
    [Fact]
    public void OnlyTheFixtureOpensADevice() {
        var sources = Directory.GetFiles(ProjectDirectory(), "*.cs", SearchOption.TopDirectoryOnly);

        Assert.True(sources.Length >= 50, $"{sources.Length} sources under '{ProjectDirectory()}' is not this project.");

        var creators = sources
            .Where(path => !string.Equals(Path.GetFileName(path), Self, StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(Creation, StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Fixture.cs"], creators);
    }

    /// <summary>The fixture's door is the only thing in this assembly that zeroes the counter.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A harness that resets after its own setup is #1174 again, one file at a time.</b>
    ///         Every <c>ErrorCount</c> check in this assembly reads a counter it did not zero, and
    ///         what makes that check mean anything is that the zeroing happened at
    ///         <c>Fixture.TryOpen</c> — before the textures, buffers, pipelines and descriptor sets
    ///         the harness goes on to build. A second reset moves the window's start past all of
    ///         them, so the check afterwards is true of a counter the harness set to zero itself and
    ///         says nothing at all about construction. That is an instrument reporting health it
    ///         never measured, and it survived #1174 in twenty-seven files.
    ///     </para>
    ///     <para>
    ///         <b>The instrument check comes first.</b> "Nowhere resets" satisfies "only the fixture
    ///         resets" — so a tree where the door itself stopped resetting is a tree this test would
    ///         wave through while every <c>ErrorCount</c> check in the assembly quietly became
    ///         cumulative across its whole class. The door is asserted to still carry the call before
    ///         anything else is asked.
    ///     </para>
    /// </remarks>
    [Fact]
    public void OnlyTheFixtureResetsTheValidationCounter() {
        var sources = Directory.GetFiles(ProjectDirectory(), "*.cs", SearchOption.TopDirectoryOnly);

        Assert.True(sources.Length >= 50, $"{sources.Length} sources under '{ProjectDirectory()}' is not this project.");

        // ⚠ The instrument, first. A tree where nothing resets passes the census below perfectly.
        Assert.True(
            File.ReadLines(Path.Combine(ProjectDirectory(), "Fixture.cs")).Any(Calls),
            $"`Fixture.cs` no longer calls `{Zeroing}`, so nothing zeroes the counter at the door and "
            + "every `ErrorCount` check in this assembly is now cumulative across its whole class. "
            + "The census below would have passed on that tree, which is the reason this line is above it."
        );

        var zeroing = sources
            .Where(path => !string.Equals(Path.GetFileName(path), Self, StringComparison.Ordinal))
            .Where(path => File.ReadLines(path).Any(Calls))
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            zeroing.Length == 1 && string.Equals(zeroing[0], "Fixture.cs", StringComparison.Ordinal),
            $"`{Zeroing}` is called from {string.Join(", ", zeroing)}. It may only be called from the "
            + $"door, `{Door}`: a harness that resets after building its own resources moves the "
            + "start of the window past them, and the `ErrorCount` check it makes afterwards then "
            + "reports a counter the harness zeroed itself. That is #1174, per file."
        );
    }

    /// <summary>Whether a line of source calls the reset, rather than merely naming it.</summary>
    /// <remarks>
    ///     ⚠ Comment lines are skipped, and that is not fussiness: <c>FixtureTextureViewTests</c>'
    ///     remarks explain the door's reset by spelling the call, and a whole-file <c>Contains</c>
    ///     would report the file that documents the rule as the file that breaks it.
    /// </remarks>
    static bool Calls(string line) {
        var trimmed = line.TrimStart();

        return !trimmed.StartsWith("//", StringComparison.Ordinal)
            && trimmed.Contains($"{Zeroing}()", StringComparison.Ordinal);
    }

    /// <summary>Every class that opens a device is in the one collection that runs serially.</summary>
    /// <remarks>
    ///     <para>
    ///         The guard above is about a machine with <em>no</em> device; this one is about a machine
    ///         with one. xunit parallelises across collections and never within one, so
    ///         <c>[Collection("<see cref="Serialised" />")]</c> is the whole of what keeps two fixtures
    ///         from holding a device at once — and <c>VulkanDiagnostics</c> is process-wide, so the
    ///         second fixture's validation errors are attributed to whichever frame happens to call
    ///         <c>Fail</c> first. That failure names the wrong test, in a suite where the message is
    ///         the entire diagnostic.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two classes had escaped it</b> — <see cref="VirtualGeometryDeviceTests" /> and
    ///         <see cref="VirtualGeometryGoldenTests" />, the two newest device openers in the
    ///         assembly, which is exactly how an attribute nothing enforces is lost. Fifty-six files
    ///         carrying it by hand is a convention, and a convention is what this replaces.
    ///     </para>
    ///     <para>
    ///         By attribute rather than by source text, unlike the guard: the collection <i>is</i>
    ///         recorded by an attribute, so reading the source for one would be checking the spelling
    ///         of the thing rather than the thing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryClassThatOpensADeviceIsSerialised() {
        var classes = TestClasses();

        // ⚠ The instrument, first — the same reason as above. A census of nothing serialises perfectly.
        Assert.True(
            classes.Count >= 50,
            $"only {classes.Count} test classes were found in this assembly, and there are dozens. "
            + "The census is broken, not the suite."
        );

        var serialised = 0;

        foreach (var (type, source) in classes) {
            if (!File.ReadAllText(source).Contains(Door, StringComparison.Ordinal)) {
                continue;
            }

            var collection = type
                .GetCustomAttributes(true)
                .FirstOrDefault(attribute => attribute.GetType().Name is "CollectionAttribute");

            var name = collection?.GetType().GetProperty("Name")?.GetValue(collection) as string;

            Assert.True(
                string.Equals(name, Serialised, StringComparison.Ordinal),
                $"{type.Name} calls `{Door}` and is in collection '{name ?? "<none>"}' rather than "
                + $"'{Serialised}', so it opens a device while the serialised collection has one. "
                + $"VulkanDiagnostics is process-wide: the next validation error will be reported "
                + "against whichever fixture reads it first, which is not the one that caused it."
            );

            serialised++;
        }

        Assert.True(
            serialised >= 40,
            $"only {serialised} classes were seen calling `{Door}`, and most of this suite draws. "
            + "The door has been renamed and this test now checks nothing."
        );
    }

    /// <summary>Every public class in this assembly that declares a fact, with the file that declares it.</summary>
    /// <remarks>
    ///     One class per file is the convention here and this depends on it, so it asserts it: a class
    ///     with no file of its own is a failure rather than a class quietly left unchecked.
    /// </remarks>
    static List<(Type Type, string Source)> TestClasses() {
        var directory = ProjectDirectory();
        var found = new List<(Type, string)>();

        foreach (var type in typeof(DeviceGuardTests).Assembly.GetTypes()) {
            if (type == typeof(DeviceGuardTests) || type.IsNested || type.IsAbstract || !type.IsClass) {
                continue;
            }

            var facts = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(method => method.GetCustomAttributes(true).Any(IsFact));

            if (!facts) {
                continue;
            }

            var source = Path.Combine(directory, type.Name + ".cs");

            Assert.True(
                File.Exists(source),
                $"{type.Name} declares facts and there is no {type.Name}.cs beside its siblings. One class "
                + "per file is what lets this check read a class's guard at all; a second class hiding in "
                + "another file's tail is a class nothing here looks at."
            );

            found.Add((type, source));
        }

        return found;
    }

    /// <summary>Whether an attribute is what makes a method a test.</summary>
    /// <remarks>
    ///     By base type <i>and</i> by name. <c>TheoryAttribute</c> derives from <c>FactAttribute</c>
    ///     today, and a census that silently depended on that would stop seeing every theory in the
    ///     assembly the day it did not — without failing.
    /// </remarks>
    static bool IsFact(object attribute) =>
        attribute is FactAttribute || attribute.GetType().Name is "FactAttribute" or "TheoryAttribute";

    /// <summary>This project's own directory, found by walking up rather than by counting directories.</summary>
    /// <remarks>
    ///     ⚠ Never a walk from the repository root. <c>.claude/worktrees</c> holds a full checkout per
    ///     parallel agent, so a search for these file names from above finds other branches' copies of
    ///     them and reports on a tree nobody is editing.
    /// </remarks>
    static string ProjectDirectory() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Platform", "Vixen.Graphics.Golden.Tests");

            if (File.Exists(Path.Combine(candidate, "Fixture.cs"))) {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"this project's sources were not found above '{AppContext.BaseDirectory}'.");
    }
}
