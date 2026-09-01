; Inno Setup 脚本 —— EndfieldCharge Windows 安装器
; 使用：iscc installer\EndfieldCharge.iss
; CI 会先把 MyAppVersion 替换为实际版本号

#define MyAppName "EndfieldCharge"
#define MyAppVersion "0.0.0"
#define MyAppPublisher "QinAnze"
#define MyAppURL "https://github.com/QinAnze/zmd-charge"
#define MyAppExeName "EndfieldCharge.exe"

[Setup]
AppId={{6B6BD34B-6E4D-490C-A8AE-62963965257A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\EndfieldCharge
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=Output
OutputBaseFilename=EndfieldCharge-{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName} {#MyAppVersion}
; 默认当前用户安装（不强制管理员），用户可在安装时选择提升权限
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
; 中文语言文件不属于 Inno Setup 官方默认安装（在官方仓库的 Languages 目录），
; CI 机器上 compiler:Languages\ 下找不到，因此已把 ChineseSimplified.isl 打进仓库
; （installer\Languages\），用相对路径引用（相对脚本所在目录）。
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked
Name: "startup";     Description: "开机自动启动 {#MyAppName}"; GroupDescription: "附加选项:"

[Files]
; 注意：PublishSingleFile 只把托管 dll 打进 exe，SkiaSharp 的 native dll
; （libSkiaSharp / libHarfBuzzSharp / av_libglesv2）必须放在 exe 旁边，否则启动即崩。
; 因此这里打包整个 publish 目录，而不是只拷 exe。
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";              Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}";          Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";        Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 勾选「开机自动启动」时写入 HKCU Run（与程序内托盘勾选同一位置，程序内可再取消）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent
