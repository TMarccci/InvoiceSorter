<p align="center">
  <img src="assets/invoicesorter-256.png" width="96" alt="Invoice Sorter" />
</p>

<h1 align="center">Invoice Sorter</h1>

<p align="center">
  Szkennelt számlák szétbontása, olvasása, ellenőrzése és átnevezése – helyben, felhőszolgáltatás nélkül.
</p>

<p align="center">
  <a href="https://github.com/TMarccci/InvoiceSorter/releases/latest">Letöltés</a> ·
  <a href="docs/FELHASZNALOI_UTMUTATO.md">Felhasználói útmutató</a> ·
  <a href="docs/SZERVER.md">Szerver</a> ·
  <a href="docs/FEJLESZTOI_DOKUMENTACIO.md">Fejlesztői dokumentáció</a>
</p>

---

Az **Invoice Sorter** Windows-alkalmazás a dokumentumszkenner által készített nagy, sok számlát tartalmazó PDF-et
**számlákra bontja**, kiolvassa a **kibocsátót** és a **számlaszámot**, ellenőrzi a **vevő adószámát**, majd minden
számlát külön PDF-be ment `{Számlaszám}_{Kibocsátó}.pdf` néven.

A feldolgozás teljesen a saját gépeden (vagy a saját szervereden) fut: az oldalakat a Windows beépített
szövegfelismerése olvassa, a bizonytalan oldalakhoz opcionálisan a [Surya](https://github.com/datalab-to/surya)
MI-alapú OCR is használható. Internet, MI-szolgáltatás vagy kredit nem kell hozzá.

## Fő funkciók

- **Automatikus szétbontás** – a sablonok, a számlacímek és a vevő cég alapján; üres hátoldalak elhagyása, elforgatott
  oldalak kiegyenesítése, többoldalas számlák összevonása.
- **Dátumok** – kiállítás, teljesítés, fizetési határidő a címkéjük alapján; ahol nem egyértelmű, üresen marad, és a
  lapon talált dátumokat kínálja fel; nyugtán és készpénzes számlán az egyetlen dátum mindhárom.
- **Adatkiolvasás sablonokkal** – a gyakori magyar számlázóprogramokhoz (Számlázz.hu, Billingo, NAV Online Számlázó,
  pénztárgépes nyugták …) beépített sablonok; saját sablon pár kattintással, a lapon rajzolva.
- **Ellenőrzés** – vevő adószáma (ellenőrzőszámmal), duplikátumok, „nem számla” oldalak, 0–100 pontos megbízhatóság.
- **Kényelmes javítás** – javaslatok egy kattintással, szétvágás, összevonás, oldalsorrend, forgatás, kizárás.
- **Surya OCR** (opcionális) – a halvány, apró betűs oldalak újraolvasása helyben vagy a szerveren.
- **Központi konfiguráció** – az Invoice Sorter szerveren a rendszergazda egy helyen kezeli a sablonokat és a
  beállításokat; a kliensek automatikusan megkapják. A szerver közös Surya OCR-t ad sorban állással.
- **Frissítésjelzés** – a program jelzi, ha új verzió érhető el.

## Telepítés

| Mit | Fájl (a [kiadások](https://github.com/TMarccci/InvoiceSorter/releases/latest) oldaláról) | Hová kerül |
|---|---|---|
| **Kliens** – minden gépre, ahol számlákat dolgoznak fel | `InvoiceSorter-Setup-x.y.z.exe` | `C:\Program Files\TMarccci\InvoiceSorter` |
| **Szerver** (opcionális) – egy gépre a hálózaton, lehetőleg NVIDIA videokártyával | `InvoiceSorter-Server-Setup-x.y.z.exe` | `C:\Program Files\TMarccci\InvoiceSorter Server` |

Követelmény: Windows 10 1809 vagy újabb (64 bites), Windows 11. Más futtatókörnyezetet nem kell telepíteni.

Szerver nélkül a kliens önmagában is teljes értékű (**helyi konfiguráció**). Több gépnél érdemes a szervert
telepíteni, és a klienseken a *Beállítások › Konfiguráció* oldalon a **központilag kezelt** módot választani – lásd
[SZERVER.md](docs/SZERVER.md).

## Dokumentáció

| Dokumentum | Kinek |
|---|---|
| [Felhasználói útmutató](docs/FELHASZNALOI_UTMUTATO.md) | a program használata: feldolgozás, ellenőrzés, sablonok, beállítások |
| [Szerver](docs/SZERVER.md) | telepítés, szerverkezelő, központi konfiguráció, Surya-sor, API |
| [Fejlesztői dokumentáció](docs/FEJLESZTOI_DOKUMENTACIO.md) | felépítés, fordítás, tesztek, CI/CD, kiadás, telepítők |

## Gyors fordítás forrásból

```powershell
dotnet test tests/InvoiceSorter.Tests              # tesztek (.NET 10 SDK)
dotnet run --project src/InvoiceSorter.App         # kliens indítása
pwsh scripts/build-client.ps1 -Version 1.2.3       # kliens exe + telepítő (Inno Setup 6)
pwsh scripts/build-server.ps1 -Version 1.2.3       # szerver + szerverkezelő + telepítő
```

## Adatvédelem

- A szkennelt számlák, a beállítások és a sablonok a gépen (vagy a saját szervereden) maradnak.
- Internetre csak két, kikapcsolható dolog megy ki: a kibocsátó **8 jegyű adószáma** az EU VIES nyilvános
  cégnyilvántartásába (cégnév lekérdezése), és a **frissítésellenőrzés** a GitHubra.
- A tárolóban nincs valós cég- vagy számlaadat; a tesztek kitalált cégneveket és adószámokat használnak.

## Licenc

[MIT](LICENSE). A Surya OCR kódja GPL-3.0 (külön folyamatként fut, nem része a programnak), a modellsúlyoknak saját
licencük van – üzleti használat előtt nézd meg: <https://github.com/datalab-to/surya>.
