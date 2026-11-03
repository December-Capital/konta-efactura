using System.Globalization;
using System.Xml.Linq;
using Xunit.Abstractions;

namespace Konta.EFactura.Tests;

/// <summary>
/// Questions put to SFS's test service once, by hand, rather than checks run every time: each posts
/// drafts that stay on the test account (only the web interface deletes them since 2026-10-27).
/// Run one with <c>KONTA_EFACTURA_PROBE</c> set to its name; what SFS answered is written to the
/// test output and to <c>fixtures/live/</c>, and goes into <c>docs/SOAP-NOTES.md</c> by hand.
/// </summary>
public sealed class LiveProbes(ITestOutputHelper output)
{
    /// <summary>
    /// Which rounding SFS holds a line's amount to. Three drafts of one line, 3 × 3.375 = 10.125, its
    /// total written 10.12 (half to even, as <see cref="InvoiceLine"/> rounds), 10.13 (half away from
    /// zero, as the product rounds) and 10.14 (wrong either way, to see whether SFS checks at all).
    /// The test company is not a VAT payer, so only a line amount can be asked, not a VAT amount.
    /// </summary>
    [LiveProbeFact("rounding")]
    public async Task How_sfs_takes_a_line_amount_on_half_a_ban()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var totals = new[] { "10.12", "10.13", "10.14" };
        var invoices = totals.Select(total =>
        {
            var draft = LiveTests.Draft("");
            return new Invoice
            {
                SupplierIdno = draft.SupplierIdno,
                BuyerIdno = draft.BuyerIdno,
                SupplierBankAccount = draft.SupplierBankAccount,
                BuyerBankAccount = draft.BuyerBankAccount,
                DeliveryDate = draft.DeliveryDate,
                CreationMotive = draft.CreationMotive,
                CorrelationId = $"konta-round-{stamp}-{total}",
                Lines = [new InvoiceLine { Code = "1", Name = $"Probă rotunjire {total}", UnitOfMeasure = "buc", Quantity = 3m, UnitPriceWithoutVat = 3.375m, VatPercent = 0m }],
            };
        }).ToList();

        var xml = XDocument.Parse(InvoiceXml.Build(invoices));
        var rows = xml.Descendants("Row").ToList();
        Assert.Equal("10.12", rows[0].Attribute("TotalPriceWithoutTVA")!.Value);
        for (var i = 0; i < totals.Length; i++)
        {
            rows[i].SetAttributeValue("TotalPriceWithoutTVA", totals[i]);
            rows[i].SetAttributeValue("TotalPrice", totals[i]);
        }

        var result = await LiveTests.Live("PostInvoices-rounding", client => client.PostInvoicesXmlAsync(xml.ToString(SaveOptions.DisableFormatting)));

        output.WriteLine($"Status posted {result.Posted} of {result.Total}");
        output.WriteLine($"ErrorMessage: {result.ErrorMessage}");
        foreach (var refusal in result.Refusals)
        {
            output.WriteLine($"Refused #{refusal.Order} ({totals.ElementAtOrDefault(refusal.Order)}): {refusal.Message}");
        }

        Assert.Equal(3, result.Total);
    }

    /// <summary>
    /// A draft as the product now issues one (HANDOFF N5): both parties named as SFS's register names
    /// them, and the books' own amount, 3 × 3.375 posted as 10.13 (half away from zero). Posted today,
    /// for the director to sign today in the web interface; <c>issued</c> reads it back after.
    /// </summary>
    [LiveProbeFact("issue")]
    public async Task A_draft_with_both_names_and_the_books_amount()
    {
        var draft = LiveTests.Draft("");
        var named = await LiveTests.Live("GetTaxpayersInfo-issue", client => client.GetTaxpayersAsync([draft.SupplierIdno, draft.BuyerIdno]));
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var invoice = new Invoice
        {
            SupplierIdno = draft.SupplierIdno,
            BuyerIdno = draft.BuyerIdno,
            SupplierName = named.Single(t => t.FiscalCode == draft.SupplierIdno).Name,
            BuyerName = named.Single(t => t.FiscalCode == draft.BuyerIdno).Name,
            SupplierBankAccount = draft.SupplierBankAccount,
            BuyerBankAccount = draft.BuyerBankAccount,
            DeliveryDate = draft.DeliveryDate,
            CreationMotive = draft.CreationMotive,
            CorrelationId = $"konta-issue-{stamp}",
            Lines = [new InvoiceLine { Code = "1", Name = "Servicii de testare, emitere", UnitOfMeasure = "buc", Quantity = 3m, UnitPriceWithoutVat = 3.375m, VatPercent = 0m, Net = 10.13m, Vat = 0m }],
        };

        var result = await LiveTests.Live("PostInvoices-issue", client => client.PostInvoicesAsync([invoice]));

        output.WriteLine($"{invoice.CorrelationId}: posted {result.Posted} of {result.Total} {result.ErrorMessage}");
        output.WriteLine($"Supplier '{invoice.SupplierName}', buyer '{invoice.BuyerName}'");
        Assert.True(result.AllPosted, result.ErrorMessage);
    }

    /// <summary>The issue probe's invoice once signed: its row as SFS keeps it, and its PDF in <c>fixtures/live/</c>.</summary>
    [LiveProbeFact("issued")]
    public async Task The_issued_draft_once_signed()
    {
        var signed = await LiveTests.Live("SearchInvoices-issued", client => client.SearchInvoicesAsync(
            EFacturaActorRole.Supplier, new EFacturaSearch { Status = EFacturaInvoiceStatus.SignedBySupplier }));
        var found = await LiveTests.Live("GetInvoicesBySeriaNumber-issued", client => client.GetInvoicesAsync(signed.Select(s => s.Id)));
        var ours = found.Where(f => f.CorrelationId?.StartsWith("konta-issue-", StringComparison.Ordinal) == true).ToList();

        foreach (var invoice in ours)
        {
            var document = XDocument.Parse(invoice.Xml!);
            output.WriteLine($"{invoice.CorrelationId} {invoice.Id.Seria} {invoice.Id.Number} {invoice.Status}");
            output.WriteLine($"  Supplier Title '{document.Descendants("Supplier").Single().Attribute("Title")?.Value}', Buyer Title '{document.Descendants("Buyer").Single().Attribute("Title")?.Value}'");
            output.WriteLine($"  Row {string.Join(" ", document.Descendants("Row").Single().Attributes().Where(a => a.Name.LocalName != "Name").Select(a => $"{a.Name}={a.Value}"))}");

            var pdf = await LiveTests.Live("GetInvoicesContentForPrint-issued", client => client.GetPdfAsync([invoice.Id], EFacturaActorRole.Supplier), TimeSpan.FromMinutes(4));
            File.WriteAllBytes(Path.Combine(LiveSettings.Load()!.Root, "fixtures", "live", $"{invoice.CorrelationId}.pdf"), pdf ?? []);
            output.WriteLine($"  PDF {pdf?.Length ?? 0} bytes");
        }

        Assert.NotEmpty(ours);
    }

    /// <summary>
    /// Cancels the one invoice the issue probe made, signed on 2026-11-03 (<c>EBA 000002596</c>,
    /// found by our id, never by guessing), then asks where SFS shows it: what the product's
    /// cancellation does (HANDOFF N42). Nothing else on the test account is touched.
    /// </summary>
    [LiveProbeFact("cancel")]
    public async Task The_issued_invoice_cancelled_and_where_sfs_shows_it_after()
    {
        var signed = await LiveTests.Live("SearchInvoices-cancel", client => client.SearchInvoicesAsync(
            EFacturaActorRole.Supplier, new EFacturaSearch { Status = EFacturaInvoiceStatus.SignedBySupplier }));
        var found = await LiveTests.Live("GetInvoicesBySeriaNumber-cancel", client => client.GetInvoicesAsync(signed.Select(s => s.Id)));
        var ours = Assert.Single(found, f => f.CorrelationId == "konta-issue-20261103145245");
        Assert.Equal(("EBA", "000002596"), (ours.Id.Seria, ours.Id.Number));

        var answer = Assert.Single(await LiveTests.Live("PostCanceledInvoices", client => client.CancelInvoicesAsync([(ours.Id, "Factură de test, emisă din Konta")])));
        output.WriteLine($"Cancel: done {answer.Done}, message '{answer.Message}'");

        var after = Assert.Single(await LiveTests.Live("CheckInvoicesStatus-after-cancel", client => client.CheckInvoicesStatusAsync([ours.Id])));
        output.WriteLine($"After: status {after.Status} ({(int)after.Status}), message '{after.Message}'");
    }

    /// <summary>The rounding probe's drafts as SFS keeps them, minutes after posting: whether it rewrote a total.</summary>
    [LiveProbeFact("rounding-readback")]
    public async Task The_rounding_drafts_as_sfs_keeps_them()
    {
        var drafts = await LiveTests.Live("GetInvoicesForSigning-rounding", client => client.GetInvoicesForSigningAsync(EFacturaActorRole.Supplier));

        foreach (var draft in drafts.Where(d => d.CorrelationId?.StartsWith("konta-round-", StringComparison.Ordinal) == true))
        {
            var row = XDocument.Parse(draft.Xml!).Descendants("Row").Single();
            output.WriteLine($"{draft.CorrelationId}: {string.Join(" ", row.Attributes().Where(a => a.Name.LocalName != "Name").Select(a => $"{a.Name}={a.Value}"))}");
        }
    }
}

/// <summary>A live fact run only when <c>KONTA_EFACTURA_PROBE</c> names it, with the API user's company IDNO.</summary>
public sealed class LiveProbeFactAttribute : FactAttribute
{
    public LiveProbeFactAttribute(string probe)
    {
        if (LiveSettings.Load() is not { Idno: not null })
        {
            Skip = "Posts drafts as the API user's company: set KONTA_EFACTURA_USER, _PASSWORD and _IDNO.";
        }
        else if (!string.Equals(LiveSettings.Value("KONTA_EFACTURA_PROBE"), probe, StringComparison.Ordinal))
        {
            Skip = $"A probe, run by hand: set KONTA_EFACTURA_PROBE={probe}. Its drafts stay on the test account.";
        }
    }
}
