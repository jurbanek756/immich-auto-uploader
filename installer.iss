; Inno Setup Script for Immich Auto Uploader
; Requires Inno Setup 6.0 or later

#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

[Setup]
AppId={{E8F6A31D-8F74-4C5D-927A-4D7893652E2B}
AppName=Immich Auto Uploader
AppVersion={#AppVersion}
AppVerName=Immich Auto Uploader {#AppVersion}
AppPublisher=Immich Auto Uploader Contributors
AppPublisherURL=https://github.com/jurbanek756/immich-auto-uploader
AppSupportURL=https://github.com/jurbanek756/immich-auto-uploader/issues
AppUpdatesURL=https://github.com/jurbanek756/immich-auto-uploader/releases
DefaultDirName={autopf}\ImmichAutoUploader
DefaultGroupName=Immich Auto Uploader
AllowNoIcons=yes
OutputDir=.
OutputBaseFilename=ImmichAutoUploader-{#AppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=commandline dialog
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=ImmichAutoUploader.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Immich Auto Uploader"; Filename: "{app}\ImmichAutoUploader.exe"
Name: "{autodesktop}\Immich Auto Uploader"; Filename: "{app}\ImmichAutoUploader.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ImmichAutoUploader.exe"; Description: "{cm:LaunchProgram,Immich Auto Uploader}"; Flags: nowait postinstall skipifsilent
