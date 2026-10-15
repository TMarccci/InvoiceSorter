# Invoice Sorter – felhasználói útmutató

Ez az útmutató a kliensprogram használatát írja le. A szerver telepítéséről és kezeléséről a
[SZERVER.md](SZERVER.md) szól.

- [Telepítés és frissítés](#telepítés-és-frissítés)
- [A feldolgozás lépései](#a-feldolgozás-lépései)
- [Hibák ellenőrzése és javítása](#hibák-ellenőrzése-és-javítása)
- [Mentés](#mentés)
- [Sablonok](#sablonok)
- [Beállítások](#beállítások)
- [Helyi és központi konfiguráció](#helyi-és-központi-konfiguráció)
- [Surya OCR](#surya-ocr)
- [Megbízhatósági pontszám](#megbízhatósági-pontszám)
- [Fájlok és napló](#fájlok-és-napló)
- [Gyakori kérdések](#gyakori-kérdések)

## Telepítés és frissítés

1. Töltsd le az `InvoiceSorter-Setup-x.y.z.exe` fájlt a
   [kiadások oldaláról](https://github.com/TMarccci/InvoiceSorter/releases/latest).
2. Futtasd (rendszergazdai jóváhagyást kér). A program a `C:\Program Files\TMarccci\InvoiceSorter` mappába kerül,
   és megjelenik a Start menüben. Igény szerint asztali ikont és a PDF-ek *Társítás* menüjébe bejegyzést is készít.
3. **Frissítés:** ha új verzió jelenik meg, a program fejlécében egy lila **„Új verzió érhető el”** gomb jelenik meg.
   Erre kattintva a program letölti és elindítja az új telepítőt, majd bezárul (a *Nem* gombbal csak a kiadás
   oldala nyílik meg). A beállítások és a sablonok megmaradnak. (A jelzés a *Beállítások › Konfiguráció* oldalon
   kikapcsolható.)

Az eltávolítás (*Gépház › Alkalmazások*) a felhasználói adatokat (beállítások, sablonok, napló) nem törli.

## A feldolgozás lépései

A program egy ötlépéses varázsló; a fejléc alatti sáv mutatja, hol tartasz.

### 1. Forrásfájl kiválasztása

Válaszd ki (*Tallózás…*) vagy húzd az ablakra a szkenner által készített PDF-et. A **Vevő cég** mezőben megadhatod,
melyik cégnek szólnak a számlák; az *Automatikus felismerés* az oldalakon lévő adószámok alapján választ.

### 2–3. Számlákra bontás, adatok olvasása és ellenőrzés

A *Feldolgozás indítása* után a program:

- minden oldalt beolvas (Windows OCR); az üres oldalakat elhagyja, az elforgatottakat kiegyenesíti;
- új számlát kezd, ha egy oldalt felismer egy **sablon**, ha szerepel rajta a vevő cég, vagy ha számlacím áll rajta
  („Számla”, „Egyszerűsített számla”, „Készpénzfizetési számla” …);
- a többi oldalt (reklámos hátoldal, folytatólap, ÁSZF) az előző számlához csatolja; az egymás utáni azonos
  számlaszámú számlákat összevonja;
- kiolvassa a kibocsátót és a számlaszámot, megkeresi a vevő adószámát;
- a hiányos számlákat **újraolvassa** (nagyítva, a másik OCR-nyelvvel, világosabban/sötétebben, fekete-fehéren,
  illetve beállítástól függően Surya OCR-rel).

Közben látod az éppen olvasott oldalt a megtalált adatok kereteivel, az eltelt és a hátralévő időt. Ha a Surya a
szerveren fut, és más gépek oldalai is várnak, a sorban elfoglalt hely is megjelenik („Sorban áll a szerveren: 3. hely”).

### 4. Hibák ellenőrzése

Lásd a [következő fejezetet](#hibák-ellenőrzése-és-javítása).

### 5. Mentés

Lásd a [Mentés](#mentés) fejezetet.

## Hibák ellenőrzése és javítása

A bal oldali lista minden számlát mutat: oldalak, sablon, kibocsátó, számlaszám, vevő adószáma, megbízhatósági pont
és a problémák. A **Csak hibások** / **Csak alacsony pontszám** jelölőkkel szűrhetsz, a **Következő hiba** gomb a
következő hibás számlára ugrik.

Lehetséges hibák:

| Hiba | Teendő |
|---|---|
| *Kibocsátó nem található* / *Számlaszám nem található* | írd be, vagy kattints egy javaslatra a mező alatt |
| *A vevő adószáma nem található / hibás* | nézd meg az oldalt; ha rendben van: **Adószám elfogadva** |
| *Duplikátum* | ugyanaz a lap kétszer: **Kizárás**; ha tényleg két számla: **Duplikátum megtartása** |
| *Nem számla? Az oldal címe: „Szállítólevél”* | **Összevonás az előzővel** / **Összevonás…**, **Kizárás**, vagy **Ez számla** |
| *Alacsony megbízhatóság* | ellenőrizd a kibocsátót és a számlaszámot (csak ha a Beállításokban hibának számít) |

### Javaslatok

A kibocsátó és a számlaszám alatt kis „chipek” mutatják, milyen más értékeket talált a program (a sablon szabálya,
az általános szabályok, a címke melletti szöveg, az újraolvasások, illetve a kibocsátónál az adószám alapján ismert
név). Egy kattintással átveheted őket; a forrás az egérmutató alatt látszik.

### Gombok

| Csoport | Gomb | Mit csinál |
|---|---|---|
| Oldalak | **Szétvágás itt** | a kijelölt oldaltól új számla kezdődik |
| | **Oldal fel / le** | az oldal sorrendjének módosítása (a bélyegképek húzásával is megy) |
| | **OCR-szöveg** | az oldalról kiolvasott teljes szöveg, olvasatonként külön fülön |
| | **Forgatás balra / jobbra** | elforgatja és újraolvassa az oldalt; elforgatva menti |
| Számla | **Összevonás az előzővel**, **Összevonás…** | az oldalakat az előző / bármelyik másik számlához fűzi |
| | **Újraolvasás** | a kiválasztott sablonnal újraolvassa a számlát |
| | **Új sablon…** | sablon készítése a kijelölt oldalból |
| Mentés | **Kizárás** | a számla nem kerül mentésre |
| | **Adószám elfogadva** | elfogadod, bár a vevő adószámát nem sikerült igazolni |
| | **Duplikátum megtartása** | mindkét azonos számlát menti (a második `_2` végződést kap) |
| | **Ez számla** | a „nem számla” jelzés visszavonása |

A kézzel módosított számlák sárga sorral és ✎ jellel látszanak, és a jelentésben is szerepelnek. Az
**Újrafelismerés** a teljes fájlt újra szétbontja a jelenlegi sablonokkal (a meglévő OCR-ből; a kézi módosítások
elvesznek). Ha a vevő cég nem szerepel a beállításaid között, a program felajánlja a felvételét.

## Mentés

Válaszd ki a célmappát, majd **Számlák mentése**.

- A hibátlan számlák neve: `{Számlaszám}_{Kibocsátó}.pdf` (a minta a Beállításokban módosítható).
- A még hibás számlák a `_Errors` almappába kerülnek (kikapcsolható).
- Meglévő fájlt soha nem ír felül, hanem sorszámot fűz a névhez.
- A számlák mellé CSV-jelentés (`_report_*.csv`) és a feldolgozás naplója (`_log_*.txt`) is kerül.
- A kézzel javított kibocsátóneveket a program megjegyzi (központi módban a szerveren), így legközelebb automatikusan
  javítja.

## Sablonok

Egy **sablon** egy számlaelrendezést ír le: miről ismerhető fel az első oldala, és hol áll rajta a kibocsátó és a
számlaszám. Megnyitás: **Sablonok…** a fejlécben, vagy **Új sablon…** az ellenőrző képernyőn.

| Rész | Jelentés |
|---|---|
| **1. Az első oldal felismerése** | szövegek, amelyeknek mind szerepelniük kell az oldalon; egy szöveg területre is korlátozható |
| **2. Kibocsátó** | *Állandó szöveg* (egy szállítóhoz tartozó sablon) vagy az oldalról olvasva |
| **3. Számlaszám** | általában *Címke mellett*: megkeresi a címkét (pl. `Számlaszám`) és a mellette/alatta álló értéket olvassa |

Használat: válassz oldalt (a jelenlegi fájlból vagy *Minta PDF megnyitása…*), jelöld ki, mit rajzolsz
(*Azonosító szöveg / Kibocsátó / Számlaszám*), és húzz keretet az oldalon. Az eredmény azonnal látszik alul; a
*Próba az összes oldalon* megmutatja, mely oldalakon ismeri fel a sablon.

**Tipp:** inkább a **számlázóprogramot** ismertesd fel (pl. a láblécét: „A bizonylatot a Számlázz.hu készítette”), és
a kibocsátót olvasd az oldalról – így egy sablon minden cégre jó, amely azt a programot használja.

Beépített sablonok: Számlázz.hu, Billingo, NAV Online Számlázó, pénztárgépes nyugták, NaturaSoft, Okos-Ügyvitel,
FR-SOFT, Vectory, JUTA-soft, MVM LIBRA, SAP Business One, Microsoft Dynamics 365 Business Central, Diamond Stúdió,
InfoCentrum, HUGIR, RLB-60, webshop-számlák, előnyomott számlatömb, valamint néhány nagy szolgáltató saját rendszere
(METRO, Magyar Telekom, MOL üzemanyagkártya, FleetCor, ZNET, ViDaNet). Amit egyik sem ismer fel, azt az **Általános
(nincs sablon)** szabályai olvassák. A *Beépített sablonok visszaállítása…* a hiányzókat pótolja.

### Szűrők (haladó)

- Azonosító szövegben: `A | B | C` = bármelyik elég; `re:<regex>` = reguláris kifejezés; `narrow:` előtag = csak
  keskeny, blokk alakú oldalon számít.
- A címke is lehet több: `Számla sorszám | Számla száma`.
- A mező *Regex*-e kiválasztja az értéket (az első csoport vagy a `value` nevű csoport). Az *OCR-számjegyek
  javítása* mellett a `digits` nevű csoportban az O/I/l/S/B számjeggyé alakul. A *Kimeneti formátum* a csoportokból
  rakja össze az értéket (pl. `$1/$2`).

## Beállítások

A **Beállítások…** ablak oldalai:

| Oldal | Tartalom |
|---|---|
| **Konfiguráció** | helyi vagy központi konfiguráció, szervercím, kliens kulcs, frissítésjelzés |
| **Vevő cégek** | a vevő cégek neve és adószáma (az *Ellenőrzés* oszlop azonnal jelzi a hibás adószámot); nyugtáknál a törzsszám elfogadása |
| **Szétbontás** | üres oldalak, automatikus forgatás, összevonás |
| **Oldaltípusok** | számlacímek, folytatólapok, mellékletcímek, „nem számla” címek |
| **Windows OCR** | nyelv, felbontás, hibatűrés |
| **Surya OCR** | mikor és hol fusson a Surya |
| **Kibocsátók** | névjavítás, cégnév lekérdezése adószám alapján, ismert kibocsátók listája |
| **Megbízhatóság** | az alacsony pontszám küszöbe; számítson-e hibának |
| **Mentés** | fájlnévminta (`{InvoiceNumber}`, `{Issuer}`, `{Style}`, `{Number}`), hibás számlák almappája |

**Tipp:** a jobb ékezetfelismeréshez telepítsd a magyar OCR-nyelvet (*Gépház › Idő és nyelv › Nyelv › Magyar
hozzáadása*, „Optikai karakterfelismerés”), majd a *Windows OCR* oldalon válaszd a `hu-HU`-t.

### Ismert kibocsátók

Helyesen írt nevek, soronként egy, adószámmal is: `Minta Szolgáltató Kft. | 12345676-2-41`. Az oldalról olvasott,
ezekhez hasonló nevet a program kicseréli; ha a név olvashatatlan, az adószám alapján ismeri fel a kibocsátót.

### Cégnév adószám alapján

Bekapcsolva (alapértelmezés) a program a szállító új adószámaihoz egyszer lekérdezi a hivatalos cégnevet az EU
**VIES** nyilvántartásából (csak a 8 jegyű adószám megy ki), és a gépen tárolja. A rosszul olvasott nevet (pl. az
eladó bankja vagy a vevő) ezzel cseréli; az ismert kibocsátók listája elsőbbséget élvez.

## Helyi és központi konfiguráció

*Beállítások › Konfiguráció*:

- **Helyi konfiguráció** – a sablonokat és a beállításokat ezen a gépen tárolod és szerkeszted.
- **Központilag kezelt konfiguráció** – a sablonok és a beállítások az Invoice Sorter szerverről jönnek. Add meg a
  szerver címét (pl. `http://szerver:8770`) és a **kliens kulcsot** (mindkettőt a szerverkezelő *Áttekintés* oldala
  mutatja), majd **Kapcsolat tesztelése**.

Központi módban:

- a program induláskor, 5 percenként és minden feldolgozás előtt megnézi, van-e újabb konfiguráció, és automatikusan
  átveszi; a fejlécben a **„Központi konfiguráció · 12. változat”** felirat látszik;
- a sablonok és a beállítások csak megtekinthetők (szerkeszteni a szerverkezelőben lehet); a sablonokat ki is
  próbálhatod;
- **új vevő céget** felvehetsz (*Beállítások › Vevő cégek*, vagy az ellenőrző képernyő ajánlatával): a szerverre
  kerül, és minden gép megkapja. Meglévő cég módosítása vagy törlése a szerverkezelőben lehetséges;
- a kézzel javított kibocsátónevek a szerverre kerülnek, így minden gép megtanulja őket;
- a Surya OCR a szerveren fut, sorban állással;
- ha a szerver nem érhető el, a legutóbb letöltött konfigurációval dolgozik (a fejléc felirata narancssárga).

A gépfüggő beállítások (utolsó mappák, helyi Surya) mindig a gépen maradnak. Visszaváltáskor a korábbi helyi
sablonok és beállítások újra érvényesek.

## Surya OCR

A [Surya](https://github.com/datalab-to/surya) MI-alapú szövegfelismerő: a halvány és apró betűket sokkal jobban
olvassa, mint a Windows OCR. *Beállítások › Surya OCR*:

- **Kikapcsolva** – csak Windows OCR (leggyorsabb);
- **Csak a bizonytalan számlákhoz** – a problémás oldalakat a Surya újraolvassa (ajánlott);
- **Minden oldal** – a Surya minden oldalt elolvas (legpontosabb, leglassabb).

Hol fusson (helyi konfigurációnál):

- **Ezen a gépen** – a *Surya telepítése…* létrehozza a program saját Python-környezetét
  (`%LOCALAPPDATA%\InvoiceSorter\surya`; Python 3.10+ kell, ~5 GB, 10–30 perc). NVIDIA videokártyával gyors.
- **Szerveren** – az Invoice Sorter szerver Surya-ját használja (cím + kliens kulcs).

Központi módban a Surya mindig a szerveren fut. A *Surya tesztelése* ellenőrzi a működést. Ha a Surya nem érhető el,
a feldolgozás Windows OCR-rel folytatódik, és a napló jelzi az okát.

## Megbízhatósági pontszám

Minden számla 0–100 pontot kap: mennyire biztos a program a kibocsátóban, a számlaszámban, a vevő adószámában és a
szétbontásban. 100-ról indul, és minden kétség pontot von le (pl. általános sablon −20, nem ismert kibocsátó −8/−12,
csak újraolvasással talált számlaszám −8, egymásnak ellentmondó olvasatok −10…−20). A hibás számlák 60 alatt
maradnak; a kézzel beírt értékek megerősítettnek számítanak. Színek: zöld 85-től, sárga 60–84, piros 60 alatt. A
levonások az ellenőrző képernyőn és a CSV-jelentésben is szerepelnek.

## Fájlok és napló

| Mi | Hol |
|---|---|
| Program | `C:\Program Files\TMarccci\InvoiceSorter` |
| Sablonok, beállítások (helyi mód) | `%APPDATA%\InvoiceSorter\styles.json`, `settings.json` |
| A központi konfiguráció helyi másolata | `%APPDATA%\InvoiceSorter\central-cache.json` |
| Adószám alapján lekérdezett cégnevek | `%APPDATA%\InvoiceSorter\company-registry.json` |
| Napi naplófájlok | `%LOCALAPPDATA%\InvoiceSorter\Logs\` |
| Egy feldolgozás oldalképei (2 nap után törlődnek) | `%LOCALAPPDATA%\InvoiceSorter\Work\` |
| Helyi Surya-környezet | `%LOCALAPPDATA%\InvoiceSorter\surya\` |

Az ablak alján lévő **Napló** panel élőben mutatja ugyanezt (*Részletes (hibakereső) üzenetek* az oldalankénti
részletekhez).

## Gyakori kérdések

**A kézzel írt számlákat miért nem olvassa?** Az OCR a kézírást nem ismeri fel. A program a nyomtatott címük alapján
szétbontja őket, és a hibás számlák közé teszi – a kibocsátót és a számlaszámot be kell írni.

**Egy halvány nyugta adatai nem olvashatók.** Kapcsold be a Surya OCR-t (*Csak a bizonytalan számlákhoz*), vagy írd be
kézzel.

**Rossz helyen vágta szét a számlákat.** Használd a *Szétvágás itt* / *Összevonás* gombokat. Ha egy számlázóprogram
rendszeresen gondot okoz, készíts hozzá sablont – az *Újrafelismerés* után az egész fájlra érvényes lesz.

**Nem kapcsolódik a szerverhez.** Ellenőrizd a címet és a portot, a kliens kulcsot, és hogy a szerveren fut-e a
szolgáltatás, illetve nyitva van-e a port a tűzfalon (szerverkezelő › Áttekintés).
