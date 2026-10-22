using System.Globalization;
using System.Xml.Linq;

namespace Konta.EFactura.Tests;

/// <summary>
/// Calls SFS's test service for real. Skipped unless an API user is given, in the environment or
/// in the git-ignored <c>.env</c> at the repository root:
/// <c>KONTA_EFACTURA_USER</c>, <c>KONTA_EFACTURA_PASSWORD</c>, and <c>KONTA_EFACTURA_PROXY</c>
/// (for example <c>socks5://127.0.0.1:1080</c>) when this computer's address is not the one SFS
/// registered. Each exchange is written to <c>fixtures/live/</c>, also git-ignored, without the
/// password; read a fixture before copying it anywhere tracked.
/// </summary>
public sealed class LiveTests
{
    [LiveFact]
    public async Task The_test_service_answers_the_api_user()
    {
        var answer = await Live("Test", client => client.TestAsync("Konta"));

        Assert.Equal(2, answer.Status);
    }

    [LiveFact]
    public async Task Any_idno_can_be_looked_up()
    {
        // Two companies from SFS's own guide.
        var found = await Live("GetTaxpayersInfo", client => client.GetTaxpayersAsync(["1002600004030", "1002600023736"]));

        Assert.Equal(["1002600004030", "1002600023736"], found.Select(t => t.FiscalCode));
        Assert.All(found, t => Assert.True(t.InTaxRegistry));
    }

    [LiveFact]
    public async Task An_invoice_from_another_supplier_is_refused_though_the_call_succeeds()
    {
        // The guide's example supplier, which is not the API user's company.
        var invoice = new Invoice
        {
            SupplierIdno = "1002600001257",
            BuyerIdno = "1002600003354",
            SupplierBankAccount = "22241410046",
            BuyerBankAccount = "2224710SV12365037100",
            DeliveryDate = DateTimeOffset.Now,
            CreationMotive = CreationMotive.Delivery,
            CorrelationId = "konta-live-foreign-supplier",
            Lines = [new InvoiceLine { Code = "1", Name = "Test", UnitOfMeasure = "buc", Quantity = 1m, UnitPriceWithoutVat = 10m, VatPercent = 20m }],
        };

        var result = await Live("PostInvoices", client => client.PostInvoicesAsync([invoice]));

        Assert.False(result.AllPosted);
        Assert.Equal((1, 0), (result.Total, result.Posted));
        Assert.Equal(0, Assert.Single(result.Refusals).Order);
    }

    /// <summary>
    /// Posts a draft as the API user's company, so it runs only when <c>KONTA_EFACTURA_IDNO</c> names
    /// it; reads it back by the id the platform gave and deletes it by that id, so the run leaves
    /// nothing. Only its own draft: the others on the test account are kept for a person, one of them
    /// (<c>konta-registered-15</c>) with the registered accounts for the signing attempt.
    /// </summary>
    [LivePostFact]
    public async Task A_draft_is_posted_with_the_platforms_id_read_back_and_deleted_by_it()
    {
        var invoice = Draft("konta-live-");

        var result = await Live("PostInvoices", client => client.PostInvoicesAsync([invoice]));
        var id = Assert.Single(result.DraftIds);
        var fetched = Assert.Single(await Live("GetInvoicesBySeriaNumber-posted", client => client.GetDraftsAsync([id])));
        var deletion = await Live("DeleteDraftInvoice", client => client.DeleteDraftAsync(id));
        var after = await Live("SearchInvoices-after-delete", client => client.SearchInvoicesAsync(EFacturaActorRole.Supplier, new EFacturaSearch()));
        var gone = Assert.Single(await Live("GetInvoicesBySeriaNumber-deleted", client => client.GetDraftsAsync([id])));

        Assert.True(result.AllPosted, result.ErrorMessage);
        Assert.Empty(result.Refusals);

        // A search may list a draft only minutes after posting; asked by its id, the platform answers at once.
        Assert.True(fetched.Found, fetched.Message);
        Assert.Equal((EFacturaInvoiceStatus.Draft, id, invoice.CorrelationId), (fetched.Status, fetched.PlatformId, fetched.CorrelationId));

        Assert.True(deletion.Accepted, deletion.Message);
        Assert.DoesNotContain(after, d => d.PlatformId == id);

        // Asked for by its id, a deleted draft is still answered, with a status of its own and no id.
        Assert.Equal((EFacturaInvoiceStatus.DeletedDraft, null as Guid?, invoice.CorrelationId), (gone.Status, gone.PlatformId, gone.CorrelationId));
    }

    [LiveFact]
    public async Task Deleting_a_draft_nobody_holds_is_answered_as_done_all_the_same()
    {
        var deletion = await Live("DeleteDraftInvoice-unknown", client => client.DeleteDraftAsync(Guid.NewGuid()));

        // The same answer as for a draft it deleted: the answer is no proof (SOAP-NOTES.md).
        Assert.True(deletion.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(deletion.Message));
    }

    [LivePostFact]
    public async Task Drafts_are_listed_by_a_search_with_the_platforms_id_and_for_signing_without_it()
    {
        // Drafts from earlier runs, posted here and by the konta repository's gateway tests.
        var searched = await Live("SearchInvoices-drafts", client => client.SearchInvoicesAsync(EFacturaActorRole.Supplier, new EFacturaSearch()));
        var toSign = await Live("GetInvoicesForSigning-drafts", client => client.GetInvoicesForSigningAsync(EFacturaActorRole.Supplier));

        Assert.NotEmpty(searched);
        Assert.All(searched, d => Assert.NotNull(d.PlatformId));
        Assert.NotEmpty(toSign);
        Assert.All(toSign, d => Assert.Null(d.PlatformId));
    }

    [LiveFact]
    public async Task The_signed_invoice_of_2017_is_read_in_the_older_format()
    {
        // The one invoice the test account holds that is not a draft: received, signed by the buyer.
        var signed = await Live("SearchInvoices-signed-by-buyer", client => client.SearchInvoicesAsync(
            EFacturaActorRole.Buyer, new EFacturaSearch { Status = EFacturaInvoiceStatus.SignedByBuyer }));
        var invoice = Assert.Single(signed);

        var found = Assert.Single(await Live("GetInvoicesBySeriaNumber-older", client => client.GetInvoicesAsync([invoice.Id])));
        var status = Assert.Single(await Live("CheckInvoicesStatus", client => client.CheckInvoicesStatusAsync([invoice.Id])));
        var document = found.ReadDocument()!;

        Assert.True(found.Found);
        Assert.True(document.OlderFormat);
        Assert.Equal((invoice.Id.Seria, invoice.Id.Number), (document.Seria, document.Number));
        Assert.Null(document.CreationMotive);
        Assert.All(document.Lines, l => Assert.Null(l.VatCode));
        Assert.Equal(EFacturaInvoiceStatus.SignedByBuyer, status.Status);
    }

    [LiveFact]
    public async Task The_test_service_publishes_the_committed_wsdl()
    {
        var live = LiveSettings.Load()!;

        // A difference means SFS changed the service: commit its WSDL and regenerate (SOAP-NOTES.md).
        Assert.Equal(Wsdl.Shape(Wsdl.Committed("Service.wsdl")), Wsdl.Shape(await Published(EFacturaEndpoints.Test, live.Proxy)));
    }

    /// <summary>
    /// Production's WSDL is public, but production answers the test service's registered address (the
    /// VPS) with 403, so this runs where <c>KONTA_EFACTURA_PRODUCTION_WSDL</c> is set: anywhere else.
    /// </summary>
    [ProductionWsdlFact]
    public async Task Production_publishes_the_committed_wsdl()
    {
        Assert.Equal(Wsdl.Shape(Wsdl.Committed("Service-production.wsdl")), Wsdl.Shape(await Published(EFacturaEndpoints.Production, proxy: null)));
    }

    [LiveFact]
    public async Task The_lists_answer_for_the_supplier()
    {
        await Live("GetAcceptedInvoices", client => client.GetAcceptedInvoicesAsync(EFacturaActorRole.Supplier));
        await Live("GetRejectedInvoices", client => client.GetRejectedInvoicesAsync(EFacturaActorRole.Supplier));
        await Live("SearchInvoices", client => client.SearchInvoicesAsync(
            EFacturaActorRole.Supplier, new EFacturaSearch { IssuedFrom = DateTime.Today.AddDays(-30), IssuedTo = DateTime.Today.AddDays(1) }));
    }

    [LivePostFact]
    public async Task Posted_drafts_wait_for_signing_with_our_id()
    {
        // Drafts from earlier runs: a draft appears minutes after posting, not at once.
        var drafts = await Live("GetInvoicesForSigning", client => client.GetInvoicesForSigningAsync(EFacturaActorRole.Supplier));

        Assert.NotEmpty(drafts);
        Assert.All(drafts, d =>
        {
            Assert.True(d.Found);
            Assert.Equal(EFacturaInvoiceStatus.Draft, d.Status);
            Assert.Equal("", d.Id.Number); // a draft has no number until it is signed
            Assert.StartsWith("konta-", d.CorrelationId, StringComparison.Ordinal);
        });
    }

    [LivePostFact]
    public async Task A_search_finds_drafts_unless_told_another_status()
    {
        var month = new EFacturaSearch { IssuedFrom = DateTime.Today.AddDays(-30), IssuedTo = DateTime.Today.AddDays(1) };

        var drafts = await Live("SearchInvoices", client => client.SearchInvoicesAsync(EFacturaActorRole.Supplier, month));
        var signed = await Live("SearchInvoices", client => client.SearchInvoicesAsync(
            EFacturaActorRole.Supplier, month with { Status = EFacturaInvoiceStatus.SignedBySupplier }));

        Assert.NotEmpty(drafts);
        Assert.All(drafts, d => Assert.Equal(EFacturaInvoiceStatus.Draft, d.Status));
        Assert.DoesNotContain(signed, d => d.Status == EFacturaInvoiceStatus.Draft);
    }

    [LivePostFact]
    public async Task The_company_has_a_registered_bank_account()
    {
        var live = LiveSettings.Load()!;

        var accounts = await Live("GetBankAccountInfo", client => client.GetBankAccountsAsync(live.Idno!));

        Assert.NotEmpty(accounts);
        Assert.All(accounts, a => Assert.False(string.IsNullOrWhiteSpace(a.Account)));
        Assert.Contains(accounts, a => a.IsRegistered);
    }

    [LiveFact]
    public async Task An_idno_nobody_holds_is_answered_with_nobody()
    {
        // A valid check digit does not make a number free: one picked that way was a real company's.
        // The 1099900000xxx numbers were unassigned on the test service on 2026-10-15.
        const string nobody = "1099900000039";

        var taxpayers = await Live("GetTaxpayersInfo-unknown", client => client.GetTaxpayersAsync([nobody]));
        var accounts = await Live("GetBankAccountInfo-unknown", client => client.GetBankAccountsAsync(nobody));

        // Left out of the answer rather than answered as unknown.
        Assert.Empty(taxpayers);
        Assert.Empty(accounts);
    }

    [LiveFact]
    public async Task An_invoice_that_does_not_exist_is_answered_per_invoice()
    {
        // A series and number the test service handed this API user on 2026-10-08; no invoice has it.
        EFacturaInvoiceId[] none = [new("EWWW", "000067623")];

        var xml = Assert.Single(await Live("GetInvoicesBySeriaNumber", client => client.GetInvoicesAsync(none)));
        var qr = Assert.Single(await Live("GetInvoicesQRcodes", client => client.GetQrCodesAsync(none)));
        var pdf = await Live("GetInvoicesContentForPrint", client => client.GetPdfAsync(none, EFacturaActorRole.Supplier));
        var cancelled = Assert.Single(await Live("PostCanceledInvoices", client => client.CancelInvoicesAsync([(none[0], "Test Konta")])));

        Assert.Equal((false, "Invoice not found!"), (xml.Found, xml.Message));
        Assert.Equal((false, "Invoice not found!"), (qr.Found, qr.Message));
        Assert.Null(pdf);
        Assert.False(cancelled.Done);
        Assert.False(string.IsNullOrEmpty(cancelled.Message));
    }

    [LiveFact]
    public async Task Logs_answer_for_a_range()
    {
        await Live("GetLogs", client => client.GetLogsAsync(DateTime.Today.AddDays(-7), DateTime.Today.AddDays(1)));
    }

    /// <summary>A draft to the guide's company that no other draft says the same as, so the platform merges it with none.</summary>
    private static Invoice Draft(string prefix)
    {
        var live = LiveSettings.Load()!;
        var now = DateTime.UtcNow;

        return new Invoice
        {
            SupplierIdno = live.Idno!,
            BuyerIdno = "1002600004030", // a company from SFS's guide, an e-Factura actor on the test service
            SupplierBankAccount = "MD24AG000225100013104168",
            BuyerBankAccount = "MD21EX000000022241410046",
            DeliveryDate = DateTimeOffset.Now,

            // The test company is not a VAT payer: the platform takes only motive 1 or 2 from it, at a 0 rate.
            CreationMotive = CreationMotive.SupplyDocumentation,
            CorrelationId = prefix + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            Lines = [new InvoiceLine { Code = "1", Name = "Servicii de testare", UnitOfMeasure = "buc", Quantity = 1m, UnitPriceWithoutVat = 10m + ((now.Second * 100) + (now.Millisecond / 10)) / 100m, VatPercent = 0m }],
        };
    }

    /// <summary>The WSDL a service publishes.</summary>
    private static async Task<XDocument> Published(Uri address, Uri? proxy)
    {
        using var http = new HttpClient(proxy is null ? new HttpClientHandler() : new HttpClientHandler { Proxy = new System.Net.WebProxy(proxy), UseProxy = true })
        {
            Timeout = TimeSpan.FromMinutes(1),
        };

        return XDocument.Parse(await http.GetStringAsync(new Uri(address + "?singleWsdl")));
    }

    /// <summary>One call to the test service, its exchange written to <c>fixtures/live/</c>.</summary>
    private static async Task<T> Live<T>(string operation, Func<EFacturaClient, Task<T>> call)
    {
        var live = LiveSettings.Load()!;
        var recorded = new List<(string Direction, string Message)>();
        T answer;

        await using (var client = new EFacturaClient(
            EFacturaEndpoints.Test,
            live.User,
            live.Password,
            new EFacturaClientOptions { Timeout = TimeSpan.FromMinutes(1), Proxy = live.Proxy, Recorder = (d, m) => recorded.Add((d, m)) }))
        {
            answer = await call(client);
        }

        live.Write(operation, recorded);
        Assert.Equal(["request", "response"], recorded.Select(r => r.Direction));

        return answer;
    }
}

/// <summary>A fact that runs only when <see cref="LiveSettings"/> finds an API user.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (LiveSettings.Load() is null)
        {
            Skip = "No e-Factura API user: set KONTA_EFACTURA_USER and KONTA_EFACTURA_PASSWORD, or put them in .env.";
        }
    }
}

/// <summary>
/// A fact that fetches production's WSDL, run only where <c>KONTA_EFACTURA_PRODUCTION_WSDL</c> is set
/// (in the environment or <c>.env</c>): production refuses the VPS, the one address the live tests run from.
/// </summary>
public sealed class ProductionWsdlFactAttribute : FactAttribute
{
    public ProductionWsdlFactAttribute()
    {
        if (string.IsNullOrEmpty(LiveSettings.Value("KONTA_EFACTURA_PRODUCTION_WSDL")))
        {
            Skip = "Fetches production's WSDL: set KONTA_EFACTURA_PRODUCTION_WSDL=1 where production answers (not the VPS).";
        }
    }
}

/// <summary>A live fact that also needs the API user's company IDNO, because it posts as that company.</summary>
public sealed class LivePostFactAttribute : FactAttribute
{
    public LivePostFactAttribute()
    {
        if (LiveSettings.Load() is not { Idno: not null })
        {
            Skip = "Posts a draft as the API user's company: set KONTA_EFACTURA_IDNO as well, or put it in .env.";
        }
    }
}

internal sealed record LiveSettings(string User, string Password, string? Idno, Uri? Proxy, string Root)
{
    /// <summary>A setting from the environment, else from the repository's <c>.env</c>.</summary>
    public static string? Value(string name)
    {
        var root = RepositoryRoot();

        return Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : root is null ? null : ReadDotEnv(Path.Combine(root, ".env")).GetValueOrDefault(name);
    }

    public static LiveSettings? Load()
    {
        var root = RepositoryRoot();
        var user = Value("KONTA_EFACTURA_USER");
        var password = Value("KONTA_EFACTURA_PASSWORD");
        var idno = Value("KONTA_EFACTURA_IDNO");
        var proxy = Value("KONTA_EFACTURA_PROXY");

        return string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password) || root is null
            ? null
            : new LiveSettings(user, password, string.IsNullOrWhiteSpace(idno) ? null : idno, string.IsNullOrWhiteSpace(proxy) ? null : new Uri(proxy), root);
    }

    /// <summary>Writes one exchange under <c>fixtures/live/</c>, refusing to keep the password.</summary>
    public void Write(string operation, IEnumerable<(string Direction, string Message)> messages)
    {
        var folder = Path.Combine(Root, "fixtures", "live");
        Directory.CreateDirectory(folder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        foreach (var (direction, message) in messages)
        {
            Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(folder, $"{stamp}-{operation}-{direction}.xml"), message);
        }
    }

    private static string? RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Konta.EFactura.sln")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ReadDotEnv(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (trimmed.Length == 0 || trimmed[0] == '#' || equals < 1)
            {
                continue;
            }

            values[trimmed[..equals].Trim()] = trimmed[(equals + 1)..].Trim().Trim('"');
        }

        return values;
    }
}
