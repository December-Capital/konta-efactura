using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Konta.EFactura;

/// <summary>A party to an invoice as the document names it.</summary>
/// <param name="Idno">Its IDNO (or IDNP), as written.</param>
/// <param name="Name">Its name (<c>Title</c>), when the document carries one.</param>
/// <param name="Address">Its address, when given.</param>
/// <param name="VatCode">Its VAT code (<c>CodTVA</c>), when given.</param>
/// <param name="BankAccount">The account on the document (<c>BankAccount/@Account</c>).</param>
/// <param name="BankCode">The bank branch's code.</param>
/// <param name="BankName">The bank branch's name.</param>
public sealed record EFacturaParty(
    string Idno, string? Name, string? Address, string? VatCode, string? BankAccount, string? BankCode, string? BankName);

/// <summary>One row of a document's <c>Merchandises</c>, as the issuer wrote it.</summary>
/// <param name="Code">The item's code, when given.</param>
/// <param name="Name">What was supplied.</param>
/// <param name="UnitOfMeasure">The unit's code from SFS's classifier, for example <c>buc</c>.</param>
/// <param name="Quantity">How many; negative on a correcting row.</param>
/// <param name="UnitPriceWithoutVat">Price of one without VAT.</param>
/// <param name="TotalWithoutVat">The row without VAT, as the issuer computed it.</param>
/// <param name="VatCode">
/// The <c>TVA</c> attribute as written: a code from SFS's classifier (<c>20</c>, <c>8</c>, <c>0</c>,
/// <c>-</c>), not a number to compute with. Which rate it stands for is the caller's question. Null on
/// a row of the older format (<see cref="EFacturaDocument.OlderFormat"/>), which states no rate.
/// </param>
/// <param name="Vat">The row's VAT, as the issuer computed it.</param>
/// <param name="TotalWithVat">The row with VAT.</param>
public sealed record EFacturaDocumentLine(
    string? Code,
    string Name,
    string UnitOfMeasure,
    decimal Quantity,
    decimal UnitPriceWithoutVat,
    decimal TotalWithoutVat,
    string? VatCode,
    decimal Vat,
    decimal TotalWithVat);

/// <summary>
/// One fiscal invoice read back from the platform's XML (<c>GetInvoicesBySeriaNumber</c>,
/// <c>GetInvoicesForSigning</c>), in SFS's own terms. Amounts are as the issuer wrote them, never
/// recomputed here.
/// </summary>
/// <remarks>
/// Invoices issued before <c>TaxInvoiceSchema.xsd</c> are in an older format, which the test service
/// still returns for one signed in 2017: rows with <c>UnitPrice</c> and <c>TotalCost</c> and no
/// <c>TVA</c> code, no <c>CreationMotiv</c>. They are read too, with <see cref="OlderFormat"/> set and
/// what that format does not state left null.
/// </remarks>
/// <param name="Seria">The series, on a signed invoice.</param>
/// <param name="Number">The number, on a signed invoice.</param>
/// <param name="IssuedDate">
/// When it was issued, on a signed invoice. On one posted through the API and signed later, the
/// platform wrote when the draft was registered, not when it was signed (2026-10-27).
/// </param>
/// <param name="DeliveryDate">The delivery date, which every document has.</param>
/// <param name="Supplier">The supplier.</param>
/// <param name="Buyer">The buyer.</param>
/// <param name="CreationMotive">
/// <c>CreationMotiv</c> as written: 1 to 3 from an issuer outside VAT, 4 (delivery) or 5
/// (non-delivery) from a VAT payer. Null in the older format, which has none.
/// </param>
/// <param name="Notes">The issuer's note.</param>
/// <param name="AttachedDocuments">The documents the issuer lists as attached.</param>
/// <param name="CorrelationId">The issuer's own id, <c>AdditionalInformation/id</c>, when it set one.</param>
/// <param name="Lines">The rows.</param>
/// <param name="OlderFormat">Whether it is in the format before SFS's current schema (see the remarks).</param>
public sealed record EFacturaDocument(
    string? Seria,
    string? Number,
    DateTimeOffset? IssuedDate,
    DateTimeOffset DeliveryDate,
    EFacturaParty Supplier,
    EFacturaParty Buyer,
    int? CreationMotive,
    string? Notes,
    string? AttachedDocuments,
    string? CorrelationId,
    IReadOnlyList<EFacturaDocumentLine> Lines,
    bool OlderFormat = false)
{
    /// <summary>The rows without VAT.</summary>
    public decimal TotalWithoutVat => Lines.Sum(l => l.TotalWithoutVat);

    /// <summary>The rows' VAT.</summary>
    public decimal TotalVat => Lines.Sum(l => l.Vat);

    /// <summary>The rows with VAT.</summary>
    public decimal TotalWithVat => Lines.Sum(l => l.TotalWithVat);

    /// <summary>
    /// Whether the issuer wrote it as a VAT payer (<c>CreationMotiv</c> 4 or 5); null when the document
    /// does not say, as in the older format. A VAT code on the supplier is not the same statement.
    /// </summary>
    public bool? IssuedByVatPayer => CreationMotive is { } motive ? motive is 4 or 5 : null;
}

/// <summary>Reads the platform's invoice XML back into <see cref="EFacturaDocument"/>s.</summary>
/// <remarks>
/// Tolerant of what the schema leaves optional and strict on what it requires: a document without a
/// supplier, a buyer, a delivery date, <c>CreationMotiv</c> or a row is refused with a
/// <see cref="FormatException"/> naming what is missing, never half read. A document whose rows are
/// in the older format (<c>TotalCost</c> rather than <c>TotalPriceWithoutTVA</c>) is held to that
/// format instead, which has no <c>CreationMotiv</c> and no <c>TVA</c> code; one mixing both kinds
/// of row is refused. Blank values, such as the ten spaces of an older document's missing VAT code,
/// are absent. Numbers and dates are read invariantly, whatever the ambient culture (PROTOCOL.md trap 4).
/// </remarks>
public static class InvoiceDocumentReader
{
    private static readonly CultureInfo Wire = CultureInfo.InvariantCulture;

    /// <summary>The platform's clock (SOAP-NOTES.md: local time, +03:00 in summer, +02:00 in winter).</summary>
    private static readonly TimeZoneInfo Chisinau = TimeZoneInfo.FindSystemTimeZoneById("Europe/Chisinau");

    /// <summary>Reads one <c>&lt;Document&gt;</c>, or the only one in a <c>&lt;Documents&gt;</c>.</summary>
    /// <param name="xml">The XML, as the platform returned it.</param>
    /// <returns>The document.</returns>
    /// <exception cref="FormatException">The XML is not one readable document.</exception>
    public static EFacturaDocument Read(string xml)
    {
        var documents = ReadAll(xml);

        return documents.Count == 1
            ? documents[0]
            : throw new FormatException(string.Create(Wire, $"Expected one document, found {documents.Count}."));
    }

    /// <summary>Reads every document in a <c>&lt;Documents&gt;</c>, or the one <c>&lt;Document&gt;</c>.</summary>
    /// <param name="xml">The XML.</param>
    /// <returns>The documents, in order.</returns>
    /// <exception cref="FormatException">The XML is not readable as documents.</exception>
    public static IReadOnlyList<EFacturaDocument> ReadAll(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XElement root;

        try
        {
            root = XElement.Parse(xml);
        }
        catch (XmlException exception)
        {
            throw new FormatException("The invoice XML is not well formed: " + exception.Message, exception);
        }

        return root.Name.LocalName switch
        {
            "Document" => [Document(root)],
            "Documents" => [.. root.Elements("Document").Select(Document)],
            var other => throw new FormatException($"Expected <Document> or <Documents>, found <{other}>."),
        };
    }

    private static EFacturaDocument Document(XElement document)
    {
        var info = document.Element("SupplierInfo") ?? throw Missing("SupplierInfo");
        var elements = (info.Element("Merchandises") ?? throw Missing("Merchandises")).Elements("Row").ToList();

        if (elements.Count == 0)
        {
            throw Missing("Merchandises/Row");
        }

        var older = elements.Select(IsOlderRow).Distinct().ToList() switch
        {
            [var one] => one,
            _ => throw new FormatException("The invoice mixes rows of the current format with rows of the older one (TotalCost)."),
        };
        var rows = elements.Select(e => older ? OlderRow(e) : Row(e)).ToList();
        var motive = info.Element("CreationMotiv");

        return new EFacturaDocument(
            Text(info.Element("Seria")),
            Text(info.Element("Number")),
            info.Element("IssuedDate") is { } issued ? Date(issued, "IssuedDate") : null,
            Date(info.Element("DeliveryDate") ?? throw Missing("DeliveryDate"), "DeliveryDate"),
            Party(info.Element("Supplier") ?? throw Missing("Supplier")),
            Party(info.Element("Buyer") ?? throw Missing("Buyer")),
            motive is not null ? Integer(motive, "CreationMotiv") : older ? null : throw Missing("CreationMotiv"),
            Text(info.Element("Notes")),
            Text(info.Element("AttachedDocuments")),
            Text(document.Element("AdditionalInformation")?.Element("id")),
            rows,
            older);
    }

    // The older format's rows give the row without VAT as TotalCost; the current one's never do.
    private static bool IsOlderRow(XElement row) =>
        row.Attribute("TotalCost") is not null && row.Attribute("TotalPriceWithoutTVA") is null && row.Attribute("UnitPriceWithoutTVA") is null;

    private static EFacturaParty Party(XElement party)
    {
        var bank = party.Element("BankAccount");

        return new EFacturaParty(
            Text(party.Attribute("IDNO")?.Value) ?? throw Missing(party.Name.LocalName + "/@IDNO"),
            Text(party.Attribute("Title")?.Value),
            Text(party.Attribute("Address")?.Value),
            Text(party.Attribute("CodTVA")?.Value),
            Text(bank?.Attribute("Account")?.Value),
            Text(bank?.Attribute("BranchCode")?.Value),
            Text(bank?.Attribute("BranchTitle")?.Value));
    }

    private static EFacturaDocumentLine Row(XElement row)
    {
        string Required(string attribute) =>
            Text(row.Attribute(attribute)?.Value) ?? throw Missing("Row/@" + attribute);

        return new EFacturaDocumentLine(
            Text(row.Attribute("Code")?.Value),
            Required("Name"),
            Required("UnitOfMeasure"),
            Amount(Required("Quantity"), "Quantity"),
            Amount(Required("UnitPriceWithoutTVA"), "UnitPriceWithoutTVA"),
            Amount(Required("TotalPriceWithoutTVA"), "TotalPriceWithoutTVA"),
            Required("TVA"),
            Amount(Required("TotalTVA"), "TotalTVA"),
            Amount(Required("TotalPrice"), "TotalPrice"));
    }

    /// <summary>
    /// A row of the older format: <c>UnitPrice</c> and <c>TotalCost</c> without VAT (as the paper form's
    /// columns are; the one such invoice seen charged no VAT, so the two readings could not be told
    /// apart there), <c>TotalTVA</c> and <c>TotalPrice</c> as now, and no rate.
    /// </summary>
    private static EFacturaDocumentLine OlderRow(XElement row)
    {
        string Required(string attribute) =>
            Text(row.Attribute(attribute)?.Value) ?? throw Missing("Row/@" + attribute);

        return new EFacturaDocumentLine(
            Text(row.Attribute("Code")?.Value),
            Required("Name"),
            Required("UnitOfMeasure"),
            Amount(Required("Quantity"), "Quantity"),
            Amount(Required("UnitPrice"), "UnitPrice"),
            Amount(Required("TotalCost"), "TotalCost"),
            Text(row.Attribute("TVA")?.Value),
            Amount(Required("TotalTVA"), "TotalTVA"),
            Amount(Required("TotalPrice"), "TotalPrice"));
    }

    private static decimal Amount(string value, string what) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Wire, out var amount)
            ? amount
            : throw new FormatException($"Row/@{what} is not a decimal: '{value}'.");

    private static int Integer(XElement element, string what) =>
        int.TryParse(element.Value.Trim(), NumberStyles.Integer, Wire, out var value)
            ? value
            : throw new FormatException($"{what} is not a whole number: '{element.Value}'.");

    /// <summary>
    /// A date as written. One without an offset is the platform's own clock, Chișinău time: the
    /// <c>IssuedDate</c> of an invoice signed on 2026-10-27 read <c>2026-10-27T15:28:53.507</c> for
    /// 13:28:53 UTC. Never the reading machine's zone, which <see cref="XmlConvert"/> would assume.
    /// </summary>
    private static DateTimeOffset Date(XElement element, string what)
    {
        var value = element.Value.Trim();

        try
        {
            var written = XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.RoundtripKind);

            return written.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(written, Chisinau.GetUtcOffset(written))
                : XmlConvert.ToDateTimeOffset(value);
        }
        catch (FormatException exception)
        {
            throw new FormatException($"{what} is not a date: '{element.Value}'.", exception);
        }
    }

    private static string? Text(XElement? element) => Text(element?.Value);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static FormatException Missing(string what) => new($"The invoice has no {what}, which the schema requires.");
}
