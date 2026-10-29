# DesktopLauncher · 桌面启动器

一张手机桌面，装下所有桌面快捷方式。

用**悬浮球**或**全局热键**唤出全屏启动器，把散落在桌面上的快捷方式收进自定义分类文件夹，点一下就能启动。桌面上的文件**不搬、不删、不改名**——整理只发生在启动器里。

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6)
![UI](https://img.shields.io/badge/WPF-desktop-5B8CFF)

> **完整使用指南：[docs/USAGE.md](docs/USAGE.md)** —— 安装、每个功能的操作步骤、键位、常见问题都在这里。

## 功能

### 常驻与唤出
- **悬浮球**：半透明小球停在屏幕边缘，单击开/关启动器；可拖动，靠近边缘自动吸附隐藏，鼠标碰到再滑出
- **全局热键**：默认 `Ctrl+Alt+D`，可在设置里换组合；悬浮球隐藏时照样能用
- **托盘常驻 + 开机自启**，单实例运行
- **多屏友好**：在鼠标所在的那块屏幕上打开，PerMonitorV2 适配高 DPI

### 启动器
- **全屏宫格**：当前壁纸模糊暗化作背景，淡入 + 上浮动画
- **分类文件夹**：手机桌面式磁贴，格子里预览前 6 个应用，点开是应用宫格
- **拖拽整理**：应用拖到别的文件夹即移动；文件夹内拖动可排序（落在目标磁贴左半/右半决定插入位置）
- **批量操作**：文件夹内「☐ 选择」多选，批量移动或移除
- **即时搜索**：输入即过滤已加入的应用，回车启动第一个
- **编辑模式**：新建 / 重命名（`F2`）/ 删除文件夹，添加应用，移除应用
- **原生右键菜单**：应用磁贴右键可调出与资源管理器一致的 Windows 菜单（复制、打开文件位置、压缩、属性……）

### 桌面整理
- **桌面图标隐藏**：只给桌面根目录的 `.lnk` / `.exe` / `.url` 加 `Hidden` 属性，文件夹、压缩包、文档一律不动；一键恢复，并自动还原资源管理器的「显示隐藏文件」设置
- **文件夹快捷键**：任意多条，按下直接在资源管理器打开指定目录
- **只做索引**：启动器只记录路径，不复制、不移动、不删除任何文件

## 快速开始

需要 Windows 10 / 11；构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
git clone https://github.com/ka1saaa/DesktopLauncher.git
cd DesktopLauncher
dotnet run --project DesktopLauncher -c Release
```

生成免安装的单文件 exe：

```bash
dotnet publish DesktopLauncher -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish
```

`publish\DesktopLauncher.exe` 约 70 MB，自带运行时，拷到任意 Win10/11 x64 机器双击即用。

首次启动后：

1. 托盘出现图标，屏幕右侧出现悬浮球
2. 单击悬浮球（或按 `Ctrl+Alt+D`）打开启动器
3. 右上角「✏ 编辑」→ 底部「+ 新建文件夹」
4. 点开文件夹 →「+ 添加应用」→ 勾选桌面快捷方式
5. 回到主界面，把应用磁贴拖进各个文件夹

## 键位速查

| 操作 | 方式 |
| --- | --- |
| 打开 / 收起启动器 | `Ctrl+Alt+D` 或单击悬浮球 |
| 关闭文件夹面板 / 启动器 | `Esc`、右上角 `×`、点击空白处 |
| 启动搜索结果第一项 | 输入关键字后 `Enter` |
| 重命名当前文件夹 | `F2` |
| 移动应用 | 拖动磁贴到目标文件夹，或右键「移入」 |
| 文件夹内排序 | 拖动磁贴到目标磁贴左半 / 右半 |
| 批量整理 | 文件夹面板「☐ 选择」→ 点选 → 移动 / 移除 |
| 应用菜单 | 右键应用磁贴 |
| 悬浮球菜单 | 右键悬浮球 |

更多细节见 [使用指南](docs/USAGE.md)。

## 数据与安全

| 项目 | 位置 |
| --- | --- |
| 配置 | `%APPDATA%\DesktopLauncher\config.json` |
| 图标缓存 | `%APPDATA%\DesktopLauncher\icons\` |
| 自定义悬浮球（可选） | `%APPDATA%\DesktopLauncher\ball.png` |

- **完全离线**：不联网、不收集数据、不需要管理员权限
- **不移动 / 重命名 / 删除桌面文件**；唯一的文件属性改动是「隐藏桌面图标」写入的 `Hidden`，可在托盘菜单一键还原
- 卸载：先恢复图标 → 托盘「退出」→ 删除 exe 与上述目录

## 项目结构

```
DesktopLauncher/
├── Models/                 # AppConfig / Category / AppEntry / FolderHotkey
├── Services/
│   ├── DesktopPathResolver.cs   # 注册表解析真实桌面（支持重定向 / OneDrive）
│   ├── AppScanner.cs            # 扫描桌面、公共桌面、额外目录
│   ├── LnkParser.cs             # WScript.Shell 解析 .lnk / .url
│   ├── IconService.cs           # 图标提取（PrivateExtractIcons）+ 内存/磁盘缓存
│   ├── WallpaperService.cs      # 壁纸读取 + 低成本模糊预渲染
│   ├── HotkeyService.cs         # RegisterHotKey 全局热键（隐藏消息窗口）
│   ├── LaunchService.cs         # 启动 .lnk / .exe / .url
│   ├── ConfigStore.cs           # JSON 配置持久化
│   ├── DesktopIconHider.cs      # 隐藏 / 恢复桌面应用图标
│   ├── ExplorerSettings.cs      # 资源管理器「显示隐藏文件」开关
│   └── ShellContextMenu.cs      # 原生 IShellFolder / IContextMenu 右键菜单
└── UI/
    ├── FloatingBallWindow.xaml  # 悬浮球
    ├── LauncherWindow.xaml      # 全屏启动器（宫格 / 文件夹面板 / 搜索 / 编辑）
    ├── TrayController.cs        # 托盘图标与菜单
    └── Dialogs/                 # 添加应用 / 重命名 / 设置
```

## 开发说明

- 目标框架 `net10.0-windows`，WPF + WinForms（WinForms 只用于托盘和系统对话框）
- 单实例：`Local\DesktopLauncher_SingleInstance` 互斥体，重复启动会提示
- 调试参数（开发用）：`--show` 直接打开启动器、`--settings` 打开设置、`--addapp` 打开添加应用、`--icontest=<路径>` 导出图标测试、`--shellmenu=<路径>` 诊断原生菜单
- 全部参数与诊断方式见[使用指南 · 调试与二次开发](docs/USAGE.md#8-调试与二次开发)

## 反馈

Bug 与建议请开 [Issue](https://github.com/ka1saaa/DesktopLauncher/issues)。
