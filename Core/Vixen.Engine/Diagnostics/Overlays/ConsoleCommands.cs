// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Vixen.Engine.Diagnostics.Overlays;

/// <summary>Marks a static method as a console command.</summary>
/// <remarks>
///     <para>
///         The declaration [13](../../../../docs/plan/13-diagnostics.md) § Diagnostic overlays asks
///         for. What finds them is <see cref="ConsoleCommands.RegisterFrom(Assembly)" />, which uses
///         reflection and says so — see its remarks for why that is the honest shape today and what
///         replaces it.
///     </para>
///     <para>
///         The method must be <c>static</c> and take a single <see cref="ConsoleContext" />.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConsoleCommandAttribute : Attribute {
    /// <summary>Declares a command.</summary>
    /// <param name="name">What is typed to run it.</param>
    public ConsoleCommandAttribute(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>What is typed to run it.</summary>
    public string Name { get; }

    /// <summary>One line saying what it does, shown by <c>help</c>.</summary>
    public string Help { get; set; } = string.Empty;
}

/// <summary>What a command is handed: its arguments, and somewhere to answer.</summary>
/// <remarks>
///     A class rather than a struct because a command may hold onto it — a command that starts
///     something and reports later is a normal thing to want — and because the argument list has to
///     outlive the call for that to work.
/// </remarks>
public sealed class ConsoleContext {
    readonly List<string> arguments = [];

    internal ConsoleContext(ConsoleCommands commands) => Commands = commands;

    /// <summary>The registry the command was found in, so a command can call another.</summary>
    public ConsoleCommands Commands { get; }

    /// <summary>The whole line as typed, the command name included.</summary>
    public string Line { get; private set; } = string.Empty;

    /// <summary>The arguments after the command name.</summary>
    public IReadOnlyList<string> Arguments => arguments;

    /// <summary>How many arguments there are.</summary>
    public int Count => arguments.Count;

    /// <summary>An argument, or <see langword="null" /> if there are not that many.</summary>
    /// <param name="index">Which one, from zero.</param>
    public string? this[int index] => index >= 0 && index < arguments.Count ? arguments[index] : null;

    /// <summary>Writes a line back to whoever ran the command.</summary>
    /// <param name="text">What to say.</param>
    public void Write(string text) => Commands.Write(text);

    /// <summary>Reads an argument as a number.</summary>
    /// <param name="index">Which one, from zero.</param>
    /// <param name="value">The number.</param>
    /// <returns><see langword="false" /> if it is missing or is not one.</returns>
    public bool TryNumber(int index, out float value) =>
        float.TryParse(this[index], System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>Reads an argument as on or off.</summary>
    /// <param name="index">Which one, from zero.</param>
    /// <param name="value">The flag.</param>
    /// <returns><see langword="false" /> if it is missing or is not one of the accepted words.</returns>
    /// <remarks>
    ///     <c>on</c>, <c>off</c>, <c>1</c> and <c>0</c> as well as <c>true</c> and <c>false</c>,
    ///     because a console is typed at in a hurry and being told "off is not a boolean" is not help.
    /// </remarks>
    public bool TryFlag(int index, out bool value) {
        value = false;

        switch (this[index]?.ToLowerInvariant()) {
            case "on" or "1" or "true" or "yes":
                value = true;
                return true;

            case "off" or "0" or "false" or "no":
                return true;

            default:
                return false;
        }
    }

    internal void Reset(string line, ReadOnlySpan<char> rest) {
        Line = line;
        arguments.Clear();

        while (!rest.IsEmpty) {
            rest = rest.TrimStart();

            if (rest.IsEmpty) {
                break;
            }

            // Quoted runs are one argument, so `echo "hello world"` says what it looks like it says.
            if (rest[0] == '"') {
                var close = rest[1..].IndexOf('"');
                var end = close < 0 ? rest.Length : close + 1;

                arguments.Add(rest[1..end].ToString());
                rest = close < 0 ? default : rest[(end + 1)..];
                continue;
            }

            var space = rest.IndexOf(' ');

            if (space < 0) {
                arguments.Add(rest.ToString());
                break;
            }

            arguments.Add(rest[..space].ToString());
            rest = rest[space..];
        }
    }
}

/// <summary>One registered command.</summary>
/// <param name="Name">What is typed to run it.</param>
/// <param name="Help">One line saying what it does.</param>
/// <param name="Run">What it does.</param>
public sealed record ConsoleCommand(string Name, string Help, Action<ConsoleContext> Run);

/// <summary>
///     The commands a console can run, and the lines they have written back.
/// </summary>
/// <remarks>
///     <para>
///         Separate from <see cref="ConsoleOverlay" /> because the two are useful apart: a remote
///         inspector runs commands with no overlay drawn, and a test runs them with no screen at all.
///         The overlay is one front end onto this.
///     </para>
///     <para>
///         ⚠ <b>A command runs on whatever thread called <see cref="Execute" />.</b> Nothing here
///         marshals. The overlay calls it from the loop thread, which is where a command that touches
///         the world has to be; a remote inspector must queue rather than call straight off its
///         socket thread.
///     </para>
/// </remarks>
public sealed class ConsoleCommands {
    /// <summary>How many output lines are kept.</summary>
    public const int MaxOutput = 256;

    /// <summary>How many entered lines are remembered.</summary>
    public const int MaxHistory = 64;

    readonly Dictionary<string, ConsoleCommand> commands = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> output = [];
    readonly List<string> history = [];
    readonly ConsoleContext context;

    /// <summary>Builds a registry with the built-in commands in it.</summary>
    public ConsoleCommands() {
        context = new(this);

        Register("help", "Lists the commands, or explains one: help [command]", Help);
        Register("clear", "Empties the console output", _ => Clear());
        Register("echo", "Writes its arguments back", static entry => entry.Write(string.Join(' ', entry.Arguments)));
    }

    /// <summary>Everything registered, in no particular order.</summary>
    public IReadOnlyCollection<ConsoleCommand> Registered => commands.Values;

    /// <summary>The lines commands have written, oldest first.</summary>
    public IReadOnlyList<string> Output => output;

    /// <summary>The lines that have been entered, oldest first.</summary>
    public IReadOnlyList<string> History => history;

    /// <summary>Adds a command.</summary>
    /// <param name="name">What is typed to run it.</param>
    /// <param name="help">One line saying what it does.</param>
    /// <param name="run">What it does.</param>
    /// <remarks>Registering a name twice replaces it, so a game can override a built-in.</remarks>
    public void Register(string name, string help, Action<ConsoleContext> run) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);

        commands[name] = new(name, help ?? string.Empty, run);
    }

    /// <summary>Removes a command.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns><see langword="true" /> if one was removed.</returns>
    public bool Remove(string name) => commands.Remove(name);

    /// <summary>Finds the commands whose names start with a prefix, for completion.</summary>
    /// <param name="prefix">What has been typed.</param>
    /// <param name="matches">Where the names go, sorted.</param>
    /// <returns>How many matched.</returns>
    public int Complete(ReadOnlySpan<char> prefix, List<string> matches) {
        ArgumentNullException.ThrowIfNull(matches);

        matches.Clear();

        foreach (var name in commands.Keys) {
            if (name.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                matches.Add(name);
            }
        }

        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return matches.Count;
    }

    /// <summary>Runs a line.</summary>
    /// <param name="line">What was typed.</param>
    /// <returns><see langword="false" /> if there is no such command.</returns>
    /// <remarks>
    ///     ⚠ <b>A command that throws is reported, not propagated.</b> A console exists to be typed at
    ///     while something is already going wrong, and taking the process down because somebody
    ///     mistyped an argument would destroy the state they were inspecting.
    /// </remarks>
    public bool Execute(string line) {
        if (string.IsNullOrWhiteSpace(line)) {
            return false;
        }

        Remember(line);
        Write($"> {line}");

        var trimmed = line.AsSpan().Trim();
        var space = trimmed.IndexOf(' ');
        var name = space < 0 ? trimmed : trimmed[..space];

        if (!commands.TryGetValue(name.ToString(), out var command)) {
            Write($"There is no command called '{name}'. Try 'help'.");
            return false;
        }

        context.Reset(line, space < 0 ? default : trimmed[space..]);

        try {
            command.Run(context);
            return true;
        } catch (Exception exception) {
            Write($"{command.Name} failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    /// <summary>Writes a line of output.</summary>
    /// <param name="text">What to say.</param>
    public void Write(string text) {
        output.Add(text ?? string.Empty);

        if (output.Count > MaxOutput) {
            output.RemoveRange(0, output.Count - MaxOutput);
        }
    }

    /// <summary>Throws the output away. Does not touch the history.</summary>
    public void Clear() => output.Clear();

    /// <summary>
    ///     Finds every <see cref="ConsoleCommandAttribute" />-marked static method in an assembly and
    ///     registers it.
    /// </summary>
    /// <param name="assembly">Where to look.</param>
    /// <returns>How many were registered.</returns>
    /// <remarks>
    ///     ⚠ <b>Reflection over every type in an assembly, and annotated as such.</b> This is the one
    ///     part of the console that a trimmed or AOT build cannot have for free — the attribute is a
    ///     compile-time fact and finding it at run time is not. It is marked
    ///     <see cref="RequiresUnreferencedCodeAttribute" /> rather than quietly returning nothing on
    ///     those builds, so a caller finds out at compile time instead of wondering why the command
    ///     they registered is missing on device. <see cref="Register(string, string, Action{ConsoleContext})" />
    ///     works everywhere and is what a shipping title should use until the generator that reads
    ///     the attribute at compile time exists.
    /// </remarks>
    [RequiresUnreferencedCode("Scans every type in the assembly for [ConsoleCommand]; trimming may remove them.")]
    public int RegisterFrom(Assembly assembly) {
        ArgumentNullException.ThrowIfNull(assembly);

        var found = 0;

        foreach (var type in assembly.GetTypes()) {
            found += RegisterFrom(type);
        }

        return found;
    }

    /// <summary>Registers the marked static methods of one type.</summary>
    /// <param name="type">Where to look.</param>
    /// <returns>How many were registered.</returns>
    [RequiresUnreferencedCode("Reads the type's methods reflectively; trimming may remove them.")]
    public int RegisterFrom(Type type) {
        ArgumentNullException.ThrowIfNull(type);

        var found = 0;

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
            if (method.GetCustomAttribute<ConsoleCommandAttribute>() is not { } attribute) {
                continue;
            }

            var parameters = method.GetParameters();

            // Refused rather than skipped: a method somebody marked and got the shape wrong on is a
            // command they expect to be able to type, and silence is the worst possible answer.
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(ConsoleContext)) {
                throw new InvalidOperationException(
                    $"[ConsoleCommand] {type.Name}.{method.Name} must be static and take one ConsoleContext."
                );
            }

            Register(
                attribute.Name,
                attribute.Help,
                method.CreateDelegate<Action<ConsoleContext>>()
            );

            found++;
        }

        return found;
    }

    void Remember(string line) {
        // A repeat of the last line is not worth a history entry — holding Up to find something is
        // the thing history is for, and a run of identical entries defeats it.
        if (history.Count > 0 && string.Equals(history[^1], line, StringComparison.Ordinal)) {
            return;
        }

        history.Add(line);

        if (history.Count > MaxHistory) {
            history.RemoveRange(0, history.Count - MaxHistory);
        }
    }

    void Help(ConsoleContext entry) {
        if (entry.Count > 0) {
            if (commands.TryGetValue(entry[0]!, out var one)) {
                entry.Write($"{one.Name} — {one.Help}");
            } else {
                entry.Write($"There is no command called '{entry[0]}'.");
            }

            return;
        }

        var names = new List<string>(commands.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names) {
            entry.Write($"{name,-16} {commands[name].Help}");
        }
    }
}
