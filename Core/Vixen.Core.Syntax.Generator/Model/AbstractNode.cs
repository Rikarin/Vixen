#nullable disable
namespace Vixen.Core.Syntax.Generator.Model;

public class AbstractNode : TreeType {
    public List<Field> Fields { get; } = [];
}
