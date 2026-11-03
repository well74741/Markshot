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
AppPublisher=well74741
AppPublisherURL=https://github.com/well74741/Markshot
AppCopyright=Copyright © 2026 well74741
VersionInfoCopyright=Copyright © 2026 well74741
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
; The wizard follows Windows' light / dark setting (Markshot itself is dark by default).
WizardStyle=modern dynamic
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
; 打开方式: Markshot is offered for pictures (Markshot refreshes this on every start; removed on uninstall).
Root: HKCU; Subkey: "Software\Classes\Markshot.Image"; ValueType: string; ValueName: ""; ValueData: "Markshot 图片"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Markshot.Image\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Markshot.exe"",0"
Root: HKCU; Subkey: "Software\Classes\Markshot.Image\shell\open"; ValueType: string; ValueName: ""; ValueData: "用 Markshot 编辑"
Root: HKCU; Subkey: "Software\Classes\Markshot.Image\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Markshot.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\Markshot.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Markshot 图片编辑器"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\Markshot.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Markshot.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.png\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.jpg\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.jpeg\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.jfif\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.bmp\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.gif\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.tif\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.tiff\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.webp\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ico\OpenWithProgids"; ValueType: none; ValueName: "Markshot.Image"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Markshot.exe"; Description: "启动 Markshot"; Flags: nowait postinstall skipifsilent
; An update started from Markshot runs silently: Markshot opens again when it is done.
Filename: "{app}\Markshot.exe"; Flags: nowait; Check: RestartAfterUpdate

[Code]
function RestartAfterUpdate: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RESTARTAFTERUPDATE') = 0 then
      Result := True;
end;
