# Test fixtures

What SFS's e-Factura test service answers, replayed through the real WCF binding by `ReplayTests`
and read by `DocumentReaderTests`. No real company, person, account or invoice is in this folder.

| Files | Source |
| --- | --- |
| `replay/GetTaxpayersInfo-*`, `GetBankAccountInfo-*`, `GetArchivedInvoices-*`, `GetSeriaAndNumbers-*` | In the shape the test service answered on 2026-10-08, with made-up data: series `EXMP`, ids zeroed, the blank first bank account kept. |
| `replay/PostInvoices-*`, `GetInvoicesBySeriaNumber-draft-*`, `GetInvoicesBySeriaNumber-deleted-*`, `DeleteDraftInvoice-*` | Recorded on 2026-10-22 from the VPS by `LiveTests.A_draft_is_posted_with_the_platforms_id_read_back_and_deleted_by_it`: one draft posted, answered with its id, asked for by that id, deleted, and asked for again (status 99). |
| `replay/SearchInvoices-drafts-*`, `GetInvoicesForSigning-drafts-*` | Recorded the same way: the nine drafts on the test account, listed by a search with their ids and for signing with their XML and no ids. |
| `replay/GetInvoicesBySeriaNumber-older-*`, `CheckInvoicesStatus-*` | Recorded the same way for the one signed invoice on the test account, received in 2017 and signed by the buyer, in the format before the current schema. |
| `documents/received-older.xml` | That invoice's XML, from `GetInvoicesBySeriaNumber-older`. |
| `documents/received-signed.xml` | Not recorded: a signed invoice made up in the current schema's shape. |
| `documents/signed-by-supplier.xml` | Recorded on 2026-10-27: a draft posted through the API (`konta-sign-20261027132851`), signed by the test company's director in the web interface and read back by `GetInvoicesBySeriaNumber`. Anonymised the same way; its series and number to `EXMP 000000130`, its XAdES signature removed. |

Everything recorded on 2026-10-22 is anonymised the way the konta repository's
`backend/tests/Konta.Integration.Tests/Fixtures/efactura/README.md` describes, with the same
made-up values, so the same company is the same IDNO in both repositories: the API user's
company is `1099900000028`, the company from SFS's guide our drafts are addressed to
`1099900000017`, the 2017 supplier `1099900000040` (all three unassigned on the test service);
names, addresses and VAT codes made up; every account number replaced, the same account the same
way in every file; the 2017 invoice's series and number to `EXMP 000000129`, its line made
generic; its two XAdES signatures, which carried a real person's qualified certificate, removed.
Banks' names and BICs, SFS's messages, the placeholder accounts others typed into the test
service (`12312312412`) and our own correlation ids are kept. Every GUID is numbered in the order
it first appears across these files, from `00000000-0000-0000-0000-000000000001`, so a draft's id
is the same in the post, the fetch and the deletion; timestamps are fixed.

To record again, from the VPS (the only address the test service answers), with the API user in
the environment or a git-ignored `.env` (`LiveTests` says which variables):

```bash
dotnet test --filter FullyQualifiedName~LiveTests
```

Recordings land in `fixtures/live/` at the repository root, which git ignores. They hold real
companies' data: anonymise them before copying any file here.
