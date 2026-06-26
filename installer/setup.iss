; Ollama - Inno Setup installer script
; Build with: ISCC.exe /DAppVersion=1.0.0 installer\setup.iss
; (publish.ps1 -MakeInstaller runs this for you)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Ollama"
#define AppExeName "Ollama.exe"
#define AppPublisher "Ollama-2.0"

[Setup]
AppId={{8A7B9C1D-2E3F-4A5B-9C1D-0E1F2A3B4C5D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://ollama.com
AppSupportURL=https://github.com/ollama/ollama
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=OllamaSetup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
SetupIconFile=..\src\Ollama2\app.ico
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startup"; Description: "Start &automatically when Windows starts"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\publish\Ollama.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\*.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{autostartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Kill the running instance (it lives in the tray) before uninstalling.
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExeName} /F /T"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Ollama2\web"
