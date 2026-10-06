# What SFS gave us for e-Factura

The State Tax Service (SFS) runs e-Factura, the system Moldovan companies use to send invoices
electronically. To connect a program to it, SFS hands out a package of documents: the exact format
an invoice must have, two example invoices, lists of codes, and two guides. This folder keeps
copies, so the code is built and tested against a known version, whatever the website shows later.

None of it is secret. Passwords, test accounts and real invoices never go in this repository.

| File | What it is | Where it came from |
| --- | --- | --- |
| `TaxInvoiceSchema.xsd` | The exact format of an invoice file, with SFS's notes on each field in Romanian and Russian | SFS's package, received 2026-10-06 |
| `ModelFacturafiscala.xml` | SFS's example invoice | same |
| `ModelFacturafiscalaFarma.xml` | The same example with the extra fields for medicines | same |
| `Classificator.xml` | An example of how a list of products is imported | same |
| `Classificators.xlsx` | The codes e-Factura uses for units of measure (74 of them) and VAT rates (0, 8, 12, 20, and "-") | same |
| `Ghid_integrare_Semi_Automatizata_{ro,ENG}.pdf` | Guide for connecting a program when a person still signs each invoice by hand (34 pages, dated 2025-09-08) | same; also at `efactura.sfs.md/Help/` |
| `Ghid_integrare_Complet_Automatizata_{ro,ENG}.pdf` | Guide for connecting a program that also signs invoices itself (43 pages, dated 2025-09-08) | same |
| `Service.wsdl` | The machine-readable list of everything the service can do | downloaded 2026-10-06 from `https://efactura-api.sfs.md/Service.svc?singleWsdl` |

Don't copy SFS's two example invoices. Checked against SFS's own format, both fail: they contain
notes typed as text in the middle of the file (`// In cazul in care data lipse?te…`), and the
medicines example puts the `IsFarma` field after `CreationMotiv`, where the format wants it before.
Read them for the general shape only. Our tests check every invoice we produce against the format
file itself.

## Addresses

| Environment | Address |
| --- | --- |
| Test portal | `https://preproductie.sfs.md` |
| Test e-Factura website | `https://efactura-pre.sfs.md` |
| Test service for programs | `https://apiefactura-pre.sfs.md` (the service is at `/Service.svc`, as in production) |
| Real service for programs | `https://efactura-api.sfs.md/Service.svc` |

The test addresses only answer computers whose internet address SFS has registered; everyone else
gets `403`. The real service shows its description to anyone. A program logs in with a user name
and password sent inside each request, over HTTPS (`basicHttpBinding` with
`TransportWithMessageCredential`, contract `E_FacturaService.IService`).

## What the service can do

The guides describe 15 operations. The service itself has 19: `Test`, `GetArchivedInvoices`,
`GetBankAccountInfo` and `GetSeriaAndNumbers` are missing from both guides.

`CheckInvoicesStatus` · `GetAcceptedInvoices` · `GetArchivedInvoices` · `GetBankAccountInfo` ·
`GetInvoicesBySeriaNumber` · `GetInvoicesContentForPrint` · `GetInvoicesForSigning` ·
`GetInvoicesQRcodes` · `GetLogs` · `GetRejectedInvoices` · `GetSeriaAndNumbers` ·
`GetTaxpayersInfo` · `PostAcceptedInvoices` · `PostCanceledInvoices` · `PostInvoices` ·
`PostInvoicesWithAttachment` · `PostRejectedInvoices` · `SearchInvoices` · `Test`

When SFS sends a new version of any file, replace it here, note the date in the table and run the
tests. They check our invoices against the format file in this folder.
