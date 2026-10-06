# Konta.EFactura

e-Factura este sistemul Serviciului Fiscal de Stat (SFS) prin care firmele din Moldova își trimit
facturile electronic. Un program de contabilitate trebuie să știe să vorbească cu acest sistem:
să-i trimită facturi și să afle ce s-a întâmplat cu ele. Aici este partea de program care
face asta. Este gratuită și oricine o poate folosi sau îmbunătăți.

> **Stadiu: versiune timpurie.** Programul pregătește corect factura în forma cerută de SFS, iar
> testele o verifică după schema oficială a SFS. Nu a trimis încă nicio factură, pentru că
> accesul nostru la mediul de test al SFS este în curs de pregătire. Nu o folosiți încă pentru
> clienți.

## De ce este deschisă

Fiecare program de contabilitate din Moldova trebuie să scrie această legătură, și toate se strică
deodată când SFS schimbă ceva. E mai simplu să o întreținem împreună, la vedere.

## Ce face

Transformă o factură în fișierul pe care îl cere e-Factura și oprește greșelile care apar des:
TVA-ul scris ca 0,20 în loc de 20, totaluri care nu dau cât suma rândurilor, câmpuri obligatorii
lipsă.

Urmează trimiterea facturilor, aflarea stării lor, acceptarea și respingerea.

Documentele primite de la SFS, cu adresele de test și de producție, sunt în
[spec/sfs](spec/sfs/README.md).

## Contribuții

Oricine lucrează cu e-Factura poate contribui, inclusiv alte firme de software. Vă rugăm să nu
puneți în exemple coduri fiscale, denumiri de firme sau facturi reale.

## Pentru programatori

Pachetul NuGet `Konta.EFactura` se publică automat din GitHub Actions la fiecare etichetă
`v<versiune>` (de exemplu `v0.1.0-alpha.1`), în GitHub Packages:
`https://nuget.pkg.github.com/December-Capital/index.json`.

- [docs/PROTOCOL.md](docs/PROTOCOL.md): cum funcționează serviciul, stările facturii, exemple și
  capcanele întâlnite deja
- [docs/SOAP-NOTES.md](docs/SOAP-NOTES.md): ce rămâne de confirmat la prima conexiune
- [spec/sfs](spec/sfs/README.md): schema, ghidurile și contractul serviciului, așa cum le-a dat SFS

---

Licența MIT, vezi [LICENSE](LICENSE). © 2026 December Capital.
