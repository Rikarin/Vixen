using System.Xml;
using System.Xml.Serialization;

namespace Vixen.Core.Syntax.Generator.Model;

#nullable disable

public class Comment {
    [XmlAnyElement] public XmlElement[] Body { get; set; }
}
