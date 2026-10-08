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
      through an SSH tunnel to 91.108.122.125, `LiveTests`). It answers `Status` 2 and puts
      `User name: <api user> from v2` in `RequestId`, so it does authenticate. What the other
      `Status` values mean is still to read. The service's clock is local time (+03:00).
- [x] A wrong password is not a SOAP fault: the service answers an HTML error page with HTTP 500,
      which WCF raises as a `ProtocolException` about content type `text/html`. Treat that as
      "authentication refused or server error", never retry it blindly.
- [x] An unregistered address gets `403` before any SOAP, on the test service, portal and website
      alike. A developer elsewhere reaches them through the registered box, for example
      `ssh -N -D 127.0.0.1:1080 my-vps` and `KONTA_EFACTURA_PROXY=socks5://127.0.0.1:1080`.
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
- [ ] The API user's company IDNO on the test service, so a valid invoice can be posted, then found
      by `SearchInvoices` with `APIeInvoiceId` (our `AdditionalInformation/id`, to confirm) and
      checked with `CheckInvoicesStatus`.
- [ ] What `SearchParameters.InvoiceStatus` 0 does: the wire always carries it, and 0 is also Draft.
- [ ] Whether the session token must be presented explicitly on later calls, or whether WCF's
      per-call credentials are sufficient. The guide says a token is returned; its examples set
      `ClientCredentials` on every call and do nothing visible with the token.
- [ ] Exact fault types. The guide names `AuthenticationFailedException`; find the rest.
- [ ] Throttling and batch limits for `PostInvoices`.
- [ ] Back-dating window. A limit is reported; confirm the exact number of days.
- [ ] Whether `GetTaxpayersInfo` is usable for general IDNO validation, or only for counterparties
      you already trade with. If the former, it may reduce our dependence on MConnect.

## Resilience

Wrap every call: retry on transport faults and 5xx with exponential backoff and jitter, never retry
an authentication fault, and make `PostInvoices` idempotent by carrying our own `RequestId` and
`AdditionalInformation/id` so a retry after a timeout can be reconciled rather than duplicated.

The guide warns that processing is queued and can be slow on the last day of the month, when
everyone files at once. Assume timeouts are normal there, and never treat a timeout as a failure to
submit: check status before resubmitting.
