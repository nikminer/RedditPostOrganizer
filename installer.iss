[Setup]
AppId=RedditGalleryDesktop
AppName=Gallery for Reddit
AppVersion=1.0.0
DefaultDirName={localappdata}\Programs\RedditGallery
DefaultGroupName=Gallery for Reddit
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=releases
OutputBaseFilename=RedditGallery-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\RedditGallery.exe
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "dist\RedditGallery\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Gallery for Reddit"; Filename: "{app}\RedditGallery.exe"
Name: "{autodesktop}\Gallery for Reddit"; Filename: "{app}\RedditGallery.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "RedditGallery"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\RedditGallery.exe"; Description: "Открыть Gallery"; Flags: nowait postinstall skipifsilent
