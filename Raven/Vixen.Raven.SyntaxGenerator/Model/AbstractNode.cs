#nullable disable
namespace SyntaxGenerator.Model;

public class AbstractNode : TreeType {
    public List<Field> Fields { get; } = [];
}
