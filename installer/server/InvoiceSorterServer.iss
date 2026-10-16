; Invoice Sorter Server – szerver telepítő (Inno Setup 6+)
; Fordítás:  iscc /DAppVersion=1.2.3 InvoiceSorterServer.iss   (vagy: pwsh scripts/build-server.ps1 -Version 1.2.3)
; A kiadott fájlok helye: ..\..\publish\server\ (InvoiceSorterServer.exe + InvoiceSorterServerManager.exe)
;
; Telepít:   C:\Program Files\TMarccci\InvoiceSorter Server
; Szolgáltatás: "InvoiceSorterServer" (automatikus indulás, LocalSystem), tűzfalszabály a 8770-es TCP portra
; Adatok:    C:\ProgramData\TMarccci\InvoiceSorter\Server (server.json, central.json, backups, Logs, surya) –
;            eltávolításkor megmaradnak; csak a rendszergazdák és a SYSTEM érik el (a kulcsok miatt).

#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#ifndef ServerDir
  #define ServerDir "..\..\publish\server"
#endif
#ifndef IconFile
  #define IconFile "..\..\assets\invoicesorter-server.ico"
#endif

#define AppName "Invoice Sorter Server"
#define Publisher "TMarccci"
#define AppUrl "https://github.com/TMarccci/InvoiceSorter"
#define AppId "{{1EF8548C-13CC-4583-838A-042869B31456}"
#define ServiceName "InvoiceSorterServer"
#define FirewallRule "Invoice Sorter Server"
#define DefaultPort "8770"

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

PrivilegesRequired=admin
DefaultDirName={commonpf64}\TMarccci\InvoiceSorter Server
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
Uninstallable=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\InvoiceSorterServerManager.exe
SetupIconFile={#IconFile}
OutputDir=Output
OutputBaseFilename=InvoiceSorter-Server-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
SetupLogging=yes
LicenseFile=..\..\LICENSE

[Languages]
Name: "hungarian"; MessagesFile: "compiler:Languages\Hungarian.isl"

[Tasks]
Name: "firewall"; Description: "Tűzfalszabály a {#DefaultPort}-es TCP portra (a kliensek elérjék a szervert)"; GroupDescription: "Hálózat:"
Name: "desktopicon"; Description: "A szerverkezelő parancsikonja az asztalon"; GroupDescription: "További parancsikonok:"; Flags: unchecked

[Dirs]
Name: "{commonappdata}\TMarccci\InvoiceSorter\Server"; Flags: uninsneveruninstall

[Files]
Source: "{#ServerDir}\InvoiceSorterServer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ServerDir}\InvoiceSorterServerManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#IconFile}"; DestDir: "{app}"; DestName: "invoicesorter-server.ico"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\..\docs\SZERVER.md"; DestDir: "{app}"; DestName: "Szerver_utmutato.md"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Invoice Sorter szerverkezelő"; Filename: "{app}\InvoiceSorterServerManager.exe"; IconFilename: "{app}\invoicesorter-server.ico"; Comment: "Az Invoice Sorter szerver kezelése"
Name: "{autodesktop}\Invoice Sorter szerverkezelő"; Filename: "{app}\InvoiceSorterServerManager.exe"; IconFilename: "{app}\invoicesorter-server.ico"; Tasks: desktopicon

[Run]
; Az adatmappát csak a SYSTEM és a rendszergazdák érik el (server.json-ban vannak a kulcsok).
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\TMarccci\InvoiceSorter\Server"" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F"; Flags: runhidden waituntilterminated; StatusMsg: "Az adatmappa jogosultságainak beállítása…"
; Szolgáltatás: létrehozás (frissítéskor már létezik – a hibát figyelmen kívül hagyja), majd a beállítások.
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\InvoiceSorterServer.exe\"""" start= delayed-auto DisplayName= ""{#AppName}"""; Flags: runhidden waituntilterminated; StatusMsg: "A szolgáltatás létrehozása…"
Filename: "{sys}\sc.exe"; Parameters: "config {#ServiceName} binPath= ""\""{app}\InvoiceSorterServer.exe\"""" start= delayed-auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Invoice Sorter: központi sablonok és beállítások a klienseknek, közös Surya OCR-sor."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/10000/restart/30000/restart/60000"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow protocol=TCP localport={code:ServerPort} program=""{app}\InvoiceSorterServer.exe"""; Flags: runhidden waituntilterminated; Tasks: firewall; StatusMsg: "Tűzfalszabály…"
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "A szolgáltatás indítása…"
Filename: "{app}\InvoiceSorterServerManager.exe"; Description: "A szerverkezelő megnyitása (kliens kulcs, Surya telepítése)"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\timeout.exe"; Parameters: "/t 5 /nobreak"; Flags: runhidden waituntilterminated; RunOnceId: "WaitService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFirewallRule"

[Code]
{ The port in server.json (an update keeps a port changed in the server manager), default 8770. }
function ServerPort(Param: String): String;
var
  Lines: TArrayOfString;
  I, P: Integer;
  S: String;
begin
  Result := '{#DefaultPort}';
  if LoadStringsFromFile(ExpandConstant('{commonappdata}\TMarccci\InvoiceSorter\Server\server.json'), Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      S := Trim(Lines[I]);
      P := Pos('"Port":', S);
      if P = 1 then
      begin
        S := Trim(Copy(S, 8, Length(S)));
        if (Length(S) > 0) and (S[Length(S)] = ',') then S := Copy(S, 1, Length(S) - 1);
        if StrToIntDef(S, 0) > 0 then Result := S;
      end;
    end;
end;

{ An update replaces the exe of a running service: stop it first. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode = 0 then Sleep(4000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM InvoiceSorterServer.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM InvoiceSorterServerManager.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
