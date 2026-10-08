using System.Globalization;
using System.Text.RegularExpressions;

namespace Konta.EFactura;

/// <summary>The side of an invoice a call speaks for (<c>ActorRole</c>).</summary>
public enum EFacturaActorRole
{
    /// <summary>The company issued the invoice.</summary>
    Supplier = 1,

    /// <summary>The company received it.</summary>
    Buyer = 2,

    /// <summary>The company carries the goods.</summary>
    Transporter = 3,
}

/// <summary>
/// Where an invoice is in its life on the platform (<c>InvoiceStatus</c>). 4 and 9 are not issued,
/// and the order is not a progression.
/// </summary>
public enum EFacturaInvoiceStatus
{
    /// <summary>Posted, not signed. Deletable only in the web interface.</summary>
    Draft = 0,

    /// <summary>Signed by the supplier.</summary>
    SignedBySupplier = 1,

    /// <summary>Refused by the buyer. Final.</summary>
    RefusedByBuyer = 2,

    /// <summary>Accepted by the buyer.</summary>
    AcceptedByBuyer = 3,

    /// <summary>Cancelled by the supplier.</summary>
    CancelledBySupplier = 5,

    /// <summary>Archived.</summary>
    Archived = 6,

    /// <summary>Sent to the buyer.</summary>
    SentToBuyer = 7,

    /// <summary>Signed by the buyer.</summary>
    SignedByBuyer = 8,

    /// <summary>The goods or services were received.</summary>
    Transported = 10,
}

/// <summary>An invoice's series and number, which the platform assigns.</summary>
/// <param name="Seria">The series, for example <c>EAA</c>.</param>
/// <param name="Number">The number, with its leading zeros.</param>
public sealed record EFacturaInvoiceId(string Seria, string Number);

/// <summary>One invoice as a listing or a status check returned it.</summary>
/// <param name="Id">Series and number.</param>
/// <param name="Status">Where the invoice is.</param>
/// <param name="Message">The platform's note on this invoice, when it has one; set when it could not be looked up.</param>
/// <param name="TimeStamp">When the platform answered for it, by its own clock.</param>
public sealed record EFacturaInvoice(EFacturaInvoiceId Id, EFacturaInvoiceStatus Status, string? Message, DateTime TimeStamp);

/// <summary>
/// What <see cref="EFacturaClient.SearchInvoicesAsync"/> looks for. Every criterion is optional; a
/// date range counts only when its start is given.
/// </summary>
public sealed record EFacturaSearch
{
    /// <summary>Our own id, as posted in <see cref="Invoice.CorrelationId"/> (<c>APIeInvoiceId</c>).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Series.</summary>
    public string? Seria { get; init; }

    /// <summary>Number.</summary>
    public string? Number { get; init; }

    /// <summary>Supplier IDNO.</summary>
    public string? SupplierIdno { get; init; }

    /// <summary>Buyer IDNO.</summary>
    public string? BuyerIdno { get; init; }

    /// <summary>Status. The wire always carries one; left out, it sends 0, whose effect is still to confirm.</summary>
    public EFacturaInvoiceStatus? Status { get; init; }

    /// <summary>Issued on or after.</summary>
    public DateTime? IssuedFrom { get; init; }

    /// <summary>Issued on or before.</summary>
    public DateTime? IssuedTo { get; init; }

    /// <summary>Delivered on or after.</summary>
    public DateTime? DeliveredFrom { get; init; }

    /// <summary>Delivered on or before.</summary>
    public DateTime? DeliveredTo { get; init; }

    /// <summary>Registered on the platform on or after.</summary>
    public DateTime? RegisteredFrom { get; init; }

    /// <summary>Registered on the platform on or before.</summary>
    public DateTime? RegisteredTo { get; init; }
}

/// <summary>A company or person as the platform knows them (<c>GetTaxpayersInfo</c>).</summary>
/// <param name="FiscalCode">The IDNO or IDNP asked about.</param>
/// <param name="Name">Name.</param>
/// <param name="Address">Address, when known.</param>
/// <param name="VatCode">VAT registration code, when registered.</param>
/// <param name="Kind">1 legal entity, 2 natural person, 3 non-resident.</param>
/// <param name="InTaxRegistry">Whether the tax registry has them.</param>
/// <param name="IsEFacturaActor">Whether they use e-Factura, so can receive an invoice there.</param>
public sealed record EFacturaTaxpayer(
    string FiscalCode, string? Name, string? Address, string? VatCode, int Kind, bool InTaxRegistry, bool IsEFacturaActor);

/// <summary>One invoice of a posted batch that the platform refused.</summary>
/// <param name="Order">Its place in the batch, from 0.</param>
/// <param name="Message">Why, in the platform's words (Romanian).</param>
public sealed record EFacturaRefusal(int Order, string Message);

/// <summary>
/// What <c>PostInvoices</c> answered. The platform reports a batch it read but refused, in whole or
/// in part, as done; only <see cref="AllPosted"/> says every invoice went in.
/// </summary>
/// <param name="RequestId">Our id for the request, echoed.</param>
/// <param name="Total">Invoices the platform read in the batch.</param>
/// <param name="Posted">Invoices it registered.</param>
/// <param name="Refusals">The invoices it refused, and why.</param>
/// <param name="ErrorMessage">The platform's message as it sent it.</param>
public sealed record EFacturaPostResult(string? RequestId, int Total, int Posted, IReadOnlyList<EFacturaRefusal> Refusals, string? ErrorMessage)
{
    /// <summary>Whether every invoice in the batch was registered.</summary>
    public bool AllPosted => Total > 0 && Posted == Total;

    // "Invoice order = 0, error: Autorul facturii nu este desemnat drept furnizor.\n\n", one per refused invoice.
    private static readonly Regex Refusal = new(
        @"Invoice order = (\d+), error: (.*?)(?=Invoice order = \d+, error:|\z)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    internal static IReadOnlyList<EFacturaRefusal> ParseRefusals(string? errorMessage) =>
        string.IsNullOrWhiteSpace(errorMessage)
            ? []
            : Refusal.Matches(errorMessage)
                .Select(m => new EFacturaRefusal(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value.Trim()))
                .ToList();
}

/// <summary>
/// The platform answered a call with an execution error (<c>Status</c> 3), such as a batch that is
/// not valid against its schema.
/// </summary>
public sealed class EFacturaException : Exception
{
    /// <summary>An execution error the platform reported.</summary>
    /// <param name="operation">The operation called.</param>
    /// <param name="requestId">The request's id, echoed.</param>
    /// <param name="message">The platform's message, when it gave one.</param>
    public EFacturaException(string operation, string? requestId, string? message)
        : base($"e-Factura {operation} failed: {message ?? "no message"}")
    {
        Operation = operation;
        RequestId = requestId;
        PlatformMessage = message;
    }

    /// <summary>The operation called.</summary>
    public string Operation { get; }

    /// <summary>The request's id, echoed.</summary>
    public string? RequestId { get; }

    /// <summary>The platform's message as it sent it.</summary>
    public string? PlatformMessage { get; }
}
