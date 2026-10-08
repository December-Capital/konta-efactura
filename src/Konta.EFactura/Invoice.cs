using System.Globalization;

namespace Konta.EFactura;

/// <summary>One fiscal invoice, in the shape SIA "e-Factura" accepts.</summary>
public sealed class Invoice
{
    /// <summary>Supplier IDNO (13 digits).</summary>
    public required string SupplierIdno { get; init; }

    /// <summary>Buyer IDNO (13 digits).</summary>
    public required string BuyerIdno { get; init; }

    /// <summary>Delivery date and time.</summary>
    public required DateTimeOffset DeliveryDate { get; init; }

    /// <summary>
    /// Supplier bank account. The schema makes it optional, but the platform fails on an invoice
    /// without it ("Object reference not set to an instance of an object").
    /// </summary>
    public required string SupplierBankAccount { get; init; }

    /// <summary>Buyer bank account. Required by the platform, as <see cref="SupplierBankAccount"/> is.</summary>
    public required string BuyerBankAccount { get; init; }

    /// <summary>
    /// Why the invoice is issued (<c>CreationMotiv</c>), which the schema requires on every
    /// document. A VAT payer issues for a delivery or a non-delivery.
    /// </summary>
    public required CreationMotive CreationMotive { get; init; }

    /// <summary>
    /// Our own identifier, echoed back in <c>AdditionalInformation/id</c>. Set it: it is the only
    /// way to reconcile a submission against a platform document before series and number exist.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>The lines.</summary>
    public required IReadOnlyList<InvoiceLine> Lines { get; init; }

    /// <summary>
    /// Checks what the platform will not check for you.
    /// </summary>
    /// <exception cref="InvalidOperationException">The invoice is not internally consistent.</exception>
    public void Validate()
    {
        AssertIdno(SupplierIdno, nameof(SupplierIdno));
        AssertIdno(BuyerIdno, nameof(BuyerIdno));
        AssertPresent(SupplierBankAccount, nameof(SupplierBankAccount));
        AssertPresent(BuyerBankAccount, nameof(BuyerBankAccount));

        if (!Enum.IsDefined(CreationMotive))
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"CreationMotive {(int)CreationMotive} is not one the platform knows."));
        }

        if (Lines.Count == 0)
        {
            throw new InvalidOperationException("An invoice needs at least one line.");
        }

        foreach (var line in Lines)
        {
            line.Validate();
        }
    }

    /// <summary>Total excluding VAT.</summary>
    public decimal TotalWithoutVat => Lines.Sum(l => l.TotalWithoutVat);

    /// <summary>Total VAT.</summary>
    public decimal TotalVat => Lines.Sum(l => l.VatAmount);

    /// <summary>Total including VAT.</summary>
    public decimal TotalWithVat => Lines.Sum(l => l.TotalWithVat);

    private static void AssertPresent(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{field} is required: the platform cannot process an invoice without it.");
        }
    }

    private static void AssertIdno(string idno, string field)
    {
        if (idno.Length != 13 || !idno.All(char.IsAsciiDigit))
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"{field} must be 13 digits; got '{idno}'."));
        }
    }
}

/// <summary>
/// Why a fiscal invoice is issued, as the platform numbers it (<c>CreationMotiv</c> in
/// <c>TaxInvoiceSchema.xsd</c>). The first three are for issuers not registered for VAT, the last
/// two for VAT payers.
/// </summary>
public enum CreationMotive
{
    /// <summary>Not a VAT payer: documenting supplies, or moving assets between parts of one entity.</summary>
    SupplyDocumentation = 1,

    /// <summary>Not a VAT payer: re-invoicing expenses that are compensated.</summary>
    ExpenseReinvoicing = 2,

    /// <summary>Not a VAT payer: a combined invoice, with compensated expenses on a separate line.</summary>
    Combined = 3,

    /// <summary>VAT payer: a supply ("Livrare").</summary>
    Delivery = 4,

    /// <summary>VAT payer: not a supply ("Non-livrare").</summary>
    NonDelivery = 5,
}

/// <summary>One invoice line.</summary>
public sealed class InvoiceLine
{
    /// <summary>Item code.</summary>
    public required string Code { get; init; }

    /// <summary>Item description, as it should appear on the document.</summary>
    public required string Name { get; init; }

    /// <summary>Unit of measure, for example <c>buc</c> or <c>kg</c>.</summary>
    public required string UnitOfMeasure { get; init; }

    /// <summary>Quantity. Negative on a credit line.</summary>
    public required decimal Quantity { get; init; }

    /// <summary>Unit price excluding VAT.</summary>
    public required decimal UnitPriceWithoutVat { get; init; }

    /// <summary>
    /// VAT rate as a whole-number percentage: <c>20</c>, <c>8</c> or <c>0</c> — never <c>0.20</c>.
    /// </summary>
    public required decimal VatPercent { get; init; }

    /// <summary>Line total excluding VAT.</summary>
    public decimal TotalWithoutVat => Round(Quantity * UnitPriceWithoutVat);

    /// <summary>Line VAT.</summary>
    public decimal VatAmount => Round(TotalWithoutVat * VatPercent / 100m);

    /// <summary>Line total including VAT.</summary>
    public decimal TotalWithVat => TotalWithoutVat + VatAmount;

    /// <summary>
    /// Checks the line is self-consistent.
    /// </summary>
    /// <exception cref="InvalidOperationException">The line is not usable.</exception>
    public void Validate()
    {
        if (VatPercent is < 0m or > 100m)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"VatPercent is a whole-number percentage between 0 and 100; got {VatPercent}. Use 20, not 0.20."));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("A line needs a description.");
        }

        // A zero-VAT line still counts towards the document totals. Losing it is a known way to
        // produce a document whose totals disagree with its lines.
        if (Quantity == 0m)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Line '{Name}' has zero quantity."));
        }

        // Sign consistency: a negative quantity must yield a negative line total. The integration
        // guide's own worked example gets this wrong, so it is worth asserting.
        if (decimal.Sign(Quantity) != decimal.Sign(TotalWithoutVat) && TotalWithoutVat != 0m)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Line '{Name}' has quantity {Quantity} but total {TotalWithoutVat}; signs disagree."));
        }
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.ToEven);
}
