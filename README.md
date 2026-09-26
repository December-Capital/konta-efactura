# Konta.EFactura

A .NET client for **SIA „e-Factura”**, the Republic of Moldova's electronic invoicing system, run by
the State Tax Service (SFS).

Open source because this is the least differentiating and most breakable part of any Moldovan
accounting product. Every vendor writes it, nobody enjoys it, and when SFS changes something we all
find out at the same time. Better to find out together.

> **Status: alpha, and not yet verified against the live service.** The XML serialiser and its
> tests are real and passing. The SOAP client is being built against the published integration
> guide and has not yet completed a round trip against an SFS test account. Do not put this in
> front of a paying customer yet.

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

### The 15 operations

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
    DeliveryDate = DateTimeOffset.UtcNow,
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

## Contributing

Bug reports from anyone integrating with e-Factura are welcome, including from competitors.
If SFS changes the schema or the service, a pull request with a failing test is the most useful
thing you can send.

Please do **not** include real IDNO codes, company names or invoice data in issues or tests.

## Related

- [konta-rulebook](https://github.com/December-Capital/konta-rulebook) — Moldovan fiscal law as
  citable, effective-dated data.
- [Ghid de integrare semiautomatizat SIA „e-Factura”](https://efactura.sfs.md/Help/Ghid_integrare_Semi_Automatizata.pdf)
  — the official integration guide (SFS, Chișinău, 2025).

## Licence

MIT. See [LICENSE](LICENSE).
