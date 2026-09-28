# Konta.EFactura

Componentă gratuită, cu sursă deschisă, prin care un program de contabilitate se conectează la
**SIA „e-Factura”** a Serviciului Fiscal de Stat al Republicii Moldova.

> **Stadiu: versiune timpurie.** Pregătirea facturii în formatul cerut de SFS este gata și
> testată. Conexiunea directă cu serviciul SFS nu a fost încă încercată pe un cont de test real.
> Nu o folosiți încă pentru clienți.

## De ce este deschisă

Fiecare program de contabilitate din Moldova trebuie să scrie această conexiune, și toate se strică
în același timp când SFS schimbă ceva. Mai bine o întreținem împreună, la vedere.

## Ce face

- Transformă facturile în formatul XML cerut de e-Factura, cu verificările care previn greșelile
  des întâlnite (TVA scris greșit, totaluri care nu se potrivesc cu rândurile)
- **În lucru:** trimiterea facturilor, citirea stării lor, acceptarea și respingerea

## Contribuții

Sunt binevenite de la oricine lucrează cu e-Factura, inclusiv de la alte firme de software.
Vă rugăm să nu includeți IDNO-uri, denumiri de firme sau facturi reale.

## Pentru programatori

- [docs/PROTOCOL.md](docs/PROTOCOL.md) — cum funcționează serviciul, stările facturii, exemple și
  capcanele deja întâlnite
- [docs/SOAP-NOTES.md](docs/SOAP-NOTES.md) — ce rămâne de confirmat la prima conexiune
- [Ghidul oficial de integrare](https://efactura.sfs.md/Help/Ghid_integrare_Semi_Automatizata.pdf) (SFS)

---

Licența MIT — vezi [LICENSE](LICENSE). © 2026 December Capital.
