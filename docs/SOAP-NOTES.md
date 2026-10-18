# Notes on the SOAP client

Working notes for whoever implements the transport. What is ticked was seen on the test service;
the rest comes from the published integration guide. Tick each item as you confirm it.

## Configuration

The guide shows a `web.config` client section using `basicHttpBinding` with
`security mode="TransportWithMessageCredential"`. In modern .NET there is no `web.config`, so
configure the binding in code:

```csharp
var binding = new BasicHttpsBinding(BasicHttpsSecurityMode.TransportWithMessageCredential)
{
    MaxReceivedMessageSize = 32 * 1024 * 1024, // invoice batches with attachments are large
};
binding.Security.Message.ClientCredentialType = BasicHttpMessageCredentialType.UserName;

var client = new ServiceClient(binding, new EndpointAddress(endpointUri));
client.ClientCredentials.UserName.UserName = username;
client.ClientCredentials.UserName.Password = password;
```

This is what `EFacturaClient.Binding` does. Construct an `EFacturaClient` with
`EFacturaEnvironment.Test` or `.Production` and the API user's name and password.

The contract is generated from the committed WSDL and committed itself, so a change in the
service shows up as a reviewable diff. It is generated `--internal`: the package's public surface is
`EFacturaClient`, never SFS's wire types. To regenerate after replacing `spec/sfs/Service.wsdl`, from
the repository root:

```bash
dotnet tool install --global dotnet-svcutil   # 8.0.0 was used
rm src/Konta.EFactura/Generated/EFacturaService.cs
dotnet-svcutil spec/sfs/Service.wsdl --outputDir Generated --outputFile EFacturaService.cs \
  --namespace "*,Konta.EFactura.Generated" --internal \
  --projectFile src/Konta.EFactura/Konta.EFactura.csproj --targetFramework net9.0 --sync
git checkout src/Konta.EFactura/Konta.EFactura.csproj   # svcutil rewrites it with floating versions
```

`--outputDir` is relative to the project, not to the current directory. svcutil adds
`System.ServiceModel.*` at `8.*` and `NetTcp`, which we do not use; the project pins
`System.ServiceModel.Http` and `.Primitives` at 8.1.2, and `System.Security.Cryptography.Xml` at
9.0.20 because the 8.0.2 they bring has high-severity advisories. A test fails if the generated
contract and the WSDL disagree on the operations.

## To confirm against a test account

- [x] The endpoint URL for test, and whether production differs by host or by path only. By host:
      `apiefactura-pre.sfs.md` and `efactura-api.sfs.md`, both at `/Service.svc` (SFS's package,
      2026-10-06). The test host answers 403 until SFS registers the caller's address.
- [x] The first `Test` call from a registered address with real test credentials (2026-10-08,
      from the registered address, `LiveTests`). It answers `Status` 2 and puts
      `User name: <api user> from v2` in `RequestId`, so it does authenticate. What the other
      `Status` values mean is still to read. The service's clock is local time (+03:00).
- [x] A wrong password is not a SOAP fault: the service answers an HTML error page with HTTP 500,
      which WCF raises as a `ProtocolException` about content type `text/html`. Treat that as
      "authentication refused or server error", never retry it blindly.
- [x] An unregistered address gets `403` before any SOAP, on the test service, portal and website
      alike. A developer elsewhere has to go out through the registered address;
      `KONTA_EFACTURA_PROXY` takes a proxy URL for that.
- [x] The response `Status`: 1 accepted for execution, 2 done, 3 execution error (the guide, every
      operation). `EFacturaClient` raises 3 as `EFacturaException` and returns the rest.
- [x] **`PostInvoices` answers 2 for a batch it read and refused.** An invoice whose supplier is not
      the API user's company comes back with `Status` 2, `TotalInvoices` 1, `TotalInvoicesPosted` 0
      and `ErrorMessage` `Invoice order = 0, error: Autorul facturii nu este desemnat drept
      furnizor.` (2026-10-08). Only the two totals say whether anything went in;
      `EFacturaPostResult.AllPosted` compares them and `Refusals` splits the message per invoice.
- [x] A batch that is not valid against the schema is `Status` 3, with the validator's messages in
      `ErrorMessage`, each repeated twice (`<Documents />`, 2026-10-08).
- [x] `GetTaxpayersInfo` answers for any IDNO, not only counterparties: two companies from the
      guide came back with name, address, VAT code and `IsEFacturaActor`. It answers `Status` 1, a
      nil `RequestId` and a `TimeStamp` of `0001-01-01`, so do not read 1 as "not done yet" there.
- [x] `GetAcceptedInvoices`, `GetRejectedInvoices`, `GetInvoicesForSigning`, `SearchInvoices` and
      `GetLogs` answer 2 with empty `Results` on an account with no invoices. `GetLogs` was empty
      for two days that included our own calls, so it may log only some methods.
- [x] **`GetSeriaAndNumbers` hands out numbers**: with `Count` 1, `InvoiceType` 1 and an empty
      `Seria` it answered `EWWW 000067623`. Treat it as a reservation, not a lookup, and do not call
      it to explore.
- [x] A valid invoice posts (2026-10-08, as the test company, `LiveTests`): `TotalInvoicesPosted`
      1 and an empty `ErrorMessage`. The test company is not a VAT payer (no `CodTVA` in
      `GetTaxpayersInfo`); from it the platform takes only `CreationMotiv` 1 or 2 ("Motivul
      Crearii este indicat incorect trebue sa fie 1 sau 2"), though the schema also lists 3, and a
      line `TVA` of `0`.
- [x] **Both bank accounts are required**, though the schema makes `BankAccount` optional. Without
      the supplier's, the buyer's or both, the invoice is refused with `Object reference not set
      to an instance of an object.`, the platform's own crash. `TVA="-"` (the classifier's
      "no rate") fails the same way. `Invoice.Validate` now requires both accounts.
- [x] **A posted draft reaches the API minutes later, not at once.** Right after posting, and a
      minute later, every search, `GetInvoicesForSigning` and `GetArchivedInvoices` answered empty;
      about 20 minutes later `SearchInvoices` (as supplier, by issue date, by buyer, by
      `APIeInvoiceId`) and `GetInvoicesForSigning` (`ActorRole` 1, `Order` 1) found them, with
      `InvoiceStatus` 0, an empty `Seria` and `Number`, and a `TimeStamp` of `0001-01-01`. So a
      draft has no series or number to look it up by; our `AdditionalInformation/id` is the handle,
      and `SearchParameters.APIeInvoiceId` does find it. `GetInvoicesForSigning` returns the XML we
      sent, unchanged. Never read "not found" right after posting as "not posted". On 2026-10-18
      a draft was listed eleven minutes after posting, so the delay varies.
- [x] **Identical drafts are merged.** Three posts of the same content (same parties, date and
      line, different `AdditionalInformation/id`) left one draft in the web interface; drafts that
      differ in amount and line name are all kept (2026-10-08).
- [ ] Signing a posted draft in the web interface. The test company's director, who has the right
      to sign, gets "Nu aveți dreptul să accesați acest funcțional! Contactați directorul!" on every
      draft: the plain ones, one with `Title`, `Address`, `TaxpayerType` and bank names for both
      sides, so not missing fields. A draft with the bank accounts the tax service has registered
      (from `GetBankAccountInfo`) is posted to try next. Otherwise a question for SFS.
- [x] **`SearchParameters.InvoiceStatus` is a filter, always applied.** 0 finds drafts and only
      drafts; 1 found none of them. A search covers one status at a time. `EFacturaSearch.Status`
      defaults to `Draft`.
- [x] **`CheckInvoicesStatus` answered HTTP 502** on the test service, every call, whatever the
      invoice (2026-10-08). **It answers since**: on 2026-10-18 it gave `Status` 2 and, per
      invoice, the `InvoiceStatus` a search gives (8 for an invoice signed by the buyer). Keep the
      search as a fallback; the 502 may come back.
- [x] **`PostInvoicesWithAttachment` fails inside the platform** on every try, with a valid
      one-page PDF and a valid invoice: `Status` 0, zero totals, and the server's exception and
      stack trace in `RequestId`. No typed method until it works.
- [x] **`GetLogs` needs both `From` and `To`.** Without them the test service answers an HTML 500
      page. With them it answers empty, so far.
- [x] **`GetBankAccountInfo` lists the accounts registered for an IDNO**; naming an account in the
      request changes nothing. Our drafts used a made-up account; the test company's registered
      one is at another bank. The test company's answer always starts with a blank entry; another
      company's (2026-10-15) had none, so the blank belongs to that company's record, not to the
      operation. `GetBankAccountsAsync` leaves blanks out either way.
- [x] **An IDNO nobody holds is left out, not reported** (2026-10-15): `GetTaxpayersInfo` answers
      `Status` 1 with empty `Results`, `GetBankAccountInfo` `Status` 2 with empty `Results`. A valid
      check digit does not make a number free: one picked that way was a real company's. The
      `1099900000xxx` numbers were unassigned then, and the tests use them.
- [x] **`GetSeriaAndNumbers` reserves**: two calls handed out `EWWW 000067623`, then `000067624`.
      Its typed method is named for what it does, `ReserveSeriaAndNumbersAsync`, and refuses a
      count below one before calling.
- [x] `GetArchivedInvoices` takes a role, an issue-date range and a page (from 1) and answers the
      same `Invoice` list as a search: `GetArchivedInvoicesAsync`. Not in either guide; empty on the
      test account so far.
- [x] **The invoice XML reads back.** `InvoiceDocumentReader` turns the `<Document>` that
      `GetInvoicesBySeriaNumber` and `GetInvoicesForSigning` return into an `EFacturaDocument`:
      parties with name, address, VAT code and bank account, the rows as the issuer wrote them, and
      on a signed invoice the `Seria`, `Number` and `IssuedDate` the schema places at the top of
      `SupplierInfo`. A row's `TVA` stays a code (`20`, `-`), never a number, since `-` is one of
      SFS's codes. Read against our own drafts as the test service returned them and against a
      signed invoice made up in the schema's shape: **no signed invoice has been read from the
      service yet**, since none could be signed (see signing below).
- [x] **Per-invoice answers, not faults**, for an invoice the user may not touch or that does not
      exist: `Status` 2 for the call, and per invoice `Status` 3 with `Invoice not found!`,
      `User is not authorized to access this invoice` (`GetInvoicesBySeriaNumber`) or
      `This action is not allowed for this invoice!` (cancel, refuse, accept).
      `GetInvoicesContentForPrint` answers a `Result` with `Status` 0 and no content.
- [ ] Whether the session token must be presented explicitly on later calls, or whether WCF's
      per-call credentials are sufficient. The guide says a token is returned; its examples set
      `ClientCredentials` on every call and do nothing visible with the token.
- [ ] Exact fault types. The guide names `AuthenticationFailedException`; none has been seen yet:
      wrong passwords and server errors come as HTML pages, refusals as per-invoice statuses.
- [ ] Throttling and batch limits for `PostInvoices`.
- [ ] Back-dating window. A limit is reported; confirm the exact number of days.
- [x] Whether `GetTaxpayersInfo` is usable for general IDNO validation, or only for counterparties
      you already trade with: any IDNO, including companies the API user's company never traded
      with (2026-10-08 and 2026-10-15). Konta fills a partner's official name, address, VAT code
      and registered bank accounts from it and `GetBankAccountInfo`.

## Seen on 2026-10-18

Every listing on both sides, one invoice by number, decisions on a number no invoice holds, the
log, and one draft posted and read back, through Konta's gateway. New since the notes above:

- [x] **The service's WSDL has changed since SFS's package of 2026-10-06.** What
      `https://apiefactura-pre.sfs.md/Service.svc?singleWsdl` serves differs from
      `spec/sfs/Service.wsdl`: a 20th operation, `DeleteDraftInvoice`, taking a `guid` `Id` and
      answering a `Message`; `InvoiceIds` (an array of strings) in the `PostInvoices` answer; an
      `Id` string in `InvoiceIndentificator`, the series-and-number type of every by-number call;
      and no `Attachment` in `PostInvoices`'s request. The contract test compares the generated
      contract with the committed WSDL, so it cannot see this: fetch the WSDL again before
      regenerating. Not regenerated yet, so `EFacturaClient` reads none of the new fields.
- [x] **A draft now has SFS's own id.** `PostInvoices` answers `InvoiceIds` with one GUID per
      draft registered (seen since 2026-10-15; the answers of 2026-10-08 had none), and
      `SearchInvoices` lists the draft with the same GUID in `Id`. `GetInvoicesForSigning` still
      gives a nil `Id`. With `DeleteDraftInvoice`, that may let a draft be deleted without the web
      interface; not tried.
- [x] **`SearchInvoices` needs no criterion but the status**: with only `InvoiceStatus` it lists
      every invoice of that status on that side. Each listed invoice has a `TimeStamp` of
      `0001-01-01`.
- [x] **The test account holds a signed invoice from 2017**, received by the test company,
      signed by the buyer (status 8), apparently carried over from production. Its XML is in a
      format older than `TaxInvoiceSchema.xsd`: rows with `UnitPrice`, `TotalCost`, `TotalTVA` and
      `TotalPrice`, no `TVA` code and no `UnitPriceWithoutTVA`; no `CreationMotiv`; `Total`,
      `TotalTVA` and `IsFarma` under `SupplierInfo`; `RiskAnalysisValid`, `IsRegistered` and
      `IsValid` on each party; the buyer's `CodTVA` ten spaces and its `Account` empty with
      `IsManual` true; two XAdES signatures under `Signatures/SignatureContent/SignedDoc`.
      `InvoiceDocumentReader` refuses it ("no Row/@UnitPriceWithoutTVA"), so a reader for
      received invoices has to know the older rows too, and read blanks as absent.
- [x] **`GetInvoicesContentForPrint` fails inside the platform for a signed invoice**: HTTP 500
      with SFS's HTML error page ("A apărut o eroare"), whether asked as buyer or as supplier,
      portrait or landscape. For a number no invoice holds it answers `Status` 0 and no content,
      as before. No PDF has come back from the service yet.
- [x] **`GetInvoicesQRcodes` answers an image and a text** for an invoice the company is party to:
      a 106 by 106 PNG and lines reading series and number, `Valabila (Nevalabila)`, `Furn-` and
      the supplier's IDNO, `Cump-` and the buyer's, `Suma totala-` and `Suma TVA-` in lei, then a
      tab and a link to `https://efactura.fisc.md:443/EFactura.aspx?id=` with a GUID.
- [x] **Accept, refuse and cancel on a number no invoice holds** answer `Status` 2 for the call
      and `Invoice not found!` per invoice, as the other by-number calls do.
- [x] **`GetLogs` stays empty** for a day holding dozens of our own calls, from
      `GetTaxpayersInfo` to `PostInvoices`. It seems not to log API calls, at least on the test
      service.
- [x] **`PostInvoices` does not check an IBAN's check digits**: drafts whose buyer account fails
      the mod-97 check are registered and listed like any other. Check them before posting.

## Resilience

Wrap every call: retry on transport faults and 5xx with exponential backoff and jitter, never retry
an authentication fault, and make `PostInvoices` idempotent by carrying our own `RequestId` and
`AdditionalInformation/id` so a retry after a timeout can be reconciled rather than duplicated.

The guide warns that processing is queued and can be slow on the last day of the month, when
everyone files at once. Assume timeouts are normal there, and never treat a timeout as a failure to
submit: check status before resubmitting.
