using System.ServiceModel;
using Konta.EFactura.Generated;

namespace Konta.EFactura;

/// <summary>Which SIA "e-Factura" service a client talks to.</summary>
public enum EFacturaEnvironment
{
    /// <summary>SFS's test service. It answers only client addresses SFS has registered.</summary>
    Test,

    /// <summary>The real service.</summary>
    Production,
}

/// <summary>The service addresses, from SFS's integration package (<c>spec/sfs/README.md</c>).</summary>
public static class EFacturaEndpoints
{
    /// <summary>The test service.</summary>
    public static readonly Uri Test = new("https://apiefactura-pre.sfs.md/Service.svc");

    /// <summary>The real service.</summary>
    public static readonly Uri Production = new("https://efactura-api.sfs.md/Service.svc");

    /// <summary>The address of an environment.</summary>
    /// <param name="environment">Test or production.</param>
    /// <returns>Its service address.</returns>
    public static Uri For(EFacturaEnvironment environment) =>
        environment switch
        {
            EFacturaEnvironment.Test => Test,
            EFacturaEnvironment.Production => Production,
            _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unknown e-Factura environment."),
        };
}

/// <summary>What the service answered to <see cref="EFacturaClient.TestAsync"/>.</summary>
/// <param name="RequestId">The service's id for the request.</param>
/// <param name="Status">The service's status code, as it sent it. Its meanings are not documented yet.</param>
/// <param name="TimeStamp">When the service answered, by its own clock.</param>
public sealed record EFacturaTestResult(string? RequestId, int Status, DateTime TimeStamp);

/// <summary>
/// A connection to SIA "e-Factura" for one legal entity's API user. The contract is generated from
/// <c>spec/sfs/Service.wsdl</c> into <c>Generated/</c> and kept internal: callers get typed methods
/// here as each operation is confirmed against the test service, never the wire shapes.
/// </summary>
/// <remarks>
/// The API user is created in e-Factura by the company's manager, and can do everything the platform
/// allows for that company. Keep its password out of source code, configuration files under version
/// control and logs.
/// </remarks>
public sealed class EFacturaClient : IAsyncDisposable
{
    /// <summary>The largest response accepted: invoice batches with attachments are large.</summary>
    public const int MaxMessageBytes = 32 * 1024 * 1024;

    private readonly ServiceClient _client;

    /// <summary>Opens nothing yet: the first call connects.</summary>
    /// <param name="environment">Test or production.</param>
    /// <param name="userName">The company's API user.</param>
    /// <param name="password">Its password.</param>
    /// <param name="timeout">How long one call may take; two minutes when not given.</param>
    public EFacturaClient(EFacturaEnvironment environment, string userName, string password, TimeSpan? timeout = null)
        : this(EFacturaEndpoints.For(environment), userName, password, timeout)
    {
    }

    /// <summary>For an address other than the two published ones, such as a local stand-in in tests.</summary>
    /// <param name="address">The service address.</param>
    /// <param name="userName">The company's API user.</param>
    /// <param name="password">Its password.</param>
    /// <param name="timeout">How long one call may take; two minutes when not given.</param>
    public EFacturaClient(Uri address, string userName, string password, TimeSpan? timeout = null)
        : this(address, userName, password, timeout, proxy: null, recorder: null)
    {
    }

    /// <summary>
    /// For the live tests: a proxy to reach the test service from an address SFS has not
    /// registered, and a recorder that is handed each SOAP message as it is sent and received.
    /// </summary>
    internal EFacturaClient(Uri address, string userName, string password, TimeSpan? timeout, Uri? proxy, Action<string, string>? recorder)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var binding = Binding(timeout ?? TimeSpan.FromMinutes(2));
        if (proxy is not null)
        {
            binding.UseDefaultWebProxy = false;
            binding.ProxyAddress = proxy;
        }

        _client = new ServiceClient(binding, new EndpointAddress(address));
        _client.ClientCredentials.UserName.UserName = userName;
        _client.ClientCredentials.UserName.Password = password;

        if (recorder is not null)
        {
            _client.Endpoint.EndpointBehaviors.Add(new MessageRecorder(recorder));
        }
    }

    /// <summary>The address this client calls.</summary>
    public Uri Address => _client.Endpoint.Address.Uri;

    /// <summary>
    /// The service's <c>Test</c> operation: proves the address is reachable from here and the API
    /// user's credentials are accepted, without touching any invoice.
    /// </summary>
    /// <param name="message">Anything; the service is expected to answer it.</param>
    /// <param name="cancellationToken">Cancellation. The call itself may still finish on the server.</param>
    /// <returns>The service's answer.</returns>
    public async Task<EFacturaTestResult> TestAsync(string message, CancellationToken cancellationToken = default)
    {
        var response = await _client.TestAsync(message).WaitAsync(cancellationToken).ConfigureAwait(false);

        return new EFacturaTestResult(response.RequestId, response.Status, response.TimeStamp);
    }

    /// <summary>
    /// Posts invoices the company issued, unsigned, as the semi-automated guide describes: they land
    /// as drafts, and a person signs them in the web interface. The answer carries no series or
    /// number; find the invoices afterwards by <see cref="Invoice.CorrelationId"/>.
    /// </summary>
    /// <param name="invoices">One or more invoices, all with this company as supplier.</param>
    /// <param name="requestId">Our id for the request; a new one when not given. Keep it to reconcile a timeout.</param>
    /// <param name="cancellationToken">Cancellation. The call itself may still finish on the server: check before posting again.</param>
    /// <returns>How many the platform read and registered, and why it refused the others.</returns>
    /// <exception cref="EFacturaException">The batch is not valid against the platform's schema.</exception>
    public async Task<EFacturaPostResult> PostInvoicesAsync(
        IReadOnlyList<Invoice> invoices, string? requestId = null, CancellationToken cancellationToken = default)
    {
        var request = new PostInvocesRequest
        {
            RequestId = requestId ?? Guid.NewGuid().ToString(),
            ActorRole = (int)EFacturaActorRole.Supplier,
            InvoicesXml = InvoiceXml.Build(invoices),
            InvoicesXmlStatus = 0, // unsigned
        };

        var response = Checked("PostInvoices", await _client.PostInvoicesAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false), r => r.ErrorMessage);

        return new EFacturaPostResult(
            response.RequestId,
            response.TotalInvoices,
            response.TotalInvoicesPosted,
            EFacturaPostResult.ParseRefusals(response.ErrorMessage),
            response.ErrorMessage);
    }

    /// <summary>Where each of the given invoices is now (<c>CheckInvoicesStatus</c>).</summary>
    /// <remarks>The test service answered this operation with HTTP 502 on every call on 2026-10-08.</remarks>
    /// <param name="ids">Series and numbers.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One entry per invoice the platform answered for.</returns>
    public async Task<IReadOnlyList<EFacturaInvoice>> CheckInvoicesStatusAsync(
        IEnumerable<EFacturaInvoiceId> ids, CancellationToken cancellationToken = default)
    {
        var response = Checked("CheckInvoicesStatus", await _client.CheckInvoicesStatusAsync(Identified(ids)).WaitAsync(cancellationToken).ConfigureAwait(false));

        return Listed(response.Results);
    }

    /// <summary>Invoices matching the given criteria (<c>SearchInvoices</c>).</summary>
    /// <param name="role">Which side of the invoices the company is on.</param>
    /// <param name="search">The criteria; leave out what does not matter.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The invoices found.</returns>
    public async Task<IReadOnlyList<EFacturaInvoice>> SearchInvoicesAsync(
        EFacturaActorRole role, EFacturaSearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);

        var request = new SearchRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            ActorRole = (int)role,
            Parameters = new SearchParameters
            {
                APIeInvoiceId = search.CorrelationId,
                BuyerIDNO = search.BuyerIdno,
                SupplierIDNO = search.SupplierIdno,
                Seria = search.Seria,
                Number = search.Number,
                InvoiceStatus = (int)search.Status,
                IssuedOn = Range(search.IssuedFrom, search.IssuedTo),
                DeliveredOn = Range(search.DeliveredFrom, search.DeliveredTo),
                RegisteredOn = Range(search.RegisteredFrom, search.RegisteredTo),
            },
        };

        var response = Checked("SearchInvoices", await _client.SearchInvoicesAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return Listed(response.Results);
    }

    /// <summary>Invoices buyers have accepted, for the company in the given role (<c>GetAcceptedInvoices</c>).</summary>
    /// <param name="role">Which side the company is on.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The invoices.</returns>
    public async Task<IReadOnlyList<EFacturaInvoice>> GetAcceptedInvoicesAsync(EFacturaActorRole role, CancellationToken cancellationToken = default)
    {
        var request = new ActorBaseRequest { RequestId = Guid.NewGuid().ToString(), ActorRole = (int)role };
        var response = Checked("GetAcceptedInvoices", await _client.GetAcceptedInvoicesAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return Listed(response.Results);
    }

    /// <summary>Invoices buyers have refused, for the company in the given role (<c>GetRejectedInvoices</c>).</summary>
    /// <param name="role">Which side the company is on.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The invoices.</returns>
    public async Task<IReadOnlyList<EFacturaInvoice>> GetRejectedInvoicesAsync(EFacturaActorRole role, CancellationToken cancellationToken = default)
    {
        var request = new ActorBaseRequest { RequestId = Guid.NewGuid().ToString(), ActorRole = (int)role };
        var response = Checked("GetRejectedInvoices", await _client.GetRejectedInvoicesAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return Listed(response.Results);
    }

    /// <summary>
    /// What the platform knows of the given IDNOs or IDNPs (<c>GetTaxpayersInfo</c>). Any code can be
    /// asked about, not only counterparties the company already trades with.
    /// </summary>
    /// <param name="fiscalCodes">IDNOs or IDNPs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One entry per code the platform found.</returns>
    public async Task<IReadOnlyList<EFacturaTaxpayer>> GetTaxpayersAsync(IEnumerable<string> fiscalCodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fiscalCodes);

        // The platform answers this one with Status 1, no RequestId and a zero TimeStamp, even when it found everyone.
        var request = new TaxpayersRequest { RequestId = Guid.NewGuid().ToString(), FiscalCodes = fiscalCodes.ToArray() };
        var response = Checked("GetTaxpayersInfo", await _client.GetTaxpayersInfoAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return (response.Results ?? [])
            .Select(t => new EFacturaTaxpayer(t.IDNO, t.Name, t.Address, t.CodTVA, t.TaxpayerType, t.ExistInTaxRegistry, t.IsEFacturaActor))
            .ToList();
    }

    /// <summary>
    /// The company's invoices waiting for a signature, with their XML (<c>GetInvoicesForSigning</c>).
    /// As supplier, these are the drafts it posted: the way to see them, since a draft has no series
    /// or number yet. Each carries our <see cref="EFacturaInvoiceXml.CorrelationId"/>.
    /// </summary>
    /// <param name="role">Which side the company signs for.</param>
    /// <param name="alreadySignedOnce">For the supplier: false for invoices not signed at all, true for those with a first signature.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The invoices. A posted draft appears here minutes after posting, not at once.</returns>
    public async Task<IReadOnlyList<EFacturaInvoiceXml>> GetInvoicesForSigningAsync(
        EFacturaActorRole role, bool alreadySignedOnce = false, CancellationToken cancellationToken = default)
    {
        var request = new SignRequest { RequestId = Guid.NewGuid().ToString(), ActorRole = (int)role, Order = alreadySignedOnce ? 2 : 1 };
        var response = Checked("GetInvoicesForSigning", await _client.GetInvoicesForSigningAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false), r => r.Message);

        return WithXml(response.Results);
    }

    /// <summary>Invoices by series and number, with their XML (<c>GetInvoicesBySeriaNumber</c>).</summary>
    /// <param name="ids">Series and numbers.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One entry per invoice asked for; one the company may not see is not <see cref="EFacturaInvoiceXml.Found"/>.</returns>
    public async Task<IReadOnlyList<EFacturaInvoiceXml>> GetInvoicesAsync(IEnumerable<EFacturaInvoiceId> ids, CancellationToken cancellationToken = default)
    {
        var response = Checked("GetInvoicesBySeriaNumber", await _client.GetInvoicesBySeriaNumberAsync(Identified(ids)).WaitAsync(cancellationToken).ConfigureAwait(false), r => r.Message);

        return WithXml(response.Results);
    }

    /// <summary>The QR codes printed on the given invoices (<c>GetInvoicesQRcodes</c>).</summary>
    /// <param name="ids">Series and numbers.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One entry per invoice asked for.</returns>
    public async Task<IReadOnlyList<EFacturaQrCode>> GetQrCodesAsync(IEnumerable<EFacturaInvoiceId> ids, CancellationToken cancellationToken = default)
    {
        var response = Checked("GetInvoicesQRcodes", await _client.GetInvoicesQRcodesAsync(Identified(ids)).WaitAsync(cancellationToken).ConfigureAwait(false));

        return (response.Results ?? [])
            .Select(q => new EFacturaQrCode(new EFacturaInvoiceId(q.Seria, q.Number), q.Status == 2, q.Message, q.QRCode, q.QRCodeText))
            .ToList();
    }

    /// <summary>
    /// The printable PDF of the given invoices (<c>GetInvoicesContentForPrint</c>). Not yet seen
    /// with a signed invoice: for one it cannot find, the platform answers with no content.
    /// </summary>
    /// <param name="ids">Series and numbers.</param>
    /// <param name="role">Which side the company prints for.</param>
    /// <param name="landscape">Landscape pages rather than portrait.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The PDF, or null when the platform sent none.</returns>
    public async Task<byte[]?> GetPdfAsync(
        IEnumerable<EFacturaInvoiceId> ids, EFacturaActorRole role, bool landscape = false, CancellationToken cancellationToken = default)
    {
        var identified = Identified(ids);
        var request = new InvoicesContentRequest
        {
            RequestId = identified.RequestId,
            SeriaAndNumbers = identified.SeriaAndNumbers,
            ActorRole = (int)role,
            Orientation = landscape ? 2 : 1, // the guide says "Portrait" or "Landscape"; the WSDL wants a number
        };
        var response = Checked("GetInvoicesContentForPrint", await _client.GetInvoicesContentForPrintAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return response.Result?.Content is { Length: > 0 } content ? content : null;
    }

    /// <summary>
    /// Cancels invoices the company issued (<c>PostCanceledInvoices</c>). In the semi-automated mode
    /// the guide leaves this to a person in the web interface; the API offers it all the same.
    /// </summary>
    /// <param name="invoices">Series, number and why.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One result per invoice.</returns>
    public async Task<IReadOnlyList<EFacturaInvoiceResult>> CancelInvoicesAsync(
        IEnumerable<(EFacturaInvoiceId Id, string Reason)> invoices, CancellationToken cancellationToken = default)
    {
        var request = new CanceledRequest { RequestId = Guid.NewGuid().ToString(), InvoicesComments = Commented(invoices) };
        var response = Checked("PostCanceledInvoices", await _client.PostCanceledInvoicesAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return Decided(response.Results);
    }

    /// <summary>Refuses invoices the company received (<c>PostRejectedInvoices</c>).</summary>
    /// <param name="invoices">Series, number and why.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One result per invoice.</returns>
    public async Task<IReadOnlyList<EFacturaInvoiceResult>> RejectInvoicesAsync(
        IEnumerable<(EFacturaInvoiceId Id, string Reason)> invoices, CancellationToken cancellationToken = default)
    {
        var request = new RejectRequest { RequestId = Guid.NewGuid().ToString(), InvoicesComments = Commented(invoices) };
        var response = Checked("PostRejectedInvoices", await _client.PostRejectedInvoicesAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return Decided(response.Results);
    }

    /// <summary>Accepts invoices the company received (<c>PostAcceptedInvoices</c>).</summary>
    /// <param name="ids">Series and numbers.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One result per invoice.</returns>
    public async Task<IReadOnlyList<EFacturaInvoiceResult>> AcceptInvoicesAsync(IEnumerable<EFacturaInvoiceId> ids, CancellationToken cancellationToken = default)
    {
        var identified = Identified(ids);
        var request = new AcceptedRequest { RequestId = identified.RequestId, SeriaAndNumbers = identified.SeriaAndNumbers };
        var response = Checked("PostAcceptedInvoices", await _client.PostAcceptedInvoicesAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return Decided(response.Results);
    }

    /// <summary>
    /// The bank accounts the tax service has registered for an IDNO (<c>GetBankAccountInfo</c>).
    /// Use one of these on an invoice rather than an account typed by hand.
    /// </summary>
    /// <param name="idno">The company.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The accounts. The platform puts a blank entry first whatever is asked, and answers the same
    /// whether or not the request names an account; the blank entry is left out.
    /// </returns>
    public async Task<IReadOnlyList<EFacturaBankAccount>> GetBankAccountsAsync(string idno, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idno);

        var request = new BankAccountRequest { RequestId = Guid.NewGuid().ToString(), IDNO = idno };
        var response = Checked("GetBankAccountInfo", await _client.GetBankAccountInfoAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return (response.Results ?? [])
            .Where(b => !string.IsNullOrWhiteSpace(b.AccountNumber))
            .Select(b => new EFacturaBankAccount(b.AccountNumber, Blank(b.BranchCode), Blank(b.BranchTitle), b.IsRegistered))
            .ToList();
    }

    /// <summary>
    /// The platform's record of this API user's calls (<c>GetLogs</c>). Both ends are required: the
    /// test service fails with an HTML error page when they are left out. It has returned nothing so far.
    /// </summary>
    /// <param name="from">Start.</param>
    /// <param name="to">End.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The calls.</returns>
    public async Task<IReadOnlyList<EFacturaLogEntry>> GetLogsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var request = new LogsRequest { RequestId = Guid.NewGuid().ToString(), From = from, To = to };
        var response = Checked("GetLogs", await _client.GetLogsAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false));

        return (response.Results ?? [])
            .Select(l => new EFacturaLogEntry(l.Method, l.Username, l.StartDateTime, l.EndDateTime, l.Status, l.Error, l.Response))
            .ToList();
    }

    /// <summary>Status 3 is an execution error; 1 (accepted) and 2 (done) carry a usable answer.</summary>
    internal static T Checked<T>(string operation, T response, Func<T, string?>? message = null)
        where T : BaseResponse
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Status == 3
            ? throw new EFacturaException(operation, response.RequestId, message?.Invoke(response))
            : response;
    }

    private static InvoicesRequest Identified(IEnumerable<EFacturaInvoiceId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var identifiers = ids.Select(i => new InvoiceIndentificator { Seria = i.Seria, Number = i.Number }).ToArray();
        if (identifiers.Length == 0)
        {
            throw new ArgumentException("At least one invoice is required.", nameof(ids));
        }

        return new InvoicesRequest { RequestId = Guid.NewGuid().ToString(), SeriaAndNumbers = identifiers };
    }

    private static List<EFacturaInvoiceXml> WithXml(XmlInvoice[]? results) =>
        (results ?? [])
            .Select(i => new EFacturaInvoiceXml(new EFacturaInvoiceId(i.Seria, i.Number), i.Status == 2, (EFacturaInvoiceStatus)i.InvoiceStatus, i.Message, i.Xml))
            .ToList();

    private static List<EFacturaInvoiceResult> Decided(InvoiceResult[]? results) =>
        (results ?? [])
            .Select(r => new EFacturaInvoiceResult(new EFacturaInvoiceId(r.Seria, r.Number), r.Status == 2, r.Message, r.TimeStamp))
            .ToList();

    private static InvoiceComment[] Commented(IEnumerable<(EFacturaInvoiceId Id, string Reason)> invoices)
    {
        ArgumentNullException.ThrowIfNull(invoices);

        var comments = invoices.Select(i => new InvoiceComment { Seria = i.Id.Seria, Number = i.Id.Number, Comment = i.Reason }).ToArray();
        if (comments.Length == 0)
        {
            throw new ArgumentException("At least one invoice is required.", nameof(invoices));
        }

        return comments;
    }

    // The platform pads missing bank details with a space.
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<EFacturaInvoice> Listed(Generated.Invoice[]? results) =>
        (results ?? [])
            .Select(i => new EFacturaInvoice(new EFacturaInvoiceId(i.Seria, i.Number), (EFacturaInvoiceStatus)i.InvoiceStatus, i.Message, i.TimeStamp))
            .ToList();

    // The wire wants a start whenever a range is given; an end alone means nothing to it.
    private static DateSearch? Range(DateTime? from, DateTime? to) =>
        from is { } start ? new DateSearch { StartDate = start, EndDate = to } : null;

    /// <summary>
    /// The binding SFS's guide describes: <c>basicHttpBinding</c> with
    /// <c>TransportWithMessageCredential</c>, so HTTPS carries the call and the user name and
    /// password travel in the SOAP header of every message.
    /// </summary>
    /// <param name="timeout">How long one call may take.</param>
    internal static BasicHttpBinding Binding(TimeSpan timeout)
    {
        var binding = new BasicHttpBinding(BasicHttpSecurityMode.TransportWithMessageCredential)
        {
            MaxReceivedMessageSize = MaxMessageBytes,
            MaxBufferSize = MaxMessageBytes,
            OpenTimeout = timeout,
            SendTimeout = timeout,
            ReceiveTimeout = timeout,
            CloseTimeout = TimeSpan.FromSeconds(30),
        };

        binding.Security.Message.ClientCredentialType = BasicHttpMessageCredentialType.UserName;
        binding.ReaderQuotas.MaxStringContentLength = MaxMessageBytes;
        binding.ReaderQuotas.MaxArrayLength = MaxMessageBytes;

        return binding;
    }

    /// <summary>Closes the connection, or aborts it when it has faulted.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.State == CommunicationState.Faulted)
            {
                _client.Abort();
            }
            else
            {
                await _client.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (CommunicationException)
        {
            _client.Abort();
        }
        catch (TimeoutException)
        {
            _client.Abort();
        }
    }
}
