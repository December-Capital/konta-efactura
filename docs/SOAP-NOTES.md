# Notes on the SOAP client

Working notes for whoever implements the transport. Everything here comes from the published
integration guide, not from a live round trip — mark each item verified as you confirm it.

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

Generate the contract from the WSDL with `dotnet-svcutil`, then commit the generated file so a
change in the service surface shows up as a reviewable diff:

```bash
dotnet tool install --global dotnet-svcutil
dotnet-svcutil <wsdl-url> --outputFile src/Konta.EFactura/Generated/EFacturaService.cs
```

## To confirm against a test account

- [ ] The endpoint URL for test, and whether production differs by host or by path only.
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
submit — check status before resubmitting.
