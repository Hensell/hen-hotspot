; Compile through Build-Installer.ps1 so only a fresh self-contained publish is packaged.
#if VER < EncodeVer(6, 7, 3)
  #error Inno Setup 6.7.3 or later is required
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by Build-Installer.ps1
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir must be supplied by Build-Installer.ps1
#endif

[Setup]
; Keep this ID stable across releases so Windows recognizes upgrades.
AppId={{B3F42B96-DF89-41E8-A72F-6DB281EAF611}
AppName=Hen Hotspot
AppVersion={#AppVersion}
AppPublisher=Hensell
AppPublisherURL=https://hensell.dev/
AppSupportURL=https://github.com/Hensell/hen-hotspot/issues
AppUpdatesURL=https://github.com/Hensell/hen-hotspot/releases
DefaultDirName={localappdata}\Programs\Hen Hotspot
DefaultGroupName=Hen Hotspot
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
AppMutex=Local\HenHotspot.MainWindow
SetupMutex=Local\HenHotspot.Setup
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\HenHotspot.exe
SetupIconFile=..\src\Assets\Brand\hen-hotspot.ico
WizardStyle=modern
WizardSmallImageFile=..\src\Assets\Brand\hen-mark-128.png
Compression=lzma2
SolidCompression=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=HenHotspot-Setup-{#AppVersion}-win-x64
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Hensell
VersionInfoDescription=Hen Hotspot Installer
VersionInfoCopyright=Copyright (c) 2026 Hensell

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "\WinDivert.dll,\WinDivert64.sys"; Flags: ignoreversion recursesubdirs createallsubdirs
; Keep identical native binaries in place if Windows still holds the driver file.
; Different contents with the same version are replaced, newer versions are preserved.
Source: "{#PublishDir}\WinDivert.dll"; DestDir: "{app}"; Flags: replacesameversion
Source: "{#PublishDir}\WinDivert64.sys"; DestDir: "{app}"; Flags: replacesameversion

[Icons]
Name: "{autoprograms}\Hen Hotspot"; Filename: "{app}\HenHotspot.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Hen Hotspot"; Filename: "{app}\HenHotspot.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\HenHotspot.exe"; Description: "{cm:LaunchProgram,Hen Hotspot}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[CustomMessages]
english.CloseHen=Close Hen Hotspot and wait for its filtering components to stop, then try again.
spanish.CloseHen=Cierra Hen Hotspot y espera a que se detengan sus componentes de filtrado. Luego vuelve a intentarlo.
brazilianportuguese.CloseHen=Feche o Hen Hotspot e aguarde os componentes de filtragem pararem. Depois, tente novamente.
english.ProcessCheckFailed=Setup could not check whether Hen Hotspot is running. Restart Windows and try again.
spanish.ProcessCheckFailed=El instalador no pudo comprobar si Hen Hotspot está abierto. Reinicia Windows y vuelve a intentarlo.
brazilianportuguese.ProcessCheckFailed=O instalador não conseguiu verificar se o Hen Hotspot está aberto. Reinicie o Windows e tente novamente.
english.NewerVersion=A newer version of Hen Hotspot is already installed in this folder. Use the newer installer.
spanish.NewerVersion=Ya hay una versión más reciente de Hen Hotspot en esta carpeta. Usa el instalador más reciente.
brazilianportuguese.NewerVersion=Uma versão mais recente do Hen Hotspot já está instalada nesta pasta. Use o instalador mais recente.

[Code]
function CheckRunningProcesses: String;
var
    Locator, Services, Processes: Variant;
begin
    Result := '';
    try
        { Helpers use the same executable but do not own the main-window mutex. }
        Locator := CreateOleObject('WbemScripting.SWbemLocator');
        Services := Locator.ConnectServer('', 'root\CIMV2');
        Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''HenHotspot.exe''');
        if Processes.Count > 0 then
            Result := CustomMessage('CloseHen');
    except
        Log('Process check failed: ' + GetExceptionMessage);
        Result := CustomMessage('ProcessCheckFailed');
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
    InstalledVersion, SetupVersion: Int64;
begin
    Result := CheckRunningProcesses;
    if Result <> '' then
        Exit;
    if GetPackedVersion(ExpandConstant('{app}\HenHotspot.exe'), InstalledVersion) then
        if StrToVersion('{#AppVersion}', SetupVersion) then
            if ComparePackedVersion(InstalledVersion, SetupVersion) > 0 then
                Result := CustomMessage('NewerVersion');
end;

function InitializeUninstall: Boolean;
var
    ErrorMessage: String;
begin
    ErrorMessage := CheckRunningProcesses;
    Result := ErrorMessage = '';
    if not Result then
        SuppressibleMsgBox(ErrorMessage, mbError, MB_OK, IDOK);
end;

{ User settings and history are outside the installation directory and are preserved. }
