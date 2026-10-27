using System.Globalization;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Konta.EFactura.Tests;

/// <summary>
/// Invoices read back from the platform's XML: a signed one in the shape the schema gives (made-up
/// parties, <c>fixtures/documents/</c>), one we posted and had signed on the test service, the test
/// service's one in the format before it, both anonymised, and our own drafts read back as we built them.
/// </summary>
public sealed class DocumentReaderTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "documents", name));

    [Fact]
    public void The_fixture_is_valid_against_SFSs_schema()
    {
        var set = new XmlSchemaSet();
        set.Add(null, Path.Combine(AppContext.BaseDirectory, "sfs", "TaxInvoiceSchema.xsd"));
        set.Compile();
        var problems = new List<string>();

        new XDocument(new XElement("Documents", XElement.Parse(Fixture("received-signed.xml"))))
            .Validate(set, (_, e) => problems.Add(e.Message));

        Assert.Empty(problems);
    }

    [Fact]
    public void A_signed_invoice_is_read_with_its_parties_rows_and_series()
    {
        var document = InvoiceDocumentReader.Read(Fixture("received-signed.xml"));

        Assert.Equal(("EAA", "000000123"), (document.Seria, document.Number));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(3)), document.IssuedDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), document.DeliveryDate);
        Assert.Equal(
            new EFacturaParty("1099900000017", "\"FURNIZOR EXEMPLU\" S.R.L.", "mun. Chișinău, str. Exemplului 1", "0099001", "MD77AG000000022510000101", "TESTMD2X101", "B.C.'BANCA DE TEST'S.A."),
            document.Supplier);
        Assert.Equal(("1099900000028", null as string), (document.Buyer.Idno, document.Buyer.VatCode));
        Assert.Equal(4, document.CreationMotive);
        Assert.True(document.IssuedByVatPayer);
        Assert.Equal("Conform contractului nr. 7", document.Notes);
        Assert.Equal("furnizor-42", document.CorrelationId);

        Assert.Equal(3, document.Lines.Count);
        Assert.Equal(new EFacturaDocumentLine("H-01", "Hârtie A4, 500 coli", "buc", 10m, 85.50m, 855.00m, "20", 171.00m, 1026.00m), document.Lines[0]);
        Assert.Null(document.Lines[1].Code);
        Assert.Equal("8", document.Lines[1].VatCode);

        // "-" is a code in SFS's classifier, not a number: it is kept as written.
        Assert.Equal(("-", -1m, -5.00m), (document.Lines[2].VatCode, document.Lines[2].Quantity, document.Lines[2].TotalWithVat));
        Assert.Equal((881.00m, 173.48m, 1054.48m), (document.TotalWithoutVat, document.TotalVat, document.TotalWithVat));
    }

    [Fact]
    public void A_signed_invoice_in_the_older_format_is_read_with_what_it_states()
    {
        // The test service's invoice of 2017, anonymised (fixtures/README.md).
        var document = InvoiceDocumentReader.Read(Fixture("received-older.xml"));

        Assert.True(document.OlderFormat);
        Assert.Equal(("EXMP", "000000129"), (document.Seria, document.Number));
        Assert.Null(document.IssuedDate);
        Assert.Equal(new DateTimeOffset(2017, 2, 9, 11, 2, 40, 804, TimeSpan.Zero), document.DeliveryDate);
        Assert.Equal(("1099900000040", "0099002", "MD33VI167990695471475MDL"), (document.Supplier.Idno, document.Supplier.VatCode, document.Supplier.BankAccount));

        // Ten spaces of VAT code, an empty account and a bank named by a space are absent, not values.
        Assert.Equal(new EFacturaParty("1099900000028", "EXEMPLU CUMPARATOR S.R.L.", "SEC.CIOCANA Strada Exemplului nr.3", null, null, null, null), document.Buyer);

        // No CreationMotiv, so nobody can say from the document whether it was issued as a VAT payer.
        Assert.Null(document.CreationMotive);
        Assert.Null(document.IssuedByVatPayer);
        Assert.Null(document.CorrelationId);

        Assert.Equal(new EFacturaDocumentLine("1", "Servicii prestate", "buc", 1m, 1519m, 1519m, null, 0m, 1519m), Assert.Single(document.Lines));
        Assert.Equal((1519m, 0m, 1519m), (document.TotalWithoutVat, document.TotalVat, document.TotalWithVat));
    }

    [Fact]
    public void An_invoice_we_posted_and_signed_is_read_with_what_the_platform_added()
    {
        // Signed in the web interface on 2026-10-27 and read back from the test service, anonymised
        // (fixtures/README.md). The platform added Total, TotalTVA, IsFarma and flags the schema lacks.
        var document = InvoiceDocumentReader.Read(Fixture("signed-by-supplier.xml"));

        Assert.False(document.OlderFormat);
        Assert.Equal(("EXMP", "000000130"), (document.Seria, document.Number));

        // Written without an offset, in Chișinău time: 13:28:53 UTC, two seconds after the draft was posted.
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 15, 28, 53, 507, TimeSpan.FromHours(2)), document.IssuedDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 13, 28, 51, 997, TimeSpan.Zero), document.DeliveryDate);

        // Names and addresses are not in the signed XML; the buyer's VAT code is.
        Assert.Equal(new EFacturaParty("1099900000028", null, null, null, "MD08EN088898186506590277", null, null), document.Supplier);
        Assert.Equal(new EFacturaParty("1099900000017", null, null, "0099001", "MD35AG298229286314360507", null, null), document.Buyer);
        Assert.Equal(1, document.CreationMotive);
        Assert.Equal("konta-sign-20261027132851", document.CorrelationId);
        Assert.Equal(new EFacturaDocumentLine("1", "Servicii de testare, semnare", "buc", 1m, 17.27m, 17.27m, "0", 0m, 17.27m), Assert.Single(document.Lines));
    }

    [Theory]
    [InlineData("2026-07-15T12:00:00", 3)]
    [InlineData("2026-01-15T12:00:00", 2)]
    public void A_date_without_an_offset_is_Chisinau_time_in_summer_and_winter(string written, int offset)
    {
        var xml = Fixture("signed-by-supplier.xml").Replace("2026-10-27T15:28:53.507", written, StringComparison.Ordinal);

        var issued = InvoiceDocumentReader.Read(xml).IssuedDate!.Value;

        Assert.Equal((12, TimeSpan.FromHours(offset)), (issued.Hour, issued.Offset));
    }

    [Fact]
    public void A_document_in_the_current_format_is_never_read_as_an_older_one()
    {
        var current = InvoiceDocumentReader.Read(Fixture("received-signed.xml"));

        Assert.False(current.OlderFormat);
        Assert.True(current.IssuedByVatPayer);

        // Without its CreationMotiv a current document is refused, whatever the older format allows.
        var withoutMotive = Fixture("received-signed.xml").Replace("<CreationMotiv>4</CreationMotiv>", "", StringComparison.Ordinal);
        Assert.Contains("CreationMotiv", Assert.Throws<FormatException>(() => InvoiceDocumentReader.Read(withoutMotive)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_of_both_formats_in_one_document_are_refused()
    {
        var older = XElement.Parse(Fixture("received-older.xml"));
        var current = XElement.Parse(Fixture("received-signed.xml")).Descendants("Row").First();
        older.Descendants("Merchandises").Single().Add(current);

        var refused = Assert.Throws<FormatException>(() => InvoiceDocumentReader.Read(older.ToString()));

        Assert.Contains("mixes", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TotalCost=\"1519\"", "")]
    [InlineData("UnitPrice=\"1519\"", "")]
    [InlineData("TotalTVA=\"0\"", "")]
    public void What_the_older_format_states_is_required_of_it(string remove, string replacement)
    {
        // Without TotalCost the row is not an older one, so it is held to the current format instead.
        var broken = Fixture("received-older.xml").Replace(remove, replacement, StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => InvoiceDocumentReader.Read(broken));
    }

    [Fact]
    public void A_draft_we_built_reads_back_as_we_built_it()
    {
        var invoice = new Invoice
        {
            SupplierIdno = "1099900000017",
            BuyerIdno = "1099900000028",
            SupplierBankAccount = "MD77AG000000022510000101",
            BuyerBankAccount = "MD24AG000000022510000202",
            DeliveryDate = new DateTimeOffset(2026, 10, 15, 1, 32, 14, 590, TimeSpan.Zero),
            CreationMotive = CreationMotive.SupplyDocumentation,
            CorrelationId = "konta-7",
            Lines = [new InvoiceLine { Code = "1", Name = "Servicii de testare", UnitOfMeasure = "buc", Quantity = 1m, UnitPriceWithoutVat = 10m, VatPercent = 0m }],
        };

        var document = InvoiceDocumentReader.Read(InvoiceXml.Build([invoice]));

        Assert.Null(document.Seria);
        Assert.Null(document.IssuedDate);
        Assert.Equal(invoice.DeliveryDate, document.DeliveryDate);
        Assert.Equal(("1099900000017", "MD77AG000000022510000101", null as string), (document.Supplier.Idno, document.Supplier.BankAccount, document.Supplier.Name));
        Assert.Equal(1, document.CreationMotive);
        Assert.False(document.IssuedByVatPayer);
        Assert.Equal("konta-7", document.CorrelationId);
        Assert.Equal(new EFacturaDocumentLine("1", "Servicii de testare", "buc", 1m, 10m, 10m, "0", 0m, 10m), Assert.Single(document.Lines));
    }

    [Fact]
    public void Several_documents_are_read_in_order_and_one_is_required_by_Read()
    {
        var xml = $"<Documents>{Fixture("received-signed.xml")}{Fixture("received-signed.xml").Replace("000000123", "000000124", StringComparison.Ordinal)}</Documents>";

        Assert.Equal(["000000123", "000000124"], InvoiceDocumentReader.ReadAll(xml).Select(d => d.Number));
        Assert.Throws<FormatException>(() => InvoiceDocumentReader.Read(xml));
    }

    [Fact]
    public void Numbers_are_read_invariantly_whatever_the_culture()
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            // ro-MD writes a comma for the decimal point; the wire never does.
            CultureInfo.CurrentCulture = new CultureInfo("ro-MD");

            Assert.Equal(85.50m, InvoiceDocumentReader.Read(Fixture("received-signed.xml")).Lines[0].UnitPriceWithoutVat);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("<CreationMotiv>4</CreationMotiv>", "", "CreationMotiv")]
    [InlineData("<DeliveryDate>2026-10-12T00:00:00Z</DeliveryDate>", "", "DeliveryDate")]
    [InlineData("UnitOfMeasure=\"kg\"", "", "UnitOfMeasure")]
    [InlineData("Quantity=\"10\"", "Quantity=\"10,5\"", "Quantity")]
    [InlineData("<Document>", "<Factura>", "Factura")]
    public void What_the_schema_requires_is_never_half_read(string remove, string replacement, string named)
    {
        var broken = Fixture("received-signed.xml").Replace(remove, replacement, StringComparison.Ordinal).Replace("</Document>", replacement == "<Factura>" ? "</Factura>" : "</Document>", StringComparison.Ordinal);

        var refused = Assert.Throws<FormatException>(() => InvoiceDocumentReader.Read(broken));

        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_platform_returns_reads_through_EFacturaInvoiceXml()
    {
        var returned = new EFacturaInvoiceXml(new EFacturaInvoiceId("EAA", "000000123"), true, EFacturaInvoiceStatus.SentToBuyer, null, Fixture("received-signed.xml"));

        Assert.Equal("EAA", returned.ReadDocument()!.Seria);
        Assert.Null((returned with { Xml = null }).ReadDocument());
    }
}
