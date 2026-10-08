# Konta.EFactura — protocol notes and usage

The technical notes that used to be the README. See also [SOAP-NOTES.md](SOAP-NOTES.md).

A .NET client for **SIA „e-Factura”**, the Republic of Moldova's electronic invoicing system, run by
the State Tax Service (SFS).

Open source because this is the least differentiating and most breakable part of any Moldovan
accounting product. Every vendor writes it, nobody enjoys it, and when SFS changes something we all
find out at the same time. Better to find out together.

> **Status: alpha.** The XML serialiser validates against SFS's schema, and `EFacturaClient` has
> called every operation of SFS's test service except signing (2026-10-08): it posts drafts and
> finds them again, looks up taxpayers and bank accounts. Signing could not be tried, so nothing
> has been read back, printed or cancelled on a signed invoice yet. Do not put this in front of a
> paying customer yet.

## What the protocol actually looks like

Worth knowing before you estimate anything:

- **SOAP/WCF, not REST.** `basicHttpBinding` with `TransportWithMessageCredential`: HTTPS transport,
  username and password in the message header. The service returns a session token used for
  subsequent calls.
- **Credentials are per legal entity.** A user with the Manager (Director) role creates a dedicated
  API user inside the e-Factura web interface: *Settings → Company users → Register API user*. That
  API user can reach every function the platform offers, so treat the secret accordingly: one leak
  is total control of a company's invoicing.
- **Two modes.** *Semi-automated* transmits by API and leaves signing, cancelling and rejecting to a
  human in the web interface — no certificate custody, which is why it should be your default.
  *Automated* presents the certificate at transmission; for high volume (the guide's threshold is
  over 10,000 invoices a month) SFS points at an automated PKI signing service from STISC.
- **Actor roles:** 1 supplier, 2 buyer, 3 transporter. One integration serves all three sides.
- **The payload is SFS's own XML.** Not UBL, not EN 16931 — despite Moldova's stated direction
  towards Peppol for cross-border trade. Build to your own internal model and serialise through an
  adapter, so a future UBL profile is a second serialiser rather than a rewrite.

### Invoice lifecycle

```
0  Draft                 (deletable only in the SFS portal, never by API)
1  Signed by supplier
2  Refused by buyer      (terminal)
3  Accepted by buyer
5  Cancelled by supplier
6  Archived
7  Sent to buyer
8  Signed by buyer
10 Transported           (goods or services received)
```

4 and 9 are not issued. Do not renumber them, do not fill the gaps, and do not assume a linear
progression.

### The operations

The guides describe 15; the service's WSDL (`spec/sfs/Service.wsdl`) has 19. The four the guides
leave out are `Test`, `GetArchivedInvoices`, `GetBankAccountInfo` and `GetSeriaAndNumbers`. The 15:

`PostInvoices` · `PostInvoicesWithAttachment` · `PostAcceptedInvoices` · `PostRejectedInvoices` ·
`PostCanceledInvoices` · `GetAcceptedInvoices` · `GetRejectedInvoices` · `GetInvoicesBySeriaNumber` ·
`GetInvoicesForSigning` · `GetInvoicesContentForPrint` · `GetInvoicesQRcodes` · `GetTaxpayersInfo` ·
`GetLogs` · `SearchInvoices` · `CheckInvoicesStatus`

## Use it

```csharp
var invoice = new Invoice
{
    SupplierIdno = "1002600001257",
    BuyerIdno    = "1002600003354",
    SupplierBankAccount = "22241410046",          // required by the platform, see trap 11
    BuyerBankAccount    = "2224710SV12365037100",
    DeliveryDate = DateTimeOffset.UtcNow,
    CreationMotive = CreationMotive.Delivery,   // CreationMotiv, required by the schema
    CorrelationId = "our-id-42",
    Lines =
    [
        new InvoiceLine
        {
            Code = "1",
            Name = "Servicii de consultanță",
            UnitOfMeasure = "buc",
            Quantity = 2m,
            UnitPriceWithoutVat = 15m,
            VatPercent = 20m,   // whole-number percentage, not 0.20
        },
    ],
};

string xml = InvoiceXml.Build([invoice]);

await using var client = new EFacturaClient(EFacturaEnvironment.Test, apiUser, apiPassword);
var posted = await client.PostInvoicesAsync([invoice]);
if (!posted.AllPosted)
{
    foreach (var refusal in posted.Refusals)
        Console.WriteLine($"Invoice {refusal.Order}: {refusal.Message}");
}
```

## Traps we have already hit

Collected so you do not have to rediscover them.

1. **`TVA` is a whole-number percentage** (`20`) while every amount attribute is a decimal
   (`36.00`). Passing `0.20` produces a document the platform accepts and an accountant rejects.
2. **The integration guide's worked example has inconsistent signs** on its negative-quantity rows:
   quantity and line total do not reconcile. Do not copy it. `InvoiceLine.Validate()` asserts sign
   agreement for this reason.
3. **Zero-VAT lines still count towards document totals.** Dropping them is a well-known way to get
   totals that disagree with lines.
4. **Serialise every number and date invariantly.** `ro-MD` uses a comma as the decimal separator;
   the wire format does not. There is a test that runs under `ro-MD` specifically to catch this.
5. **Drafts cannot be deleted through the API**, only in the SFS portal. Plan your error handling
   around that.
6. **Cancellation is only allowed from the statuses the platform permits**, which is narrower than
   the enum suggests.
7. **One credential set per legal entity**, not one per installation. An accounting firm holds many.
8. **`CreationMotiv` is required by the schema and missing from the guide's example.** 4 (delivery)
   or 5 (non-delivery) for a VAT payer, 1 to 3 for an issuer that is not. Without it the document
   is invalid. The tests validate every payload against `spec/sfs/TaxInvoiceSchema.xsd`.
9. **SFS's own example XML files are not valid against SFS's own schema.** See `spec/sfs/README.md`.
10. **The test environment answers `403` to any address SFS has not registered**, the API and the
    web interface alike. Production serves its WSDL to anyone.
11. **Both bank accounts are required in practice**, though the schema makes them optional. Leave
    out either and the platform refuses the invoice with its own `NullReferenceException` text,
    `Object reference not set to an instance of an object.` `TVA="-"` does the same.
12. **`PostInvoices` reports a refused invoice as success.** `Status` 2 means the batch was read,
    not posted; compare `TotalInvoicesPosted` with `TotalInvoices` (`EFacturaPostResult.AllPosted`)
    and read the reasons out of `ErrorMessage` (`Refusals`).
13. **A company that is not a VAT payer may use only `CreationMotiv` 1 or 2**, at a `TVA` of `0`,
    whatever the schema's note on 3 says. A VAT payer uses 4 or 5.
14. **`GetSeriaAndNumbers` hands out a number** each time it is called. It is not a lookup.
15. **A posted draft shows up minutes later**, with no series or number. Find it by your own
    `AdditionalInformation/id` (`SearchInvoices` with `APIeInvoiceId`, or the XML from
    `GetInvoicesForSigning`), and never read "not found" just after posting as "not posted".
16. **Identical drafts are merged.** Post the same content twice and one draft remains, which
    makes a blind retry harmless but means two genuinely identical invoices need a difference.
17. **A search filters by one invoice status, always.** The wire carries `InvoiceStatus` even when
    you mean "any", and 0 is Draft.

## Contributing

Bug reports from anyone integrating with e-Factura are welcome, including from competitors.
If SFS changes the schema or the service, a pull request with a failing test is the most useful
thing you can send.

Please do **not** include real IDNO codes, company names or invoice data in issues or tests.

## Related

- [`spec/sfs/`](../spec/sfs/README.md): SFS's schema, examples, classifiers, guides and WSDL, as
  received, with the test and production addresses.
- [konta-rulebook](https://github.com/December-Capital/konta-rulebook) — Moldovan fiscal law as
  citable, effective-dated data.
- [Ghid de integrare semiautomatizat SIA „e-Factura”](https://efactura.sfs.md/Help/Ghid_integrare_Semi_Automatizata.pdf)
  — the official integration guide (SFS, Chișinău, 2025).

## Licence

MIT. See [LICENSE](../LICENSE).
