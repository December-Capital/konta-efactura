# SFS's own documents for SIA „e-Factura”

Copies of what the State Tax Service (SFS) publishes, kept here so the client is built and tested
against a fixed version rather than whatever the website serves today. Nothing here is a secret:
no credentials, no test accounts, no real invoices. Those never go in this repository.

| File | What it is | Where it came from |
| --- | --- | --- |
| `TaxInvoiceSchema.xsd` | The schema of the `<Documents>` payload, with SFS's notes on each field (RO/RU) | SFS integration package, received 2026-10-06 |
| `ModelFacturafiscala.xml` | SFS's example invoice | same |
| `ModelFacturafiscalaFarma.xml` | The same with the pharmaceutical fields (`Farm*`, `IsFarma`) | same |
| `Classificator.xml` | Example of the goods classifier import format | same |
| `Classificators.xlsx` | The platform's ids for units of measure (74) and VAT rates (0, 8, 12, 20, "-") | same |
| `Ghid_integrare_Semi_Automatizata_{ro,ENG}.pdf` | Integration guide, semi-automated mode (34 pages, dated 2025-09-08) | same; also at `efactura.sfs.md/Help/` |
| `Ghid_integrare_Complet_Automatizata_{ro,ENG}.pdf` | Integration guide, automated mode, with XAdES-BES signing (43 pages, dated 2025-09-08) | same |
| `Service.wsdl` | The service contract, `?singleWsdl` of the production endpoint | fetched 2026-10-06 from `https://efactura-api.sfs.md/Service.svc?singleWsdl` |

Neither SFS example is valid against its own schema as shipped (checked with lxml): both carry
`// In cazul in care data lipse?te…` notes as text inside `SupplierInfo` and `VehicleLogbook`, and
the pharmaceutical one puts `IsFarma` after `CreationMotiv`, where the schema wants it before. Read
them for the shape, never copy them. The tests validate our own output against the XSD.

## Addresses

| Environment | Address |
| --- | --- |
| Test portal | `https://preproductie.sfs.md` |
| Test e-Factura web interface | `https://efactura-pre.sfs.md` |
| Test API | `https://apiefactura-pre.sfs.md` (service at `/Service.svc`, as in production) |
| Production API | `https://efactura-api.sfs.md/Service.svc` |

The three test addresses answer `403` to any client address SFS has not registered; production
serves its WSDL to anyone. The binding is `basicHttpBinding` with `TransportWithMessageCredential`
(HTTPS, user name and password in the SOAP header); the contract is `E_FacturaService.IService`.

## Operations in the WSDL

19, not the 15 the guides list: `Test`, `GetArchivedInvoices`, `GetBankAccountInfo` and
`GetSeriaAndNumbers` are in the contract and not described in either guide.

`CheckInvoicesStatus` · `GetAcceptedInvoices` · `GetArchivedInvoices` · `GetBankAccountInfo` ·
`GetInvoicesBySeriaNumber` · `GetInvoicesContentForPrint` · `GetInvoicesForSigning` ·
`GetInvoicesQRcodes` · `GetLogs` · `GetRejectedInvoices` · `GetSeriaAndNumbers` ·
`GetTaxpayersInfo` · `PostAcceptedInvoices` · `PostCanceledInvoices` · `PostInvoices` ·
`PostInvoicesWithAttachment` · `PostRejectedInvoices` · `SearchInvoices` · `Test`

When SFS sends a new version of any of these, replace the file, say so in this table, and run the
tests: they validate against the XSD here.
