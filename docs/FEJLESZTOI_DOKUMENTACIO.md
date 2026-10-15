# Invoice Sorter – fejlesztői dokumentáció

- [Áttekintés](#áttekintés)
- [Projektek](#projektek)
- [Fejlesztői környezet](#fejlesztői-környezet)
- [A feldolgozás menete](#a-feldolgozás-menete)
- [Sablonok és beállítások tárolása](#sablonok-és-beállítások-tárolása)
- [Szerver és központi konfiguráció](#szerver-és-központi-konfiguráció)
- [Surya OCR](#surya-ocr)
- [Tesztek](#tesztek)
- [Parancssori eszköz](#parancssori-eszköz)
- [Fordítás, telepítők](#fordítás-telepítők)
- [CI/CD és kiadás](#cicd-és-kiadás)
- [Arculat](#arculat)
- [Irányelvek](#irányelvek)

## Áttekintés

```
┌────────────── kliens (WPF) ──────────────┐        ┌──────── szerver (Windows-szolgáltatás) ────────┐
│ InvoiceSorter.exe                        │  HTTP  │ InvoiceSorterServer.exe (ASP.NET Core)          │
│  ├ Windows OCR, PDF-renderelés           │ ─────► │  ├ központi konfiguráció (central.json)         │
│  ├ szétbontás, mezők, adószám, pontszám  │        │  ├ Surya-sor (egy worker, FIFO, long polling)   │
│  ├ Surya: helyi Python-folyamat VAGY ────┼──────► │  └ Surya Python-folyamat (GPU)                  │
│  │        a szerver sora                 │        │ InvoiceSorterServerManager.exe (WPF, admin)     │
│  └ helyi / központi konfiguráció         │        │  └ szolgáltatás, konfiguráció, sor, napló        │
└──────────────────────────────────────────┘        └─────────────────────────────────────────────────┘
```

A felhasználói felület és minden üzenet **magyar**; a forráskód (azonosítók, megjegyzések) angol.

## Projektek

| Projekt | Leírás |
|---|---|
| `src/InvoiceSorter.Core` | a teljes feldolgozás: PDF-renderelés (`Windows.Data.Pdf`), OCR (`Windows.Media.Ocr`), szétbontás, mezőolvasás, adószám-ellenőrzés, pontszám, PDF-export (PDFsharp), napló, JSON-tárolás, Surya-kliens, a szerver kliense (`Central/`) |
| `src/InvoiceSorter.App` | WPF kliens (`InvoiceSorter.exe`), Fluent téma, CommunityToolkit.Mvvm. A `Themes/Theme.xaml`, a `StyleEditorWindow` és a `SettingsWindow` a szerverkezelőben is használt |
| `src/InvoiceSorter.Server` | ASP.NET Core minimal API, Windows-szolgáltatásként fut (`InvoiceSorterServer.exe`) |
| `src/InvoiceSorter.ServerManager` | WPF szerverkezelő (`InvoiceSorterServerManager.exe`, `requireAdministrator`) |
| `src/InvoiceSorter.Cli` | parancssori eszköz teszteléshez és tömeges futtatáshoz |
| `tests/InvoiceSorter.Tests` | xUnit tesztek |
| `installer/client`, `installer/server` | Inno Setup szkriptek |
| `scripts/` | build-szkriptek, ikonkészítő |
| `assets/` | ikonok |

Fontosabb Core-osztályok:

| Osztály | Feladat |
|---|---|
| `PageAnalyzer` | PDF-oldalak renderelése, OCR, üres oldalak, forgatás; a `ProgressInfo` állapotnevei |
| `InvoiceProcessor` | a teljes futás (`RunAsync`), szétbontás (`BuildDocuments`), olvasás, duplikátumok, javaslatok, kibocsátónév tanulása |
| `FieldReader` | sablon felismerése (`FindStyle`, `MatchScore`) és mezők olvasása (fix / terület / címke / regex) |
| `PageRefiner` | a hiányos számlák újraolvasása (nagyítás, nyelv, expozíció, Surya) |
| `VatValidator` | magyar adószám: formátum, ellenőrzőszám, ÁFA-kód, megyekód, OCR-hibák tűrése |
| `TrustScorer` | 0–100 pont és indoklás |
| `CompanyRegistry` | cégnév adószám alapján (EU VIES), gyorsítótárral |
| `PdfExporter` | mentés, fájlnevek, CSV-jelentés |
| `SuryaOcr` | Surya helyi Python-folyamatként (`surya_worker.py`, stdin/stdout JSON) vagy a szerver soraként |
| `JsonStore` | sablonok/beállítások betöltése, mentése, a beépített sablonok frissítése és migrációi |
| `Central/CentralClient` | a szerver HTTP-kliense (magyar hibaüzenetekkel) |
| `Central/CentralSync` | központi konfiguráció letöltése, helyi gyorsítótár |
| `Central/ServerConfig` | a szerver `server.json`-ja (a szerver és a szerverkezelő közös) |
| `UpdateChecker` | új verzió keresése a GitHub Releases API-n, a telepítő letöltése és indítása |

## Fejlesztői környezet

- Windows 10/11, **.NET 10 SDK**, Visual Studio 2026 / Rider / VS Code.
- Opcionális: **Inno Setup 6** (telepítőkhöz), **Python 3.10+** (Surya, ikonkészítés).

```powershell
dotnet build InvoiceSorter.slnx
dotnet test tests/InvoiceSorter.Tests
dotnet run --project src/InvoiceSorter.App -- --data D:\proba\kliens      # külön adatmappa
dotnet run --project src/InvoiceSorter.Server -- --data D:\proba\szerver  # konzolos szerver
dotnet run --project src/InvoiceSorter.ServerManager -p:NoElevation=true -- --data D:\proba\szerver
```

A `--data` kapcsoló a klienst és a szervert is külön adatmappára állítja, így a fejlesztés nem nyúl a valódi
beállításokhoz. A szerverkezelő `-p:NoElevation=true` mellett rendszergazdai jog nélkül fordul (a szolgáltatás
vezérléséhez ilyenkor nincs joga, de a konzolos szervert kezeli).

Felületi ellenőrzés kattintgatás nélkül:

```powershell
InvoiceSorter.exe minta.pdf --data D:\proba\kliens --screenshots D:\kepek     # minden képernyő PNG-be, majd kilép
InvoiceSorterServerManager.exe --data D:\proba\szerver --screenshots D:\kepek
```

## A feldolgozás menete

1. **Renderelés + OCR** (`PageAnalyzer`): oldalanként kép (`Work\<futás>\page_NNN.jpg`) és szavak koordinátákkal;
   párhuzamosan. Üres oldal: a sötét képpontok aránya a küszöb alatt. Forgatás: a négy irány OCR-jének összevetése.
2. **Surya minden oldalra** (ha *Minden oldal* mód): külön olvasatként (`PageInfo.Readings`).
3. **Szétbontás** (`InvoiceProcessor.BuildDocuments`): új számla, ha sablon ismeri fel az első oldalt, ha a vevő cég
   szerepel rajta (beállítható), vagy ha számlacím áll rajta; a folytatólap-kulcsszavak és mellékletcímek ezt
   felülírják; az azonos sablonú és számlaszámú szomszédok összevonhatók.
4. **Mezők** (`FieldReader`): a sablon mezőszabálya, tartalék szabálya, végül az általános sablon; ismert kibocsátók és
   cégnyilvántartás.
5. **Cégnevek** (`CompanyRegistry`): az új szállítói adószámok neve.
6. **Újraolvasás** (`PageRefiner`): a hiányos számlák oldalai más beállításokkal és/vagy Surya-val.
7. **Pontszám** (`TrustScorer`), duplikátumok, „nem számla” címek.

A felület a `ProgressInfo.Stage` konstansai alapján váltja a varázsló lépéseit – új lépésnévnél a konstansokat
használd, ne szövegösszehasonlítást.

## Sablonok és beállítások tárolása

- A beépített sablonok: `src/InvoiceSorter.Core/DefaultStyles.json` (beágyazott erőforrás).
- A felhasználóé: `%APPDATA%\InvoiceSorter\styles.json`, `settings.json` (`AppPaths`).
- **Beépített sablonok frissítése:** `JsonStore.BuiltInStylesVersion` növelésekor induláskor a
  `UpgradeBuiltInStyles` beolvasztja az új/módosult beépített sablonokat (a felhasználó saját sablonjai maradnak, előtte
  `styles.backup-*.json` készül). A célzott javítások migrációkként futnak (`RegexMigrations`, `FieldMigrations`,
  `RuleMigrations`, `LabelMigrations`).
- **Átnevezés:** `BuiltInStyleTexts.json` – `{id: {OldName, OldNotes, Name, Notes}}`; csak akkor nevez át, ha a
  felhasználó nem módosította a nevet/megjegyzést.
- Új beépített sablon: add hozzá a `DefaultStyles.json`-hoz (egyedi `Id`), növeld a `BuiltInStylesVersion`-t, írj
  tesztet egy kitalált adatokkal felépített oldalra (lásd `CoreTests`, `MultiCompanyTests`).

### Beállítások: helyi és központi mezők

Az `AppSettings` egy része gépfüggő; ezeket az `AppSettings.MergeCentral(local, central)` mindig a helyi
beállításokból veszi: `ConfigMode`, `CentralServerUrl`, `CentralClientKey`, `CentralRevision`, `CheckForUpdates`,
`LastSourceFolder`, `LastOutputFolder`, `SuryaPython`, `SuryaLocation`, `SuryaServerUrl`, `SuryaApiKey`,
`BuiltInStylesVersion`. Új gépfüggő mezőnél ezt a listát bővítsd, és a szerver `CentralStore`-jában a mező törlését
is.

## Szerver és központi konfiguráció

- **Adatmappa:** `ServerConfig.ResolveDataDir` (`--data`, `INVOICESORTER_SERVER_DATA`, alapértelmezés
  `%ProgramData%\TMarccci\InvoiceSorter\Server`). A szerver `AppPaths.LocalDir`/`DataDir` értékét is ide állítja.
- **Kulcsok:** `ClientKey` és `AdminKey` (első induláskor véletlenszerű), `Authorization: Bearer`, időben állandó
  összehasonlítással.
- **`CentralStore`:** `central.json` + `Revision` számláló; minden módosítás előtt mentés a `backups\` mappába (50
  marad); `PUT` csak akkor megy át, ha a `BasedOnRevision` egyezik (különben 409). Mentéskor a gépfüggő mezőket
  törli, a sablonokat rendezi. Induláskor a beépített sablonokat is frissíti.
- **`OcrQueue`:** `BackgroundService`, egy Surya worker; `LinkedList` várakozósor, `TaskCompletionSource` alapú
  értesítés a long pollinghoz; a futó feladat pozíciója 0. Időtúllépésnél vagy leállt workernél újraindítja a Suryát.
  A gazdátlan feladatokat (3 perce nem kérdezett kliens) törli, a befejezetteket 10 perc után elfelejti.
- **Kliensoldal:** a `MainViewModel` induláskor, 5 percenként és feldolgozás előtt hívja a `SyncCentralAsync`-et
  (`GET /api/config/revision`, változás esetén `GET /api/config`); a konfigurációt a `central-cache.json`-ba menti. A
  `SuryaOcr.ServerFor` központi módban a központi szervert adja vissza.
- Az API leírása: [SZERVER.md › HTTP API](SZERVER.md#http-api).

## Surya OCR

- `surya_worker.py` (beágyazott erőforrás): betölti a modelleket, majd soronként JSON-kéréseket olvas a stdin-ről
  (`{id, image}`) és a stdout-ra válaszol (`{id, width, height, lines[…]}`); az első sor `{ready, device, version}`.
- `setup_surya.ps1` (beágyazott, UTF-8 BOM-mal – a Windows PowerShell 5.1 miatt): a `-HomeDir` mappába venv,
  PyTorch (CUDA, ha van NVIDIA-kártya), `surya-ocr==0.17.1`, modellek (`MODEL_CACHE_DIR`).
- A Surya 0.17.1 a legújabb olyan változat, amely nem igényel külön inferencia-szervert.

## Tesztek

```powershell
dotnet test tests/InvoiceSorter.Tests
```

- A tesztek **kitalált** cégneveket és adószámokat használnak (`Minta Vevő 1926 Kft.`, `99991018-2-41` …). Valós
  számla, cégnév vagy adószám nem kerülhet a tárolóba.
- Az OCR-t nem futtatják: a teszt maga építi fel az oldalak szavait koordinátákkal.
- `SuryaServerClientTests`: egy `HttpListener`-es ál-szerver a szerver API-jával (sor, pozíciók, központi
  konfiguráció, hibaüzenetek).

## Parancssori eszköz

```
InvoiceSorter.Cli process <szkennelés.pdf> [--out <mappa>] [--styles <f>] [--settings <f>]   teljes futás
InvoiceSorter.Cli reprocess <workDir> [--out <mappa>]       újra szétbontás a meglévő OCR-ből
InvoiceSorter.Cli dump <workDir> <oldal>                    OCR-szavak koordinátákkal
InvoiceSorter.Cli test <workDir> <oldal> [--all]            melyik sablon hogyan olvassa az oldalt
InvoiceSorter.Cli suggest <workDir> [--all]                 javaslatok a hibás számlák kibocsátójára / számlaszámára
InvoiceSorter.Cli rotate <workDir> <oldal> <fok>            oldal forgatása és újraolvasása
InvoiceSorter.Cli learn <workDir>... --settings <f>         „kibocsátó | adószám” párok tanulása
InvoiceSorter.Cli surya-test <kép> [--server <cím> --api-key <kulcs>]   Surya próba (helyi vagy szerver)
```

## Fordítás, telepítők

```powershell
pwsh scripts/build-client.ps1 -Version 1.2.3     # publish/client/InvoiceSorter.exe + installer/client/Output/InvoiceSorter-Setup-1.2.3.exe
pwsh scripts/build-server.ps1 -Version 1.2.3     # publish/server/*.exe + installer/server/Output/InvoiceSorter-Server-Setup-1.2.3.exe
```

- Mindhárom program **self-contained, single-file** exe (win-x64), .NET futtatókörnyezet nélkül fut.
- Az Inno Setup fordítót (`ISCC.exe`) a `scripts/find-iscc.ps1` keresi (PATH, majd a szokásos telepítési helyek);
  ha nincs, csak az exe-k készülnek el (`-NoInstaller` kapcsolóval szándékosan is).
- **Kliens telepítő** (`installer/client/InvoiceSorter.iss`): `C:\Program Files\TMarccci\InvoiceSorter`, magyar
  nyelvű, Start menü, opcionális asztali ikon és „Társítás” bejegyzés PDF-hez. A felhasználói adatokat nem törli.
- **Szerver telepítő** (`installer/server/InvoiceSorterServer.iss`): `C:\Program Files\TMarccci\InvoiceSorter Server`,
  `sc create` szolgáltatás (`delayed-auto`, újraindítás hibánál), tűzfalszabály, adatmappa ACL (SYSTEM +
  Administrators). Frissítésnél leállítja a szolgáltatást; eltávolításkor törli a szolgáltatást és a tűzfalszabályt,
  az adatmappát megtartja.
- Az `AppId` GUID-okat ne változtasd – ezekből ismeri fel a Windows a frissítést.

## CI/CD és kiadás

- **CI** (`.github/workflows/ci.yml`) – minden `main` push és pull request: fordítás, tesztek, mindkét telepítő
  (Inno Setup a `choco install innosetup` paranccsal), artefaktumként 14 napig. Verzió: a legutóbbi kiadási címke
  utáni patch + `-ci.<futásszám>`.
- **Kiadás** (`.github/workflows/release.yml`) – a verzió a **git címkéből** jön:
  - `git tag v1.4.0` és `git push origin v1.4.0` (a `v` elhagyható), vagy
  - *Actions › Release › Run workflow* (`patch` / `minor` / `major`): a workflow maga készíti el a következő címkét.

  A kiadás csatolmányai: `InvoiceSorter-Setup-x.y.z.exe`, `InvoiceSorter-Server-Setup-x.y.z.exe`, `SHA256SUMS.txt`.
- A kliens az `UpdateChecker`-rel a `releases/latest` végpontot kérdezi; a címke verzióját a futó program
  `AssemblyInformationalVersion`-jével veti össze. Ezért a kiadás ne legyen *pre-release*, és a címke
  `x.y.z` / `vx.y.z` alakú legyen. A kliens és a szerverkezelő frissítés gombja (`UpdateInstaller`) a kiadás
  csatolmányai közül a nevük alapján választja ki a telepítőt (`InvoiceSorter-Setup-` / `InvoiceSorter-Server-Setup-`),
  letölti a `%TEMP%\InvoiceSorter-update` mappába, elindítja, és bezárja a programot – a csatolmányok nevét ne
  változtasd meg.

## Arculat

- Fő szín: indigó **#6366F1** (sötétebb: #4F46E5, halvány háttér: #EEF2FF), fejléc: #1E293B – a
  `src/InvoiceSorter.App/Themes/Theme.xaml` tartalmazza (`AccentBrush`, `AccentDarkBrush`, `AccentSoftBrush`).
- Ikonok: lekerekített indigó csempe fehér vonalas rajzzal – kliens: számla, szerver: szerverszekrény. Forrás:
  `scripts/make-icon.py` (Pillow), kimenet: `assets/*.ico`, `*-256.png`, `favicon-32.png`.

  ```powershell
  python scripts/make-icon.py
  ```

## Irányelvek

- **Felület:** minden látható szöveg magyar (gombok, üzenetek, napló, hibaüzenetek, jelentés). A belső kulcsok
  (pl. a `MarkEdited` értékei) angolok maradnak, a megjelenítéshez szótár fordít (`EditNames` a `PageModels.cs`-ben).
- **Adatvédelem:** a tároló nyilvános – valós számla, cégnév, adószám, beszállítói kapcsolat nem kerülhet bele (sem
  tesztbe, sem sablonba, sem dokumentációba). A `example/` mappa a `.gitignore`-ban van.
- **Kompatibilitás:** a `settings.json`/`styles.json` új mezői kapjanak alapértéket, hogy a régi fájlok betöltődjenek;
  a szerver API változásánál növeld a `ServerHealth.CurrentApi` értékét.
- Új funkcióhoz írj tesztet; a `dotnet test` és a CI legyen zöld.
