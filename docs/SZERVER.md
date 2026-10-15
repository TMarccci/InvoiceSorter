# Invoice Sorter szerver

Az Invoice Sorter szerver két dolgot ad a hálózat klienseinek:

1. **Központi konfiguráció** – a sablonokat és a beállításokat egy helyen, grafikus felületen (szerverkezelő)
   szerkeszted; a központi módban futó kliensek automatikusan megkapják őket.
2. **Közös Surya OCR sorban állással** – a Surya modell egyszer, a szerver videokártyáján töltődik be; a kliensek
   ide küldik a bizonytalan oldalakat. Ha valaki éppen használja, a többiek oldalai sorba állnak, és érkezési
   sorrendben kerülnek sorra.

- [Követelmények](#követelmények)
- [Telepítés](#telepítés)
- [A kliensek bekötése](#a-kliensek-bekötése)
- [Szerverkezelő](#szerverkezelő)
- [Surya OCR a szerveren](#surya-ocr-a-szerveren)
- [Biztonság](#biztonság)
- [Adatok, mentés, eltávolítás](#adatok-mentés-eltávolítás)
- [Konzolos futtatás](#konzolos-futtatás)
- [HTTP API](#http-api)
- [Hibaelhárítás](#hibaelhárítás)

## Követelmények

- Windows 10 1809+ / Windows 11 / Windows Server 2019+ (64 bites).
- Központi konfigurációhoz bármilyen gép elég.
- Surya OCR-hez ajánlott egy **NVIDIA videokártya** (CUDA, legalább 6 GB VRAM); videokártya nélkül is fut, de
  lassan. A Surya telepítéséhez **Python 3.10 vagy újabb** kell (python.org, „Install for all users” opcióval), és kb.
  5 GB szabad hely.

## Telepítés

1. Töltsd le az `InvoiceSorter-Server-Setup-x.y.z.exe` fájlt a
   [kiadások oldaláról](https://github.com/TMarccci/InvoiceSorter/releases/latest), és futtasd rendszergazdaként.
2. A telepítő:
   - a programot a `C:\Program Files\TMarccci\InvoiceSorter Server` mappába másolja
     (`InvoiceSorterServer.exe` – a szolgáltatás, `InvoiceSorterServerManager.exe` – a szerverkezelő);
   - létrehozza és elindítja az **InvoiceSorterServer** Windows-szolgáltatást (késleltetett automatikus indulás,
     hiba esetén újraindul);
   - tűzfalszabályt nyit a **8770-es TCP portra** (kikapcsolható);
   - létrehozza az adatmappát: `C:\ProgramData\TMarccci\InvoiceSorter\Server` (csak a rendszergazdák és a SYSTEM
     érik el).
3. A végén megnyílik a **szerverkezelő**. Az *Áttekintés* oldalon látod a szerver címét és a **kliens kulcsot**.

**Frissítés:** ha új verzió jelenik meg, a szerverkezelő fejlécében megjelenik az **„Új verzió érhető el”** gomb –
erre kattintva letölti és elindítja az új szerver-telepítőt. (Kézzel is futtathatod az új telepítőt.) A telepítő
leállítja a szolgáltatást, kicseréli a fájlokat, és újraindítja; a konfiguráció és a kulcsok megmaradnak.

Első induláskor a szerver a beépített sablonokkal hozza létre a központi konfigurációt. Ha egy kliensen már
kialakult egy jó helyi konfiguráció, vedd át: *Központi konfiguráció › Helyi konfiguráció importálása…*, és
válaszd ki a kliens `%APPDATA%\InvoiceSorter\styles.json` és `settings.json` fájlját.

## A kliensek bekötése

Minden kliensen: *Beállítások › Konfiguráció › Központilag kezelt konfiguráció*

- **Szerver címe:** pl. `http://SZERVER:8770` (a szerverkezelő *Áttekintés* oldaláról másolható);
- **Kliens kulcs:** a szerverkezelőből;
- **Kapcsolat tesztelése**, majd **Mentés**.

A kliens ettől kezdve a szerver sablonjait és beállításait használja, és a Surya OCR-t is a szerveren futtatja.
Részletek: [felhasználói útmutató](FELHASZNALOI_UTMUTATO.md#helyi-és-központi-konfiguráció).

## Szerverkezelő

Start menü › *Invoice Sorter szerverkezelő* (rendszergazdai jogot kér).

| Oldal | Mire jó |
|---|---|
| **Áttekintés** | a szolgáltatás állapota (indítás, leállítás, újraindítás), Surya állapota, sor, konfiguráció változata; a kliensekhez szükséges cím és kliens kulcs (másolás) |
| **Központi konfiguráció** | **Sablonok szerkesztése** és **Beállítások szerkesztése** – ugyanaz a szerkesztő, mint a kliensben; a *Mentés a szerverre* után a kliensek automatikusan megkapják. Importálás egy kliens helyi konfigurációjából, exportálás fájlba, biztonsági mentések |
| **Surya-sor** | élő lista a sorban álló, futó és befejezett oldalakról (melyik gép, mikor, hiba); Surya újraindítása |
| **Szerver beállításai** | port, figyelt cím, HTTPS-tanúsítvány, kliens kulcs (új kulcs készítése), Surya be/ki, Python, sorhossz, időkorlát, max. képméret; Surya telepítése. *Mentés és újraindítás* |
| **Napló** | a szerver legutóbbi naplófájlja |

A központi konfiguráció minden mentése új **változatot** kap. Ha két rendszergazda egyszerre szerkeszt, a később mentő
figyelmeztetést kap (a másik módosítása nem vész el), és újra kell nyitnia a szerkesztőt.

## Surya OCR a szerveren

1. Telepítsd a Pythont (3.10 vagy újabb, „Install for all users”).
2. Szerverkezelő › *Szerver beállításai* › **Surya telepítése…** – egy PowerShell-ablakban a szerver adatmappájába
   (`…\Server\surya\venv`) telepíti a PyTorch-ot és a Surya OCR-t, és letölti a modelleket (10–30 perc).
3. *Surya-sor* › **Surya újraindítása** (vagy a szolgáltatás újraindítása). Az *Áttekintés* oldalon a Surya állapota
   „Kész · cuda” lesz.

Működés:

- a szerver egyszerre **egy oldalt** olvas; a többi érkezési sorrendben vár (alapértelmezés szerint legfeljebb 500);
- a kliensek oldalanként küldenek, és megvárják az eredményt, így több gép oldalai felváltva kerülnek sorra;
- a kliens mutatja, hányadik a sorban; ha megszakítják a feldolgozást, az oldala kikerül a sorból;
- ha egy kliens 3 percig nem jelentkezik, a várakozó oldalait törli; a befejezett feladatok 10 perc után eltűnnek;
- ha a Surya lefagy vagy leáll (időkorlát: oldalanként 10 perc), a szerver újraindítja; a kliens az adott oldalt
  Windows OCR-rel olvassa.

Ha a Surya ki van kapcsolva vagy hibás, a központi konfiguráció ettől függetlenül működik.

## Biztonság

- **Kliens kulcs:** a kliensek ezzel olvassák a konfigurációt és küldenek oldalakat. *Új kulcs* után minden kliensen
  át kell írni.
- **Adminisztrátori kulcs:** csak a konfiguráció módosításához kell; a szerverkezelő a `server.json`-ból olvassa, a
  kliensekhez nem kell.
- A kulcsok a `server.json`-ban vannak; az adatmappát csak a rendszergazdák és a SYSTEM érik el.
- Alapértelmezés szerint a szerver **HTTP**-n fut – belső hálózatra ez elég. Megbízhatatlan hálózaton adj meg
  HTTPS-tanúsítványt (`.pfx`) a *Szerver beállításai* oldalon, és a klienseken `https://` címet használj.
- *Figyelt cím* `127.0.0.1`: a szerver csak a saját gépéről érhető el.
- A Surya-sorba küldött oldalképek feldolgozás után azonnal törlődnek a szerverről.

## Adatok, mentés, eltávolítás

Adatmappa: `C:\ProgramData\TMarccci\InvoiceSorter\Server`

| Fájl / mappa | Tartalom |
|---|---|
| `server.json` | port, kulcsok, Surya-beállítások |
| `central.json` | a központi konfiguráció (sablonok + beállítások, változatszám) |
| `backups\central-NNNNN.json` | a központi konfiguráció előző 50 változata |
| `Logs\` | napi naplófájlok |
| `surya\` | a Surya Python-környezete és a modellek |
| `tmp\` | a feldolgozás alatt álló oldalképek (azonnal törlődnek) |

Mentéshez elég a `server.json` és a `central.json` (vagy az egész mappa) másolata. Visszaállítás: állítsd le a
szolgáltatást, másold vissza a fájlokat, indítsd el újra.

Eltávolítás (*Gépház › Alkalmazások*): leállítja és törli a szolgáltatást és a tűzfalszabályt. Az adatmappa
**megmarad**; ha nem kell, töröld kézzel.

## Konzolos futtatás

Fejlesztéshez vagy próbához szolgáltatás nélkül is futtatható:

```powershell
InvoiceSorterServer.exe --data D:\proba\szerver-adatok
```

A `--data` (vagy az `INVOICESORTER_SERVER_DATA` környezeti változó) más adatmappát ad meg; Ctrl+C leállítja. A
szerverkezelő is elfogadja a `--data` kapcsolót.

## HTTP API

Minden kéréshez kulcs kell: `Authorization: Bearer <kulcs>`. A kliensek a gépük nevét az `X-Client-Name` fejlécben
küldik (ez látszik a sorban). A válaszok JSON-ok; hiba esetén `{"error": "…"}` magyar üzenettel.

| Metódus és útvonal | Kulcs | Leírás |
|---|---|---|
| `GET /api/health` | kliens | a szerver állapota: verzió, gépnév, konfiguráció változata, Surya (`off`/`starting`/`ready`/`error`), sor |
| `GET /api/config/revision` | kliens | `{"revision": 12}` – a kliensek ezt kérdezik gyakran |
| `GET /api/config` | kliens | a teljes központi konfiguráció: `revision`, `updatedAt`, `updatedBy`, `styles`, `settings` |
| `PUT /api/config` | admin | új konfiguráció: `{"styles": […], "settings": {…}, "basedOnRevision": 12}`; ha közben más mentett: **409** |
| `POST /api/issuers/learn` | kliens | kézzel javított kibocsátónév: `{"name": "…", "taxNumber": "12345678-2-41"}` |
| `POST /api/receivers` | kliens | új vevő cég: `{"name": "…", "vatNumber": "12345678-2-41"}` → `{added, revision}`; azonos adószám: `added: false`; érvénytelen adószám: **400** |
| `POST /api/ocr/jobs` | kliens | egy oldalkép (törzs: JPEG/PNG bájtok) a Surya-sorba → **202** `{id, status: "queued", position}`; tele: **429**; nincs Surya: **503** |
| `GET /api/ocr/jobs/{id}?wait=20` | kliens | a feladat állapota; `wait` másodpercig vár, amíg változik (long polling). Kész: `status: "done"` + `result` (sorok, szavak, koordináták) |
| `DELETE /api/ocr/jobs/{id}` | kliens | a feladat törlése a sorból |
| `GET /api/ocr/queue` | admin | az összes feladat (kép és eredmény nélkül) |
| `POST /api/admin/surya/restart` | admin | a Surya újraindítása |

Példa:

```powershell
$h = @{ Authorization = "Bearer <kliens kulcs>" }
Invoke-RestMethod http://szerver:8770/api/health -Headers $h
```

## Hibaelhárítás

| Jelenség | Ok / teendő |
|---|---|
| A kliens: „A szerver nem érhető el” | fut a szolgáltatás? (szerverkezelő › Áttekintés); nyitva a port a tűzfalon? jó a cím és a port? |
| „elutasította a kulcsot” | a kliens kulcs nem egyezik (új kulcs után minden kliensen át kell írni) |
| Surya: „Hiba: …” | nézd meg a *Napló* oldalt; gyakori ok: nincs telepítve a Python „minden felhasználónak”, vagy nincs telepítve a Surya. Telepítés után: *Surya újraindítása* |
| Surya: „Indul…” sokáig | első induláskor a modellek letöltése/betöltése néhány percig tart |
| „A sor megtelt” | túl sok oldal vár; növeld a *Sor hossza* értékét, vagy várj |
| A port foglalt | *Szerver beállításai* › Port (a tűzfalszabályt a szerverkezelő átállítja), a klienseken is írd át a címet |
