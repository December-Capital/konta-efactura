using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Konta.EFactura.Tests;

/// <summary>
/// Answers recorded in the shape the test service sent on 2026-10-08, read back through the real
/// binding with <see cref="EFacturaClientOptions.Transport"/> in place of the network. The data in
/// <c>fixtures/replay/</c> is made up; the structure is SFS's, blank first bank account included.
/// </summary>
public sealed class ReplayTests
{
    [Fact]
    public async Task A_taxpayer_is_read_from_a_recorded_answer()
    {
        var replay = new Replay();
        await using var client = Client(replay);

        var found = Assert.Single(await client.GetTaxpayersAsync(["1099900000017"]));

        Assert.Equal(new EFacturaTaxpayer("1099900000017", "\"EXEMPLU\" S.R.L.", "SEC.CENTRU Strada Exemplului nr.1", "0099001", 1, true, true), found);
        Assert.Equal("http://tempuri.org/IService/GetTaxpayersInfo", Assert.Single(replay.Actions));
        Assert.Contains(">1099900000017</", Assert.Single(replay.Bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_blank_bank_account_the_platform_puts_first_is_left_out()
    {
        var replay = new Replay();
        await using var client = Client(replay);

        var account = Assert.Single(await client.GetBankAccountsAsync("1099900000017"));

        Assert.Equal(new EFacturaBankAccount("MD77AG000000022510000101", "TESTMD2X101", "B.C.'BANCA DE TEST'S.A. fil.'Centru' Chisinau", true), account);
    }

    [Fact]
    public async Task Archived_invoices_are_listed_for_a_period_and_a_page()
    {
        var replay = new Replay();
        await using var client = Client(replay);

        var archived = Assert.Single(await client.GetArchivedInvoicesAsync(EFacturaActorRole.Buyer, new DateTime(2026, 1, 1), new DateTime(2026, 10, 8), page: 2));

        Assert.Equal((new EFacturaInvoiceId("EXMP", "000000077"), EFacturaInvoiceStatus.Archived), (archived.Id, archived.Status));
        var body = Assert.Single(replay.Bodies);
        Assert.Matches(@"<(\w+:)?ActorRole>2</(\w+:)?ActorRole>", body);
        Assert.Matches(@"<(\w+:)?Page>2</(\w+:)?Page>", body);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetArchivedInvoicesAsync(EFacturaActorRole.Buyer, new DateTime(2026, 1, 1), page: 0));
    }

    [Fact]
    public async Task Reserving_a_number_says_so_and_asks_for_at_least_one()
    {
        var replay = new Replay();
        await using var client = Client(replay);

        var reserved = Assert.Single(await client.ReserveSeriaAndNumbersAsync(1));

        Assert.Equal(new EFacturaReservedNumber(new EFacturaInvoiceId("EXMP", "000000101"), true), reserved);
        Assert.Equal("http://tempuri.org/IService/GetSeriaAndNumbers", Assert.Single(replay.Actions));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ReserveSeriaAndNumbersAsync(0));
        Assert.Single(replay.Actions);
    }

    [Fact]
    public async Task The_password_travels_in_the_security_header_and_never_reaches_the_recorder()
    {
        var replay = new Replay();
        var recorded = new List<string>();
        await using var client = new EFacturaClient(
            EFacturaEndpoints.Test,
            "api-user",
            "pa55-not-real",
            new EFacturaClientOptions { Transport = _ => replay, Recorder = (_, message) => recorded.Add(message) });

        await client.GetTaxpayersAsync(["1099900000017"]);

        Assert.Contains("pa55-not-real", Assert.Single(replay.Bodies), StringComparison.Ordinal);
        Assert.Equal(2, recorded.Count);
        Assert.All(recorded, message => Assert.DoesNotContain("pa55-not-real", message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_html_error_page_is_a_protocol_error_not_an_answer()
    {
        // What the test service sends for a wrong password (SOAP-NOTES.md).
        var replay = new Replay { Html = true };
        await using var client = Client(replay);

        await Assert.ThrowsAsync<System.ServiceModel.ProtocolException>(() => client.GetTaxpayersAsync(["1099900000017"]));
    }

    [Fact]
    public async Task A_call_given_up_on_does_not_hold_the_client_open()
    {
        var started = TimeProvider.System.GetTimestamp();
        using var leaving = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await using (var client = new EFacturaClient(
            EFacturaEndpoints.Test, "api-user", "not-a-real-password", new EFacturaClientOptions { Transport = _ => new Stalled(), Timeout = TimeSpan.FromSeconds(20) }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTaxpayersAsync(["1099900000017"], leaving.Token));
        }

        // Closing would wait for the stalled call until the binding's 20 seconds.
        Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
    }

    private static EFacturaClient Client(Replay replay) =>
        new(EFacturaEndpoints.Test, "api-user", "not-a-real-password", new EFacturaClientOptions { Transport = _ => replay, Timeout = TimeSpan.FromSeconds(10) });

    /// <summary>A service that never answers within the test.</summary>
    private sealed class Stalled : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    /// <summary>
    /// Answers each call with <c>fixtures/replay/{operation}-response.xml</c>, its security timestamp
    /// renewed, as WCF refuses an answer whose timestamp has expired.
    /// </summary>
    private sealed class Replay : HttpMessageHandler
    {
        public List<string> Actions { get; } = [];

        public List<string> Bodies { get; } = [];

        public bool Html { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = request.Headers.TryGetValues("SOAPAction", out var values) ? values.Single().Trim('"') : string.Empty;
            Actions.Add(action);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            if (Html)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("<html><body>Server Error</body></html>", Encoding.UTF8, "text/html"),
                };
            }

            var operation = action[(action.LastIndexOf('/') + 1)..];
            var recorded = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "replay", $"{operation}-response.xml"), cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Renewed(recorded), Encoding.UTF8, "text/xml"),
            };
        }

        private static string Renewed(string envelope)
        {
            var now = DateTime.UtcNow;
            string Stamp(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

            envelope = Regex.Replace(envelope, "<u:Created>[^<]*</u:Created>", $"<u:Created>{Stamp(now)}</u:Created>", RegexOptions.None, TimeSpan.FromSeconds(1));
            return Regex.Replace(envelope, "<u:Expires>[^<]*</u:Expires>", $"<u:Expires>{Stamp(now.AddMinutes(5))}</u:Expires>", RegexOptions.None, TimeSpan.FromSeconds(1));
        }
    }
}
