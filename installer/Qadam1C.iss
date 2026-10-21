[Setup]
AppId={{7B95E12C-E240-4C2A-9298-75585A1370E3}
AppName=Qadam1C
AppVersion=0.1.3
AppPublisher=Qadam1C
DefaultDirName={localappdata}\Programs\Qadam1C
DefaultGroupName=Qadam1C
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\release
OutputBaseFilename=Qadam1C-Setup-0.1.3-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Qadam1C.exe
SetupIconFile=..\assets\qadam1c.ico
CloseApplications=yes
RestartApplications=no
SetupMutex=Local\Qadam1C.Setup

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"
Name: "autostart"; Description: "Запускать Qadam1C вместе с Windows"

[Files]
Source: "..\release\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Qadam1C"; Filename: "{app}\Qadam1C.exe"
Name: "{autodesktop}\Qadam1C"; Filename: "{app}\Qadam1C.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Qadam1C"; ValueData: """{app}\Qadam1C.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Qadam1C.exe"; Description: "Запустить Qadam1C"; Flags: nowait postinstall skipifsilent
