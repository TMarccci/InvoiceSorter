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
| `InvoiceProcessor` | a teljes futás (`RunAsync`), szétbontás (`BuildDocuments`), olvasás, duplikátumok, javaslatok, kibocsátónév tanulása; kézi műveletek: `SplitAt`, `MergeInto`, `MovePage`, `DeletePage` / `RestorePage` (a törölt oldal `PageInfo.Deleted`, a `BuildDocuments` kihagyja) |
| `FieldReader` | sablon felismerése (`FindStyle`, `MatchScore`) és mezők olvasása (fix / terület / címke / regex) |
| `PageRefiner` | a hiányos számlák újraolvasása (Surya, majd a mentett oldalkép 2× nagyítva, nyelv, expozíció; a PDF-ből 300–600 DPI-n újrarajzolt kép mérve nem volt jobb – CLI `rerender-test`) |
| `DateReader` | a három dátum: dátumkeresés (formátumok, OCR-javítás, időszakok), címkék a sorban és oszlopfejlécként, döntés az olvasatok között, egydátumos szabály, ellenőrzések, javaslatok |
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

## Felismerési szabályok (pontosság)

- **Fuzzy szövegegyezés** (`TextUtil.FuzzyContainsText`): az egész szöveg legfeljebb `FuzzyTolerance` (20 %) hibával,
  és az illesztésen *belül* minden 4+ betűs szó legfeljebb `ceil(hossz × 20 %)` (min. 1) hibával. Így a
  „Vasalatkereskedelmi Kft” nem lesz „METRO KERESKEDELMI KFT” (a 4 hiba mind a „METRO”-ra esett), a valódi OCR-hibák
  („szałnlazz.hu”, „Ellerdrző”) viszont egyeznek.
- **Állandó kibocsátó** (`InvoiceProcessor.FixedIssuer`): a sablon `Fixed` neve csak akkor kerül be, ha a lap igazolja
  (a név szerepel bármely olvasatban, vagy a szállító adószáma ehhez a céghez tartozik); különben az adószám cége vagy
  üres, `FixedIssuerNotOnPageNote` jegyzettel és −15 ponttal.
- **Oldalszámláló** (`PageCounterOf`: „Oldal 2”, „Oldal: 2/3”, „Oldalszám 1/4”): a következő oldal folytatás, ha
  azonos a sablon, vagy az előző számláló közvetlenül előtte van (összeggel azonos összeggel, összeg nélkül összeg
  nélkül; a közbeeső, számláló nélküli ÁSZF-lap nem szakítja meg) – kivéve, ha a számlában már van ilyen oldal, vagy
  a lapon más számlaszám olvasható (fordított sorrendben szkennelt köteg: „2/2”, „1/2” | „2/2”, „1/2”).
- **Kulcs–érték blokk** (`FieldReader.IsNextKeyValueRow`): a címke alatti sor nem érték, ha a címke oszlopában egy
  másik címkével kezdődik, és az értéke közvetlenül a címkénk jobb oldalán áll („Számlaszám” alatt „Vevőszám 765432”).
  Cégnév (Kft., Zrt. …) nem kulcs.
- **Általános számlaszám-szabály** (11. sablonverzió, migrációval): magyar nagybetűk a számban („2026/1234GYŰJTŐ”),
  dátumdarab nem számlaszám („2026,09.18” → nem „09.18”), az OCR-ben egy számjegyet vesztett bankszámlaszám sem;
  „Okirat száma” címke; a kétnyelvű címke vége („No.:”, „/ Number:”) kimarad. A Billingo, Számlázz.hu, Okos-Ügyvitel
  és webshop sablon számában kell számjegy (a Billingo-ban kötőjel/perjel is), különben az általános címkék.
- **Fordítva szkennelt számla:** „1/2” közvetlenül a „2/2” után, azonos szállítói adószámmal → egy számla.
- **Ékezetek a cégnyilvántartásból** (`TextUtil.AccentsFrom`): a lapon olvasott név ékezetei a regisztrált név szerint
  („HÓSZIG” → „HŐSZIG”), csak hozzáad / javít, nem töröl.
- **Kisbetűs számlaszám** −10 pont (gyakran OCR-zaj: „301234567u”).
- **Számlaszám a többi oldalról** (`InvoiceNumberOf`): ha egy későbbi oldal a teljes számot mutatja, amelynek az első
  oldalé csak egy része („45678” ⊂ „412345678”), a teljes nyer.

Regressziós ellenőrzéshez: `InvoiceSorter.Cli process <pdf>` egyszer (OCR + újraolvasások → `pages.json`), utána
`reprocess <workDir> --no-refine [--why]` a régi és az új builddel, és a kimenetek összevetése (`--why`: a
megbízhatósági levonások is).

## Dátumok (DateReader)

Elv: **csak biztos értéket tölt ki.** Lépések:

1. `FindDates` – OCR-soronként reguláris kifejezések: év elöl (`2026.09.02.`, `2026. 09. 02.`, `2026-09-02`), nap elöl
   (`21.04.2026`), hónapnévvel (`2026. szeptember 2.`), pont nélküli `2026 08 28`. OCR-javítás: O/o/C→0, I/l/|→1, B→8
   (jelölve: `Repaired`); csonka dátum (`2026.08.1`) és egybetűs komponens elvetve. Időszakok (`… - …`) `InRange`.
2. `Label` – a dátum előtti címke ugyanabban a sorban (az előző dátumig; a címke után legfeljebb 3 szó), és a
   felette álló oszlopfejléc (legfeljebb 3 sor, az első érték-sorig; a fejlécnek vízszintesen fednie kell a dátumot;
   kettőspontos „címke: érték” sor nem fejléc). A címkéket a `Settings.*DateLabels` és `NotDateLabels` adja; a
   leghosszabb, legkevesebb hibájú illeszkedés nyer (max. 2–3 karakter hiba). Ellentmondó címkék: `Conflict`.
   Folyó szövegben álló dátum (`InText`) nem kap fejlécet.
3. `Decide` – fajtánként: a sablon saját szabálya (`InvoiceStyle.IssueDate` …) elsőbbséget élvez; különben az első
   oldal (amelyiken van) címkézett dátumai. Az alap olvasat dönt, az újraolvasások nem mondhatnak ellent neki; csak
   újraolvasásból két egyező olvasat kell. Több eltérő érték → `Ambiguous` (üresen marad).
4. Egydátumos szabály (`IsSingleDateDocument`: nyugta-sablon, kézi tömb, keskeny lap, vagy a fizetési mód sor elején /
   a „Fizetés módja” után): egy címkézett érték → a hiányzók azonosak; címke nélkül pontosan egy (nem időszak, nem
   szövegbeli) dátum → mindhárom.
5. `Problems` (hibák az `InvoiceDocument.BaseErrors`-ban) és `Doubts` (pontlevonás a `TrustScorer`-ben).

A dátumok a `PageRefiner.IsResolved` feltételei között is szerepelnek: hiányzó dátum újraolvasást (és Surya-t) indít.
Pontosságméréshez: `InvoiceSorter.Cli dates <workDir> [--tokens] [--why] [--json f]` – számlánként a három dátum,
forrás, problémák, minden talált dátum a címkéjével és a környezetével.

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
InvoiceSorter.Cli reread <workDir> <oldal> [--dpi 400] [--pdf <f>]   az oldal a PDF-ből újrarajzolva, a normál olvasat mellett
InvoiceSorter.Cli rerender-test <workDir> [--dpi 300,400,600] [--pdf <f>]   újraolvasás mentett képből / PDF-ből, összevetés
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
- A kliens az `UpdateChecker`-rel a kiadások listáját kérdezi (`releases?per_page=30`), és a **legmagasabb
  verziót** választja (nem a GitHub „latest” jelölését: egy régebbi verzió később kiadott javítása – pl. 1.2.1 a 1.3.0
  után – különben elrejtené az újat); a vázlatok és a *pre-release*-ek kimaradnak. A címke `x.y.z` / `vx.y.z` alakú
  legyen; a futó program `AssemblyInformationalVersion`-jével veti össze. A release workflow is csak a legmagasabb
  verziót jelöli „latest”-nek (`gh release create --latest=true|false`).
- A kliens és a szerverkezelő frissítés gombja (`UpdateInstaller`) a csatolmányok közül a nevük alapján választja ki a
  telepítőt (`InvoiceSorter-Setup-` / `InvoiceSorter-Server-Setup-`), letölti a `%TEMP%\InvoiceSorter-update`
  mappába, és `/SILENT /SP- /SUPPRESSMSGBOXES /NOCANCEL /NORESTART /CLOSEAPPLICATIONS` kapcsolókkal indítja; csendes
  telepítés után az Inno Setup `[Run]` szakasza (`Check: WizardSilent`) újraindítja a klienst (a bejelentkezett
  felhasználóként) / a szerverkezelőt. A csatolmányok nevét ne változtasd meg.

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
