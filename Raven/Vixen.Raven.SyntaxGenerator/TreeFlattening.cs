using SyntaxGenerator.Model;

namespace SyntaxGenerator;

public static class TreeFlattening {
    public static void FlattenChildren(Tree tree) {
        foreach (var type in tree.Types) {
            switch (type) {
                case AbstractNode node:
                    FlattenChildren(node.Children, node.Fields, false);
                    break;
                case Node node:
                    FlattenChildren(node.Children, node.Fields, false);
                    break;
            }
        }
    }

    static void FlattenChildren(
        List<TreeTypeChild> fieldsAndChoices,
        List<Field> fields,
        bool makeOptional
    ) {
        foreach (var fieldOrChoice in fieldsAndChoices) {
            switch (fieldOrChoice) {
                case Field field:
                    if (makeOptional && !AbstractFileWriter.IsNodeList(field.Type)) {
                        field.Optional = "true";
                    }

                    fields.Add(field);
                    break;
                case Choice choice:
                    // Children of choices are always optional (since the point is to
                    // choose from one of them and leave out the rest).
                    FlattenChildren(choice.Children, fields, true);
                    break;
                case Sequence sequence:
                    FlattenChildren(sequence.Children, fields, makeOptional);
                    break;
                default:
                    throw new InvalidOperationException("Unknown child type.");
            }
        }
    }
}
