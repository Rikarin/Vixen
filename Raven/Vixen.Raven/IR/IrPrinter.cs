// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Raven.IR;

/// <summary>
///     Renders an <see cref="IrModule" /> as stable, readable text. This is the IR's
///     debug view and the format the golden tests snapshot, so its output is
///     deterministic: no hash codes, no dictionary ordering, no culture-sensitive
///     number formatting.
/// </summary>
public static class IrPrinter {
    public static string Print(IrModule module) {
        var writer = new Writer();
        writer.Line($"module {module.Name}");

        foreach (var structType in module.Structs) {
            writer.Blank();
            PrintStruct(writer, structType);
        }

        foreach (var function in module.Functions) {
            writer.Blank();
            PrintFunction(writer, function);
        }

        foreach (var shader in module.Shaders) {
            writer.Blank();
            PrintShader(writer, shader);
        }

        return writer.ToString();
    }

    public static string Print(IrFunction function) {
        var writer = new Writer();
        PrintFunction(writer, function);
        return writer.ToString();
    }

    static void PrintStruct(Writer writer, IrStructType structType) {
        writer.Line($"struct {structType.Name}");
        writer.Indent();

        foreach (var field in structType.Fields) {
            writer.Line($"{field.Name} : {field.Type.Name}");
        }

        writer.Outdent();
        writer.Line("end");
    }

    static void PrintShader(Writer writer, IrShader shader) {
        writer.Line($"shader {shader.Name}");
        writer.Indent();

        // What the shader can be varied by. Folded out of every body by this point, so the dump
        // is the only place it is visible.
        foreach (var parameter in shader.ValueParameters) {
            writer.Line($"parameter {parameter.Name} : {parameter.Type.Name}");
        }

        foreach (var permutation in shader.Permutations) {
            var value = permutation.DefaultValue is null
                ? string.Empty
                : $" = {FormatConstant(permutation.DefaultValue)}";

            writer.Line($"permutation {permutation.Name} : {permutation.Type.Name}{value}");
        }

        foreach (var binding in shader.Bindings) {
            var semantic = binding.Semantic is null ? string.Empty : $" semantic \"{binding.Semantic}\"";
            writer.Line(
                $"binding {binding.Kind.ToString().ToLowerInvariant()} {binding.Variable} : "
                + $"{binding.Type.Name} slot {binding.Slot}{semantic}"
            );
        }

        foreach (var entryPoint in shader.EntryPoints) {
            var output = entryPoint.Outputs.Count == 0
                ? string.Empty
                : " -> " + string.Join(", ", entryPoint.Outputs.Select(Describe));
            var inputs = string.Join(", ", entryPoint.Inputs.Select(Describe));
            writer.Line(
                $"entry {entryPoint.Stage.ToString().ToLowerInvariant()} "
                + $"{entryPoint.Function.Name}({inputs}){output}"
            );
        }

        if (shader.Initializer.Statements.Count > 0) {
            writer.Blank();
            writer.Line("init");
            writer.Indent();
            PrintStatements(writer, shader.Initializer);
            writer.Outdent();
            writer.Line("end");
        }

        foreach (var function in shader.Functions) {
            writer.Blank();
            PrintFunction(writer, function);
        }

        writer.Outdent();
        writer.Line("end");
    }

    static string Describe(IrStageIo io) {
        var semantic = io.Semantic is null ? string.Empty : $" \"{io.Semantic}\"";
        return $"{io.Name} : {io.Type.Name}{semantic}";
    }

    static void PrintFunction(Writer writer, IrFunction function) {
        var parameters = string.Join(", ", function.Parameters.Select(p => $"{p} : {p.Type.Name}"));
        writer.Line($"func {function.Name}({parameters}) : {function.ReturnType.Name}");
        writer.Indent();

        foreach (var local in function.Locals) {
            writer.Line($"local {local} : {local.Type.Name}");
        }

        PrintStatements(writer, function.Body);
        writer.Outdent();
        writer.Line("end");
    }

    static void PrintStatements(Writer writer, IrBlock block) {
        foreach (var statement in block.Statements) {
            PrintStatement(writer, statement);
        }
    }

    static void PrintStatement(Writer writer, IrStatement statement) {
        switch (statement) {
            case IrBlock block:
                PrintStatements(writer, block);
                break;

            case IrInstruction instruction:
                writer.Line(Format(instruction));
                break;

            case IrIfStatement conditional:
                writer.Line($"if {conditional.Condition}");
                writer.Indent();
                PrintStatements(writer, conditional.Then);
                writer.Outdent();

                if (conditional.Else is { } otherwise) {
                    writer.Line("else");
                    writer.Indent();
                    PrintStatements(writer, otherwise);
                    writer.Outdent();
                }

                writer.Line("end");
                break;

            case IrLoopStatement loop:
                writer.Line("loop");
                writer.Indent();

                writer.Line("cond");
                writer.Indent();
                PrintStatements(writer, loop.Condition);
                writer.Outdent();
                writer.Line($"test {loop.ConditionValue} {(loop.TestBeforeBody ? "before-body" : "after-body")}");

                writer.Line("body");
                writer.Indent();
                PrintStatements(writer, loop.Body);
                writer.Outdent();

                if (loop.Continue is { } step) {
                    writer.Line("step");
                    writer.Indent();
                    PrintStatements(writer, step);
                    writer.Outdent();
                }

                writer.Outdent();
                writer.Line("end");
                break;

            case IrReturnStatement @return:
                writer.Line(@return.Value is { } value ? $"return {value}" : "return");
                break;

            case IrBreakStatement:
                writer.Line("break");
                break;

            case IrContinueStatement:
                writer.Line("continue");
                break;

            case IrDiscardStatement:
                writer.Line("discard");
                break;
        }
    }

    static string Format(IrInstruction instruction) {
        var body = instruction switch {
            IrConstantInstruction constant => $"const {FormatConstant(constant.Value)}",
            IrLoadInstruction load => $"load {load.Place}",
            IrStoreInstruction store => $"store {store.Place}, {store.Value}",
            IrArrayLengthInstruction length => $"length {length.Place}",
            IrUnaryInstruction unary => $"{Lower(unary.Op)} {unary.Operand}",
            IrBinaryInstruction binary => $"{Lower(binary.Op)} {binary.Left}, {binary.Right}",
            IrConvertInstruction convert => $"convert.{Lower(convert.ConversionKind)} {convert.Operand}",
            IrIntrinsicInstruction intrinsic =>
                $"intrinsic.{Lower(intrinsic.Intrinsic)} {Join(intrinsic.Arguments)}".TrimEnd(),
            IrCallInstruction call => $"call {call.Function.Name}({string.Join(", ", call.Arguments)})",
            IrConstructInstruction construct => $"construct {Join(construct.Arguments)}".TrimEnd(),
            IrExtractInstruction extract =>
                $"extract {extract.Source}{string.Concat(extract.Chain.Select(a => a.ToString()))}",
            IrSelectInstruction select => $"select {select.Condition}, {select.WhenTrue}, {select.WhenFalse}",
            _ => instruction.GetType().Name
        };

        return instruction.Result is { } result ? $"{result} = {body} : {result.Type.Name}" : body;
    }

    static string Join(IReadOnlyList<IrValue> values) => string.Join(", ", values);

    static string FormatConstant(object? value) =>
        value switch {
            null => "zero",
            bool flag => flag ? "true" : "false",
            float number => number.ToString("R", CultureInfo.InvariantCulture) + "f",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?"
        };

    static string Lower(Enum value) {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Indent-tracking line writer, so the printers stay declarative.</summary>
    sealed class Writer {
        readonly StringBuilder builder = new();
        int indent;

        public void Indent() => indent++;
        public void Outdent() => indent--;

        public void Line(string text) => builder.Append(' ', indent * 2).Append(text).Append('\n');

        public void Blank() => builder.Append('\n');

        public override string ToString() => builder.ToString();
    }
}
