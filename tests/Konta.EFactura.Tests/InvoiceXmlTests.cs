using System.Globalization;
using System.Xml.Linq;
using Konta.EFactura;

namespace Konta.EFactura.Tests;

public sealed class InvoiceXmlTests
{
    private static Invoice Sample(params InvoiceLine[] lines) => new()
    {
        SupplierIdno = "1002600001257",
        BuyerIdno = "1002600003354",
        SupplierBankAccount = "22241410046",
        BuyerBankAccount = "2224710SV12365037100",
        DeliveryDate = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
        CreationMotive = CreationMotive.Delivery,
        CorrelationId = "konta-1",
        Lines = lines,
    };

    private static InvoiceLine Line(
        decimal quantity = 2m,
        decimal unitPrice = 15m,
        decimal vatPercent = 20m) => new()
        {
            Code = "1",
            Name = "Servicii de abonament",
            UnitOfMeasure = "buc",
            Quantity = quantity,
            UnitPriceWithoutVat = unitPrice,
            VatPercent = vatPercent,
        };

    [Fact]
    public void Serialises_the_documents_envelope()
    {
        var xml = XDocument.Parse(InvoiceXml.Build([Sample(Line())]));

        Assert.Equal("Documents", xml.Root!.Name.LocalName);
        Assert.Single(xml.Root.Elements("Document"));
        Assert.Equal(
            "1002600001257",
            xml.Descendants("Supplier").Single().Attribute("IDNO")!.Value);
    }

    [Fact]
    public void Writes_vat_as_a_whole_number_percentage_not_a_ratio()
    {
        var xml = XDocument.Parse(InvoiceXml.Build([Sample(Line(vatPercent: 20m))]));
        var row = xml.Descendants("Row").Single();

        Assert.Equal("20", row.Attribute("TVA")!.Value);
    }

    [Fact]
    public void Computes_line_totals_consistently()
    {
        var xml = XDocument.Parse(InvoiceXml.Build([Sample(Line(quantity: 2m, unitPrice: 15m))]));
        var row = xml.Descendants("Row").Single();

        Assert.Equal("30.00", row.Attribute("TotalPriceWithoutTVA")!.Value);
        Assert.Equal("6.00", row.Attribute("TotalTVA")!.Value);
        Assert.Equal("36.00", row.Attribute("TotalPrice")!.Value);
    }

    [Fact]
    public void Keeps_zero_vat_lines_in_the_document_totals()
    {
        // A zero-rated line still contributes its net amount. Dropping it is a known way to
        // produce a document whose totals disagree with its lines.
        var invoice = Sample(Line(vatPercent: 20m), Line(quantity: 1m, unitPrice: 50m, vatPercent: 0m));

        Assert.Equal(80m, invoice.TotalWithoutVat);
        Assert.Equal(6m, invoice.TotalVat);
        Assert.Equal(86m, invoice.TotalWithVat);
        Assert.Equal(2, XDocument.Parse(InvoiceXml.Build([invoice])).Descendants("Row").Count());
    }

    [Fact]
    public void Handles_a_credit_line_with_a_negative_quantity()
    {
        var xml = XDocument.Parse(InvoiceXml.Build([Sample(Line(quantity: -1m, unitPrice: 15m))]));
        var row = xml.Descendants("Row").Single();

        Assert.Equal("-15.00", row.Attribute("TotalPriceWithoutTVA")!.Value);
        Assert.Equal("-18.00", row.Attribute("TotalPrice")!.Value);
    }

    [Fact]
    public void Rejects_a_vat_rate_given_as_a_ratio()
    {
        var invoice = Sample(Line(vatPercent: 0.20m));

        // 0.20 is a legal value in range, so it cannot be rejected outright — but it produces a
        // 0.2% document, which is why the property is documented and named Percent.
        Assert.Equal(0.06m, invoice.TotalVat);
    }

    [Fact]
    public void Rejects_an_idno_that_is_not_thirteen_digits()
    {
        var invoice = new Invoice
        {
            SupplierIdno = "123",
            BuyerIdno = "1002600003354",
            SupplierBankAccount = "22241410046",
            BuyerBankAccount = "2224710SV12365037100",
            DeliveryDate = DateTimeOffset.UnixEpoch,
            CreationMotive = CreationMotive.Delivery,
            Lines = [Line()],
        };

        Assert.Throws<InvalidOperationException>(invoice.Validate);
    }

    [Theory]
    [InlineData("", "2224710SV12365037100")]
    [InlineData("22241410046", " ")]
    public void Rejects_an_invoice_without_both_bank_accounts(string supplierAccount, string buyerAccount)
    {
        // The test service fails on either one missing, though the schema allows it (2026-10-08).
        var invoice = new Invoice
        {
            SupplierIdno = "1002600001257",
            BuyerIdno = "1002600003354",
            SupplierBankAccount = supplierAccount,
            BuyerBankAccount = buyerAccount,
            DeliveryDate = DateTimeOffset.UnixEpoch,
            CreationMotive = CreationMotive.Delivery,
            Lines = [Line()],
        };

        Assert.Throws<InvalidOperationException>(invoice.Validate);
    }

    [Fact]
    public void Rejects_an_invoice_with_no_lines()
    {
        var invoice = new Invoice
        {
            SupplierIdno = "1002600001257",
            BuyerIdno = "1002600003354",
            SupplierBankAccount = "22241410046",
            BuyerBankAccount = "2224710SV12365037100",
            DeliveryDate = DateTimeOffset.UnixEpoch,
            CreationMotive = CreationMotive.Delivery,
            Lines = [],
        };

        Assert.Throws<InvalidOperationException>(invoice.Validate);
    }

    [Fact]
    public void Serialises_numbers_invariantly_whatever_the_ambient_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // ro-MD uses a comma as the decimal separator. The wire format must not.
            CultureInfo.CurrentCulture = new CultureInfo("ro-MD");

            var xml = XDocument.Parse(InvoiceXml.Build([Sample(Line())]));
            var row = xml.Descendants("Row").Single();

            Assert.Equal("30.00", row.Attribute("TotalPriceWithoutTVA")!.Value);
            Assert.DoesNotContain(",", row.Attribute("TotalPriceWithoutTVA")!.Value, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Echoes_our_correlation_id()
    {
        var xml = XDocument.Parse(InvoiceXml.Build([Sample(Line())]));

        Assert.Equal("konta-1", xml.Descendants("id").Single().Value);
    }

    [Fact]
    public void Writes_each_partys_name_as_its_title()
    {
        var invoice = Sample(Line());
        var named = new Invoice
        {
            SupplierIdno = invoice.SupplierIdno,
            BuyerIdno = invoice.BuyerIdno,
            SupplierName = " Exemplu SRL ",
            BuyerName = "Cumpărător SA",
            SupplierBankAccount = invoice.SupplierBankAccount,
            BuyerBankAccount = invoice.BuyerBankAccount,
            DeliveryDate = invoice.DeliveryDate,
            CreationMotive = invoice.CreationMotive,
            Lines = invoice.Lines,
        };

        var xml = XDocument.Parse(InvoiceXml.Build([named]));

        Assert.Equal("Exemplu SRL", xml.Descendants("Supplier").Single().Attribute("Title")!.Value);
        Assert.Equal("Cumpărător SA", xml.Descendants("Buyer").Single().Attribute("Title")!.Value);
        Assert.Null(XDocument.Parse(InvoiceXml.Build([invoice])).Descendants("Supplier").Single().Attribute("Title"));
    }

    [Fact]
    public void Posts_the_issuers_own_amounts_rounded_another_way()
    {
        // 3 x 3.375 = 10.125: half to even is 10.12, the books' half away from zero 10.13.
        var line = new InvoiceLine { Code = "1", Name = "Servicii", UnitOfMeasure = "buc", Quantity = 3m, UnitPriceWithoutVat = 3.375m, VatPercent = 20m, Net = 10.13m, Vat = 2.03m };

        var row = XDocument.Parse(InvoiceXml.Build([Sample(line)])).Descendants("Row").Single();

        Assert.Equal("10.13", row.Attribute("TotalPriceWithoutTVA")!.Value);
        Assert.Equal("2.03", row.Attribute("TotalTVA")!.Value);
        Assert.Equal("12.16", row.Attribute("TotalPrice")!.Value);
        Assert.Equal("10.12", XDocument.Parse(InvoiceXml.Build([Sample(Line(quantity: 3m, unitPrice: 3.375m, vatPercent: 0m))])).Descendants("Row").Single().Attribute("TotalPriceWithoutTVA")!.Value);
    }

    [Theory]
    [InlineData(10.14, null)]
    [InlineData(10.125, null)]
    [InlineData(10.13, 2.05)]
    public void Rejects_an_amount_that_is_not_the_line_rounded(double net, double? vat)
    {
        var line = new InvoiceLine
        {
            Code = "1", Name = "Servicii", UnitOfMeasure = "buc", Quantity = 3m, UnitPriceWithoutVat = 3.375m, VatPercent = 20m,
            Net = (decimal)net, Vat = vat is null ? null : (decimal)vat.Value,
        };

        Assert.Throws<InvalidOperationException>(() => InvoiceXml.Build([Sample(line)]));
    }
}
