; Invoice Sorter – kliens telepítő (Inno Setup 6+)
; Fordítás:  iscc /DAppVersion=1.2.3 InvoiceSorter.iss   (vagy: pwsh scripts/build-client.ps1 -Version 1.2.3)
; A kiadott (single-file) exe helye: ..\..\publish\client\InvoiceSorter.exe  (felülírható: /DClientExe=<útvonal>)

#ifndef AppVersion
  #define AppVersion "1.4.0"
#endif
#ifndef ClientExe
  #define ClientExe "..\..\publish\client\InvoiceSorter.exe"
#endif
#ifndef IconFile
  #define IconFile "..\..\assets\invoicesorter.ico"
#endif

#define AppName "Invoice Sorter"
#define Publisher "TMarccci"
#define AppUrl "https://github.com/TMarccci/InvoiceSorter"
#define AppId "{{AB1F5DC0-3869-48DB-987F-1621495EB72B}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} telepítő

; Minden felhasználónak: C:\Program Files\TMarccci\InvoiceSorter
PrivilegesRequired=admin
DefaultDirName={commonpf64}\TMarccci\InvoiceSorter
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
Uninstallable=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\InvoiceSorter.exe
SetupIconFile={#IconFile}
OutputDir=Output
OutputBaseFilename=InvoiceSorter-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
LicenseFile=..\..\LICENSE

[Languages]
Name: "hungarian"; MessagesFile: "compiler:Languages\Hungarian.isl"

[Tasks]
Name: "desktopicon"; Description: "Parancsikon az asztalon"; GroupDescription: "További parancsikonok:"
Name: "pdfopenwith"; Description: "Megjelenés a PDF-fájlok „Társítás” menüjében"; GroupDescription: "Integráció:"; Flags: unchecked

[Files]
Source: "{#ClientExe}"; DestDir: "{app}"; DestName: "InvoiceSorter.exe"; Flags: ignoreversion
Source: "{#IconFile}"; DestDir: "{app}"; DestName: "invoicesorter.ico"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\..\docs\FELHASZNALOI_UTMUTATO.md"; DestDir: "{app}"; DestName: "Felhasznaloi_utmutato.md"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\InvoiceSorter.exe"; IconFilename: "{app}\invoicesorter.ico"; Comment: "Szkennelt számlák szétbontása, olvasása és átnevezése"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\InvoiceSorter.exe"; IconFilename: "{app}\invoicesorter.ico"; Tasks: desktopicon

[Registry]
; "Társítás" (Open with) a PDF-ekhez – a PDF alapértelmezett programját nem változtatja meg.
Root: HKLM; Subkey: "Software\Classes\Applications\InvoiceSorter.exe\shell\open\command"; ValueType: string; ValueData: """{app}\InvoiceSorter.exe"" ""%1"""; Flags: uninsdeletekey; Tasks: pdfopenwith
Root: HKLM; Subkey: "Software\Classes\Applications\InvoiceSorter.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: pdfopenwith
Root: HKLM; Subkey: "Software\Classes\.pdf\OpenWithList\InvoiceSorter.exe"; Flags: uninsdeletekey; Tasks: pdfopenwith

[Run]
Filename: "{app}\InvoiceSorter.exe"; Description: "{#AppName} indítása"; Flags: nowait postinstall skipifsilent
; Frissítés a programból (csendes telepítés): a régi példány bezárult, az új magától indul – a bejelentkezett felhasználóként.
Filename: "{app}\InvoiceSorter.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

; A felhasználók adatai (%APPDATA%\InvoiceSorter: beállítások, sablonok; %LOCALAPPDATA%\InvoiceSorter: napló, Surya)
; eltávolításkor megmaradnak – egy újratelepítés ugyanott folytatja.
