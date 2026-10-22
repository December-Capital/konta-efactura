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
/// and the order is not a progression. 99 is not in either guide.
/// </summary>
public enum EFacturaInvoiceStatus
{
    /// <summary>Posted, not signed. Deleted by its platform id (<see cref="EFacturaClient.DeleteDraftAsync"/>) or in the web interface.</summary>
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

    /// <summary>
    /// A draft deleted by <see cref="EFacturaClient.DeleteDraftAsync"/>: what <see cref="EFacturaClient.GetDraftsAsync"/>
    /// answers for it, with its XML still and no id (the test service, 2026-10-22).
    /// </summary>
    DeletedDraft = 99,
}

/// <summary>An invoice's series and number, which the platform assigns.</summary>
/// <param name="Seria">The series, for example <c>EAA</c>.</param>
/// <param name="Number">The number, with its leading zeros.</param>
public sealed record EFacturaInvoiceId(string Seria, string Number);

/// <summary>One invoice as a listing or a status check returned it.</summary>
/// <param name="Id">Series and number; both empty on a draft.</param>
/// <param name="Status">Where the invoice is.</param>
/// <param name="Message">The platform's note on this invoice, when it has one; set when it could not be looked up.</param>
/// <param name="TimeStamp">When the platform answered for it, by its own clock.</param>
/// <param name="PlatformId">
/// The platform's own id for the invoice (<c>Id</c>): set on a draft, the id <see cref="EFacturaPostResult.DraftIds"/>
/// gave and <see cref="EFacturaClient.DeleteDraftAsync"/> takes; null where the platform sends none, as for
/// a signed invoice so far.
/// </param>
public sealed record EFacturaInvoice(EFacturaInvoiceId Id, EFacturaInvoiceStatus Status, string? Message, DateTime TimeStamp, Guid? PlatformId = null);

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

    /// <summary>
    /// Status. The wire always carries one and the platform filters by it, so a search finds
    /// invoices of one status at a time; drafts unless set.
    /// </summary>
    public EFacturaInvoiceStatus Status { get; init; } = EFacturaInvoiceStatus.Draft;

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
/// <param name="DraftIds">
/// The platform's id for each draft it registered (<c>InvoiceIds</c>), in the order it sent them: the
/// handle <see cref="EFacturaClient.DeleteDraftAsync"/> takes and <see cref="EFacturaInvoice.PlatformId"/>
/// lists. Empty from a service that sends none (production's WSDL had none on 2026-10-22). Which id
/// belongs to which invoice is certain only for a batch of one.
/// </param>
public sealed record EFacturaPostResult(
    string? RequestId, int Total, int Posted, IReadOnlyList<EFacturaRefusal> Refusals, string? ErrorMessage, IReadOnlyList<Guid> DraftIds)
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
/// The platform's answer for one invoice of a batch: to a cancellation, refusal or acceptance, or
/// to a request for an invoice by series and number.
/// </summary>
/// <param name="Id">Series and number.</param>
/// <param name="Done">Whether the platform did it (per-invoice <c>Status</c> 2, not 3).</param>
/// <param name="Message">Why not, in the platform's words, for example <c>This action is not allowed for this invoice!</c>.</param>
/// <param name="TimeStamp">When the platform answered for it, by its own clock.</param>
public sealed record EFacturaInvoiceResult(EFacturaInvoiceId Id, bool Done, string? Message, DateTime TimeStamp);

/// <summary>An invoice's XML as the platform holds it (<c>GetInvoicesForSigning</c>, <c>GetInvoicesBySeriaNumber</c>).</summary>
/// <param name="Id">Series and number; both empty on a draft, which gets them only when signed.</param>
/// <param name="Found">Whether the platform returned it (per-invoice <c>Status</c> 2).</param>
/// <param name="Status">Where the invoice is.</param>
/// <param name="Message">Why it was not returned, for example <c>Invoice not found!</c>.</param>
/// <param name="Xml">The <c>&lt;Document&gt;</c>, when found.</param>
/// <param name="PlatformId">The platform's own id, when it sends one; <c>GetInvoicesForSigning</c> sends none, even for a draft.</param>
public sealed record EFacturaInvoiceXml(EFacturaInvoiceId Id, bool Found, EFacturaInvoiceStatus Status, string? Message, string? Xml, Guid? PlatformId = null)
{
    /// <summary>
    /// Our own id from the document's <c>AdditionalInformation/id</c>: the only handle on a draft,
    /// which has no series or number yet.
    /// </summary>
    public string? CorrelationId =>
        string.IsNullOrEmpty(Xml)
            ? null
            : System.Xml.Linq.XElement.Parse(Xml).Element("AdditionalInformation")?.Element("id")?.Value;

    /// <summary>The document read from <see cref="Xml"/>, or null when the platform sent none.</summary>
    /// <returns>The document.</returns>
    /// <exception cref="FormatException">The XML is not a readable document.</exception>
    public EFacturaDocument? ReadDocument() => string.IsNullOrEmpty(Xml) ? null : InvoiceDocumentReader.Read(Xml);
}

/// <summary>
/// What the platform answered to deleting a draft (<c>DeleteDraftInvoice</c>). Not whether it deleted
/// one: it answers the same for an id it never had (<see cref="EFacturaClient.DeleteDraftAsync"/>).
/// </summary>
/// <param name="DraftId">The platform's id of the draft.</param>
/// <param name="Accepted">Whether the platform answered "done" (<c>Status</c> 2).</param>
/// <param name="Message">The platform's message, in its words.</param>
public sealed record EFacturaDraftDeletion(Guid DraftId, bool Accepted, string? Message);

/// <summary>A series and number the platform handed out (<c>GetSeriaAndNumbers</c>).</summary>
/// <param name="Id">The series and number, now reserved for the company.</param>
/// <param name="Done">Whether the platform reserved it (per-entry <c>Status</c> 2).</param>
public sealed record EFacturaReservedNumber(EFacturaInvoiceId Id, bool Done);

/// <summary>An invoice's QR code (<c>GetInvoicesQRcodes</c>).</summary>
/// <param name="Id">Series and number.</param>
/// <param name="Found">Whether the platform returned it.</param>
/// <param name="Message">Why not.</param>
/// <param name="Png">The image, as PNG.</param>
/// <param name="Text">What the code says: series, number, parties, totals and the invoice's address on the platform.</param>
public sealed record EFacturaQrCode(EFacturaInvoiceId Id, bool Found, string? Message, byte[]? Png, string? Text);

/// <summary>A bank account as the platform knows it (<c>GetBankAccountInfo</c>).</summary>
/// <param name="Account">The account number.</param>
/// <param name="BranchCode">The bank branch's code.</param>
/// <param name="BranchTitle">The bank branch's name.</param>
/// <param name="IsRegistered">Whether the account is registered with the tax service for this IDNO.</param>
public sealed record EFacturaBankAccount(string Account, string? BranchCode, string? BranchTitle, bool IsRegistered);

/// <summary>One call recorded by the platform (<c>GetLogs</c>).</summary>
/// <param name="Method">The operation called.</param>
/// <param name="User">Who called it.</param>
/// <param name="Started">When it started.</param>
/// <param name="Ended">When it ended, if it did.</param>
/// <param name="Status">Its status.</param>
/// <param name="Error">Its error, if any.</param>
/// <param name="Response">The answer, as JSON.</param>
public sealed record EFacturaLogEntry(string? Method, string? User, DateTime Started, DateTime? Ended, int Status, string? Error, string? Response);

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
