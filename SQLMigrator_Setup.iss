#define AppName "SQL Migrator"
#define AppVersion "1.1.0"
#define AppPublisher "HongQuang"
#define AppExeName "SqlMigrator.exe"
#define AppDirName "SQL Migrator"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppDirName}
DefaultGroupName={#AppName}
OutputDir=D:\Projects\AI-APP\MigrateSQL\App\Installer
OutputBaseFilename=SQLMigrator_Setup_{#AppVersion}
Compression=lzma/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
DisableDirPage=no
DisableProgramGroupPage=no
LicenseFile=D:\Projects\AI-APP\MigrateSQL\LICENSE.txt
InfoBeforeFile=D:\Projects\AI-APP\MigrateSQL\README.txt
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}
AppUpdatesURL=https://github.com/hongquang86/sqlmigrate/releases
AppSupportURL=https://github.com/hongquang86/sqlmigrate/issues

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Đóng gói TOÀN BỘ output publish (exe + Core.dll + rewrite-rules.json + phụ thuộc).
; Trước đây chỉ đóng gói exe nên bản cài đặt thiếu file và crash khi chạy.
Source: "D:\Projects\AI-APP\MigrateSQL\App\Publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

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