; Markshot installer (Inno Setup 6). Build with tools\build_installer.cmd, which
; publishes the app and passes the version:  ISCC /DAppVersion=0.8.04 /DFileVersion=0.8.4.0 Markshot.iss
; Per-user install (no admin rights): %LOCALAPPDATA%\Programs\Markshot.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish_installer"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif

[Setup]
AppId={{7C6B9D2E-4F1A-4E3B-9C55-8B1E2A6F4D10}
AppName=Markshot
AppVersion={#AppVersion}
AppVerName=Markshot {#AppVersion}
AppPublisher=Markshot
VersionInfoVersion={#FileVersion}
VersionInfoProductName=Markshot
DefaultDirName={localappdata}\Programs\Markshot
DefaultGroupName=Markshot
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Markshot-Setup-{#AppVersion}
SetupIconFile=..\Assets\markshot.ico
UninstallDisplayIcon={app}\Markshot.exe
UninstallDisplayName=Markshot {#AppVersion}
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
LZMADictionarySize=262144
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Close a running Markshot before the files are replaced.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "chs"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "startup"; Description: "开机自动启动"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; Excludes: "*.lib,*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Markshot"; Filename: "{app}\Markshot.exe"
Name: "{autoprograms}\卸载 Markshot"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Markshot"; Filename: "{app}\Markshot.exe"; Tasks: desktopicon
Name: "{userstartup}\Markshot"; Filename: "{app}\Markshot.exe"; Tasks: startup

[Registry]
; Where this installer ran from: Markshot looks there for newer installers (设置 → 更新路径).
Root: HKCU; Subkey: "Software\Markshot"; ValueType: string; ValueName: "SetupDir"; ValueData: "{src}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Markshot"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"

[Run]
Filename: "{app}\Markshot.exe"; Description: "启动 Markshot"; Flags: nowait postinstall skipifsilent
