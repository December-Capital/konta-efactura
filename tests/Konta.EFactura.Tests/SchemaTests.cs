using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Konta.EFactura.Tests;

/// <summary>
/// Every payload the serialiser builds is valid against SFS's own schema
/// (<c>spec/sfs/TaxInvoiceSchema.xsd</c>), not only against our reading of the guide.
/// </summary>
public sealed class SchemaTests
{
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var set = new XmlSchemaSet();
        set.Add(null, Path.Combine(AppContext.BaseDirectory, "sfs", "TaxInvoiceSchema.xsd"));
        set.Compile();
        return set;
    });

    private static Invoice Sample(CreationMotive motive) => new()
    {
        SupplierIdno = "1002600001257",
        BuyerIdno = "1002600003354",
        SupplierBankAccount = "22241410046",
        DeliveryDate = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
        CreationMotive = motive,
        CorrelationId = "konta-1",
        Lines =
        [
            new InvoiceLine { Code = "1", Name = "Pâine", UnitOfMeasure = "buc", Quantity = 3m, UnitPriceWithoutVat = 7.5m, VatPercent = 8m },
            new InvoiceLine { Code = "2", Name = "Servicii", UnitOfMeasure = "ore", Quantity = 1.5m, UnitPriceWithoutVat = 400m, VatPercent = 20m },
        ],
    };

    private static List<string> Problems(string xml)
    {
        var problems = new List<string>();
        XDocument.Parse(xml).Validate(Schema.Value, (_, e) => problems.Add(e.Message));
        return problems;
    }

    [Theory]
    [InlineData(CreationMotive.Delivery)]
    [InlineData(CreationMotive.NonDelivery)]
    public void A_payload_is_valid_against_the_official_schema(CreationMotive motive)
    {
        Assert.Empty(Problems(InvoiceXml.Build([Sample(motive), Sample(motive)])));
    }

    [Fact]
    public void The_schema_refuses_a_document_without_its_creation_motive()
    {
        var xml = XDocument.Parse(InvoiceXml.Build([Sample(CreationMotive.Delivery)]));
        xml.Descendants("CreationMotiv").Single().Remove();

        Assert.Contains(Problems(xml.ToString()), p => p.Contains("Merchandises", StringComparison.Ordinal) || p.Contains("CreationMotiv", StringComparison.Ordinal));
    }

    [Fact]
    public void The_creation_motive_is_the_platforms_number()
    {
        var xml = XDocument.Parse(InvoiceXml.Build([Sample(CreationMotive.NonDelivery)]));

        Assert.Equal("5", xml.Descendants("CreationMotiv").Single().Value);
    }
}
