#define AppName "SQL Management Tools"
; AppVersion truyền từ ngoài bằng ISCC /DAppVersion="x.y.z" (workflow release);
; build tay không truyền thì dùng mặc định dưới đây.
#ifndef AppVersion
#define AppVersion "1.1.3"
#endif
#define AppPublisher "HongQuang"
#define AppExeName "SqlMigrator.exe"
#define AppDirName "SQL Management Tools"

[Setup]
AppName={#AppName}
; AppId CỐ ĐỊNH để Windows/Inno nhận ra cùng một app qua các lần đổi tên —
; thiếu nó, bản đổi tên sẽ cài song song thay vì nâng cấp bản cũ.
AppId={{E1DBDBD1-008A-4E4E-B4A4-916AA9E8BB82}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppDirName}
DefaultGroupName={#AppName}
OutputDir={#SourcePath}\App\Installer
OutputBaseFilename=SQLMigrator_Setup_{#AppVersion}
Compression=lzma/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
DisableDirPage=no
DisableProgramGroupPage=no
LicenseFile={#SourcePath}\LICENSE.txt
InfoBeforeFile={#SourcePath}\README.txt
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile={#SourcePath}\assets\app.ico
AppUpdatesURL=https://github.com/hongquang86/sqlmigrate/releases
AppSupportURL=https://github.com/hongquang86/sqlmigrate/issues

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Đóng gói TOÀN BỘ output publish (exe + Core.dll + rewrite-rules.json + phụ thuộc).
; Trước đây chỉ đóng gói exe nên bản cài đặt thiếu file và crash khi chạy.
Source: "{#SourcePath}\App\Publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\baselines"
Type: filesandordirs; Name: "{app}\transfers"

[Registry]
Root: HKCU; Subkey: "Software\{#AppPublisher}\{#AppName}"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey

[Code]
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
end;