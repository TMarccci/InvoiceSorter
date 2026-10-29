# Invoice Sorter – felhasználói útmutató

Ez az útmutató a kliensprogram használatát írja le. A szerver telepítéséről és kezeléséről a
[SZERVER.md](SZERVER.md) szól.

- [Telepítés és frissítés](#telepítés-és-frissítés)
- [A feldolgozás lépései](#a-feldolgozás-lépései)
- [Hibák ellenőrzése és javítása](#hibák-ellenőrzése-és-javítása)
- [Dátumok](#dátumok)
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
   Egy kattintás: a program letölti a telepítőt, a Windows rendszergazdai jóváhagyást kér, a program bezárul, a
   telepítés ablak nélkül (csak egy folyamatjelzővel) lefut, és az új verzió magától elindul. A *Nem* gombbal csak
   a kiadás oldala nyílik meg. A beállítások és a sablonok megmaradnak. (A jelzés a *Beállítások › Konfiguráció*
   oldalon kikapcsolható.) Kézzel letölteni nem kell.

**Kis képernyőn:** az **F12** (vagy F11) teljes képernyőre vált – címsor és tálca nélkül –, újra megnyomva vissza.
Ha az ablak nagyobb lenne a képernyőnél, a program teljes méretben nyílik meg, és az ellenőrző képernyőn a számla
adatai görgethetők, hogy az oldal képe mindig látsszon.

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
- a hiányos számlákat **újraolvassa** (beállítástól függően először Surya OCR-rel, majd kétszeres nagyításban, a másik
  OCR-nyelvvel, sötétebben/világosabban, nagyobb kontraszttal, fekete-fehéren).

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
| *Duplikátum* | ugyanaz a lap kétszer: **Kizárás** (vagy a felesleges oldal: **Oldal törlése**); ha tényleg két számla: **Duplikátum megtartása** |
| *Nem számla? Az oldal címe: „Szállítólevél”* | **Összevonás az előzővel** / **Összevonás…**, **Kizárás**, vagy **Ez számla** |
| *Alacsony megbízhatóság* | ellenőrizd a kibocsátót és a számlaszámot (csak ha a Beállításokban hibának számít) |
| *Hiányzó dátum* / *Nem dönthető el egyértelműen* | írd be a dátumot, vagy kattints egy javaslatra a mező alatt – lásd [Dátumok](#dátumok) |
| *A fizetési határidő korábbi, mint a kiállítás dátuma* / *Valószínűtlen dátum* | nézd meg az oldalt; ha így helyes: **Dátumok elfogadva** |

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
| | **Oldal törlése** | a kijelölt oldal kikerül a számlából, és nem kerül mentésre (reklám, kétszer beszkennelt lap, oda nem való oldal); a bélyegképen a *Delete* billentyű is jó. Ha a számla egyetlen oldala, a számla is törlődik – ha csak kihagynád a mentésből, inkább a **Kizárás** kell. A törölt oldal az *Újrafelismerés* után is törölve marad; az eredeti PDF nem változik |
| | **Törölt oldalak (N)…** | csak ha van törölt oldal: a választott oldal visszakerül oda, ahonnan törölted (ha a számlák azóta újra lettek bontva, új számlaként, a helyén) |
| Számla | **Összevonás az előzővel**, **Összevonás…** | az oldalakat az előző / bármelyik másik számlához fűzi |
| | **Újraolvasás** | a kiválasztott sablonnal újraolvassa a számlát |
| | **Új sablon…** | sablon készítése a kijelölt oldalból |
| Mentés | **Kizárás** | a számla nem kerül mentésre |
| | **Adószám elfogadva** | elfogadod, bár a vevő adószámát nem sikerült igazolni |
| | **Dátumok elfogadva** | a dátumok így helyesek (szokatlan sorrend, régi dátum, vagy a számlán nem szereplő dátum is) |
| | **Duplikátum megtartása** | mindkét azonos számlát menti (a második `_2` végződést kap) |
| | **Ez számla** | a „nem számla” jelzés visszavonása |

A kézzel módosított számlák sárga sorral és ✎ jellel látszanak, és a jelentésben is szerepelnek. Az
**Újrafelismerés** a teljes fájlt újra szétbontja a jelenlegi sablonokkal (a meglévő OCR-ből; a kézi módosítások
elvesznek). Ha a vevő cég nem szerepel a beállításaid között, a program felajánlja a felvételét.

## Dátumok

Minden számlánál három dátum szerepel a számlaszám alatt: **kiállítás dátuma** (számla kelte), **teljesítés
dátuma** és **fizetési határidő**. Begépelhető mezők (nem naptár): `2026.09.02.`, `2026-09-02`, `20260902`,
`02.09.2026` is jó, a program `2026.09.02.` alakra hozza (Enter vagy a mező elhagyása rögzíti). A mező alatt kis
betűvel látszik, honnan vette a program („„Számla kelte” alatt”, „„Fizetési határidő” mellett”, „egyetlen dátum
(pénztárgépes nyugta)”, „kézzel megadva”), a lapon pedig lila keret jelöli a helyét.

**Hogyan dönt a program?**

- Egy dátum csak a **címkéje** alapján kerül a helyére: a vele egy sorban, közvetlenül előtte álló címke
  („Kiállítás dátuma: 2026.09.02.”) vagy a felette álló oszlopfejléc (a szokásos „Teljesítés dátuma | Számla kelte |
  Fizetési határidő” táblázat). Kétnyelvű („Számla kelte / Invoice date”) és két sorra tört címkéket is felismer.
- Az **időszakok** („Teljesítési időszak: 2026.08.01 – 2026.08.31”), egyenlegek, szerződés-, hűségidő-, rendelés-,
  szállítólevél- és szavatossági dátumok nem kerülnek a mezőkbe.
- **Nyugta, kézi számlatömb, készpénzes / kártyás / utánvétes fizetés:** ha a lapon csak egy dátum van, az mindhárom.
  Ha egy dátum van megcímkézve (pl. „Kelt”), a hiányzók azonosak vele.
- **Ha nem biztos, üresen hagyja** – és szól: ha két eltérő dátum ugyanazzal a címkével szerepel (pl. gyűjtőszámla
  tételenkénti teljesítéssel), ha a címkék ellentmondanak egymásnak, ha az OCR-olvasatok eltérnek, vagy ha a dátum csak
  egyetlen újraolvasásban látszik. Ilyenkor a mező alatti **javaslatok** a lapon talált dátumokat mutatják (elöl az
  adott címkéjűek, az egérmutató alatt a lapon mellettük álló szöveggel); egy kattintás átveszi. Ha a számlán
  fizetési feltétel áll („Átutalás (8 nap)”), a kiszámolt határidő („kiállítás + 8 nap”) is javaslatként jelenik meg.
- **Ellenőrzések:** hiba, ha a határidő a kiállítás előtt van, ha a kiállítás a jövőben van, ha egy dátum két évnél
  régebbi vagy egy évnél későbbi, vagy ha a dátumok között több mint egy év van (tipikus OCR-hiba: halvány „2026”
  helyett „2020”). Kisebb levonás jár a szokatlanul hosszú fizetési határidőért, a kiírt fizetési feltételnek
  ellentmondó határidőért, és az OCR-javított vagy csak újraolvasásból ismert dátumért.

A hiányzó dátum alapértelmezés szerint hiba (a számla a hibás számlák közé kerül, amíg meg nem adod); ez a
*Beállítások › Dátumok* oldalon kikapcsolható. Ugyanott bővítheted a címkéket, ha egy szállító másként írja őket. Ha
egy elrendezésnél az általános címkék tévednek, a sablonban saját dátumszabályt adhatsz meg (lásd
[Sablonok](#sablonok)).

## Mentés

Válaszd ki a célmappát, majd **Számlák mentése**.

- A hibátlan számlák neve: `{Számlaszám}_{Kibocsátó}.pdf` (a minta a Beállításokban módosítható).
- A még hibás számlák a `_Errors` almappába kerülnek (kikapcsolható).
- Meglévő fájlt soha nem ír felül, hanem sorszámot fűz a névhez.
- A számlák mellé CSV-jelentés (`_report_*.csv`) és a feldolgozás naplója (`_log_*.txt`) is kerül. A jelentésben a
  számlaszám után a három dátum (`Kiállítás dátuma`, `Teljesítés dátuma`, `Fizetési határidő`, `2026.09.02.` alakban),
  a végén pedig a forrásuk is szerepel. A fájlnévbe a dátumok nem kerülnek.
- A kézzel javított kibocsátóneveket a program megjegyzi (központi módban a szerveren), így legközelebb automatikusan
  javítja.

## Sablonok

Egy **sablon** egy számlaelrendezést ír le: miről ismerhető fel az első oldala, és hol áll rajta a kibocsátó és a
számlaszám. Megnyitás: **Sablonok…** a fejlécben, vagy **Új sablon…** az ellenőrző képernyőn.

| Rész | Jelentés |
|---|---|
| **1. Az első oldal felismerése** | szövegek, amelyeknek mind szerepelniük kell az oldalon; egy szöveg területre is korlátozható |
| **2. Kibocsátó** | *Állandó szöveg* (egy szállítóhoz tartozó sablon) vagy az oldalról olvasva. Az állandó nevet a program csak akkor adja meg, ha a lap is igazolja: a név szerepel rajta, vagy a szállító adószáma ehhez a céghez tartozik. Ha nem (egy másik szállító lapja tévesen illeszkedett), a kibocsátó a lapon talált adószám cége, vagy üresen marad – és szól, hogy lehet, hogy rossz sablon illeszkedett |
| **3. Számlaszám** | általában *Címke mellett*: megkeresi a címkét (pl. `Számlaszám`) és a mellette/alatta álló értéket olvassa |
| **4. Dátumok** (nem kötelező) | saját szabály a kiállítás / teljesítés dátumához vagy a határidőhöz, ha az általános címkék ennél az elrendezésnél tévednek (pl. „Dátum/Teljesítés ideje”); a szabály által olvasott szöveg első dátuma számít. Rajzolható is (*Kelte / Teljesítés / Határidő*) |

Használat: válassz oldalt (a jelenlegi fájlból vagy *Minta PDF megnyitása…*), jelöld ki, mit rajzolsz
(*Azonosító szöveg / Kibocsátó / Számlaszám*), és húzz keretet az oldalon. Az eredmény azonnal látszik alul; a
*Próba az összes oldalon* megmutatja, mely oldalakon ismeri fel a sablon.

**Tipp:** inkább a **számlázóprogramot** ismertesd fel (pl. a láblécét: „A bizonylatot a Számlázz.hu készítette”), és
a kibocsátót olvasd az oldalról – így egy sablon minden cégre jó, amely azt a programot használja.

Beépített sablonok: Számlázz.hu, Billingo, NAV Online Számlázó, pénztárgépes nyugták, NaturaSoft, Okos-Ügyvitel,
FR-SOFT, Vectory, JUTA-soft, MVM LIBRA, SAP Business One, Microsoft Dynamics 365 Business Central, Diamond Stúdió,
InfoCentrum, HUGIR, RLB-60, webshop-számlák, előnyomott számlatömb, valamint néhány nagy szolgáltató saját rendszere
(METRO, Magyar Telekom, MOL üzemanyagkártya, FleetCor, ZNET, ViDaNet, Roto Elzett, HŐSZIG, Alza.hu). Amit egyik sem ismer fel, azt az **Általános
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
| **Dátumok** | a kiállítás / teljesítés / határidő címkéi, a kizárt („nem számladátum”) címkék, az egydátumos fizetési módok; hiba-e a hiányzó dátum |
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

**Segítene a nagyobb DPI?** A program oldalán nem: a szkennelt PDF-ekben a kép 200 DPI-s, ennél nagyobb felbontásban
csak felnagyítani lehet, új részlet nem jelenik meg. 540 számlán kipróbáltuk: az újraolvasás 300, 400 és 600 DPI-s
újrarajzolással összességében ugyanannyit olvasott, mint a mostani kétszeres nagyítás (a fekete-fehér nyugtáknál
rosszabbat), ezért maradt a mostani. A sötétítés, a kontraszt, a fekete-fehér kép, a másik OCR-nyelv és főleg a Surya
sokkal többet hoz. Ami valóban segít, az a **szkenner** beállítása: 300 DPI és szürkeárnyalatos (nem fekete-fehér)
szkennelés – főleg a halvány hőpapíros nyugtáknál és az apró betűs számláknál.

**Rossz helyen vágta szét a számlákat.** Használd a *Szétvágás itt* / *Összevonás* gombokat. Ha egy számlázóprogram
rendszeresen gondot okoz, készíts hozzá sablont – az *Újrafelismerés* után az egész fájlra érvényes lesz.

**Nem kapcsolódik a szerverhez.** Ellenőrizd a címet és a portot, a kliens kulcsot, és hogy a szerveren fut-e a
szolgáltatás, illetve nyitva van-e a port a tűzfalon (szerverkezelő › Áttekintés).
