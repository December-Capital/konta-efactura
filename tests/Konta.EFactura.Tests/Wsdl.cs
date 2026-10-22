using System.Xml.Linq;

namespace Konta.EFactura.Tests;

/// <summary>
/// What a WSDL of SFS's service says, reduced to what the generated contract depends on: its
/// operations, and each data contract of SFS's model with the members it declares and their types.
/// Namespace prefixes and the service address are left out, as they differ between copies that
/// say the same thing.
/// </summary>
internal static class Wsdl
{
    /// <summary>The namespace of SFS's data contracts.</summary>
    public const string Model = "http://schemas.datacontract.org/2004/07/AX.EFactura.Model.ApiModel";

    private static readonly XNamespace Definitions = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Schema = "http://www.w3.org/2001/XMLSchema";

    /// <summary>A WSDL committed under <c>spec/sfs/</c>.</summary>
    /// <param name="name">Its file name.</param>
    /// <returns>The document.</returns>
    public static XDocument Committed(string name) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "sfs", name));

    /// <summary>The operations of the service's port type.</summary>
    /// <param name="wsdl">The WSDL.</param>
    /// <returns>Their names, sorted.</returns>
    public static List<string> Operations(XDocument wsdl) =>
        [.. wsdl.Descendants(Definitions + "portType").Elements(Definitions + "operation")
            .Select(o => (string)o.Attribute("name")!)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every member each of SFS's data contracts declares, as <c>Type.Member: ItsType</c>; arrays,
    /// which the generator turns into .NET arrays, are left out as types of their own.
    /// </summary>
    /// <param name="wsdl">The WSDL.</param>
    /// <returns>The members, sorted.</returns>
    public static List<string> Members(XDocument wsdl) =>
        [.. wsdl.Descendants(Schema + "schema")
            .Where(s => (string?)s.Attribute("targetNamespace") == Model)
            .Elements(Schema + "complexType")
            .Where(t => !((string)t.Attribute("name")!).StartsWith("ArrayOf", StringComparison.Ordinal))
            .SelectMany(t => t.Descendants(Schema + "element").Select(e =>
                $"{(string)t.Attribute("name")!}.{(string)e.Attribute("name")!}: {LocalName((string?)e.Attribute("type"))}"))
            .Order(StringComparer.Ordinal)];

    /// <summary>Operations and members together, for comparing two copies.</summary>
    /// <param name="wsdl">The WSDL.</param>
    /// <returns>One line each.</returns>
    public static List<string> Shape(XDocument wsdl) =>
        [.. Operations(wsdl).Select(o => "operation " + o), .. Members(wsdl).Select(m => "member " + m)];

    private static string LocalName(string? qualified) =>
        qualified is null ? "?" : qualified[(qualified.IndexOf(':', StringComparison.Ordinal) + 1)..];
}
