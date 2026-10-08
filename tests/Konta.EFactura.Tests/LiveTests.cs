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

    [LiveFact]
    public async Task The_lists_answer_for_the_supplier()
    {
        await Live("GetAcceptedInvoices", client => client.GetAcceptedInvoicesAsync(EFacturaActorRole.Supplier));
        await Live("GetRejectedInvoices", client => client.GetRejectedInvoicesAsync(EFacturaActorRole.Supplier));
        await Live("SearchInvoices", client => client.SearchInvoicesAsync(
            EFacturaActorRole.Supplier, new EFacturaSearch { IssuedFrom = DateTime.Today.AddDays(-30), IssuedTo = DateTime.Today.AddDays(1) }));
    }

    /// <summary>One call to the test service, its exchange written to <c>fixtures/live/</c>.</summary>
    private static async Task<T> Live<T>(string operation, Func<EFacturaClient, Task<T>> call)
    {
        var live = LiveSettings.Load()!;
        var recorded = new List<(string Direction, string Message)>();
        T answer;

        await using (var client = new EFacturaClient(
            EFacturaEndpoints.Test, live.User, live.Password, TimeSpan.FromMinutes(1), live.Proxy, (d, m) => recorded.Add((d, m))))
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

internal sealed record LiveSettings(string User, string Password, Uri? Proxy, string Root)
{
    public static LiveSettings? Load()
    {
        var root = RepositoryRoot();
        var file = root is null ? new Dictionary<string, string>() : ReadDotEnv(Path.Combine(root, ".env"));

        string? Value(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : file.GetValueOrDefault(name);

        var user = Value("KONTA_EFACTURA_USER");
        var password = Value("KONTA_EFACTURA_PASSWORD");
        var proxy = Value("KONTA_EFACTURA_PROXY");

        return string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password) || root is null
            ? null
            : new LiveSettings(user, password, string.IsNullOrWhiteSpace(proxy) ? null : new Uri(proxy), root);
    }

    /// <summary>Writes one exchange under <c>fixtures/live/</c>, refusing to keep the password.</summary>
    public void Write(string operation, IEnumerable<(string Direction, string Message)> messages)
    {
        var folder = Path.Combine(Root, "fixtures", "live");
        Directory.CreateDirectory(folder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

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
