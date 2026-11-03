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
