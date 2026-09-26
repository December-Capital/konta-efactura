using System.Globalization;
using System.Xml.Linq;

namespace Konta.EFactura;

/// <summary>
/// Builds the XML payload SIA "e-Factura" expects from <c>PostInvoices</c>.
/// </summary>
/// <remarks>
/// <para>
/// The schema is SFS's own, documented in the semi-automated integration guide (Chișinău, 2025).
/// It is <b>not</b> UBL and not EN 16931, despite Moldova's stated direction towards Peppol for
/// cross-border trade — so treat this as one serialiser among future others, never as your
/// application's internal model.
/// </para>
/// <para>
/// Shape, from the guide's worked example:
/// </para>
/// <code>
/// &lt;Documents&gt;
///   &lt;Document&gt;
///     &lt;SupplierInfo&gt;
///       &lt;DeliveryDate&gt;2014-04-22T00:00:00.804Z&lt;/DeliveryDate&gt;
///       &lt;Supplier IDNO="1002600001257"&gt;&lt;BankAccount Account="22241410046" /&gt;&lt;/Supplier&gt;
///       &lt;Buyer IDNO="1002600003354"&gt;&lt;BankAccount Account="2224710SV12365037100" /&gt;&lt;/Buyer&gt;
///       &lt;Merchandises&gt;
///         &lt;Row Code="1" Name="…" UnitOfMeasure="buc" Quantity="-1"
///              UnitPriceWithoutTVA="15.00" TotalPriceWithoutTVA="-15.00"
///              TVA="20" TotalTVA="3.00" TotalPrice="-18.00" /&gt;
///       &lt;/Merchandises&gt;
///     &lt;/SupplierInfo&gt;
///     &lt;AdditionalInformation&gt;&lt;id&gt;your-correlation-id&lt;/id&gt;&lt;/AdditionalInformation&gt;
///   &lt;/Document&gt;
/// &lt;/Documents&gt;
/// </code>
/// <para>
/// Two things the guide's own example will mislead you about. First, <c>TVA</c> is a whole-number
/// percentage (<c>20</c>), while the amount attributes are decimals — mixing those up produces a
/// document the platform accepts and an accountant rejects. Second, the example's negative-quantity
/// rows have signs that do not reconcile with their line totals; do not copy it. Compute totals and
/// assert they agree, which is what <see cref="InvoiceLine.Validate"/> does.
/// </para>
/// </remarks>
public static class InvoiceXml
{
    /// <summary>Invariant culture for every serialised number and date. Never the ambient culture.</summary>
    private static readonly CultureInfo Wire = CultureInfo.InvariantCulture;

    /// <summary>Serialises one or more invoices into a <c>&lt;Documents&gt;</c> payload.</summary>
    /// <param name="invoices">The invoices to serialise.</param>
    /// <returns>XML ready to hand to <c>PostInvoices</c>.</returns>
    /// <exception cref="ArgumentException">No invoices were supplied.</exception>
    public static string Build(IReadOnlyList<Invoice> invoices)
    {
        ArgumentNullException.ThrowIfNull(invoices);

        if (invoices.Count == 0)
        {
            throw new ArgumentException("At least one invoice is required.", nameof(invoices));
        }

        var documents = new XElement("Documents", invoices.Select(BuildDocument));

        return documents.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement BuildDocument(Invoice invoice)
    {
        invoice.Validate();

        var supplierInfo = new XElement(
            "SupplierInfo",
            new XElement("DeliveryDate", invoice.DeliveryDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Wire)),
            Party("Supplier", invoice.SupplierIdno, invoice.SupplierBankAccount),
            Party("Buyer", invoice.BuyerIdno, invoice.BuyerBankAccount),
            new XElement("Merchandises", invoice.Lines.Select(Row)));

        var document = new XElement("Document", supplierInfo);

        if (!string.IsNullOrWhiteSpace(invoice.CorrelationId))
        {
            document.Add(new XElement(
                "AdditionalInformation",
                new XElement("id", invoice.CorrelationId)));
        }

        return document;
    }

    private static XElement Party(string name, string idno, string? bankAccount)
    {
        var party = new XElement(name, new XAttribute("IDNO", idno));

        if (!string.IsNullOrWhiteSpace(bankAccount))
        {
            party.Add(new XElement("BankAccount", new XAttribute("Account", bankAccount)));
        }

        return party;
    }

    private static XElement Row(InvoiceLine line) =>
        new(
            "Row",
            new XAttribute("Code", line.Code),
            new XAttribute("Name", line.Name),
            new XAttribute("UnitOfMeasure", line.UnitOfMeasure),
            new XAttribute("Quantity", Decimal(line.Quantity)),
            new XAttribute("UnitPriceWithoutTVA", Decimal(line.UnitPriceWithoutVat)),
            new XAttribute("TotalPriceWithoutTVA", Decimal(line.TotalWithoutVat)),

            // Whole-number percentage: 20, not 0.20.
            new XAttribute("TVA", line.VatPercent.ToString("0.####", Wire)),
            new XAttribute("TotalTVA", Decimal(line.VatAmount)),
            new XAttribute("TotalPrice", Decimal(line.TotalWithVat)));

    private static string Decimal(decimal value) => value.ToString("0.00##", Wire);
}
