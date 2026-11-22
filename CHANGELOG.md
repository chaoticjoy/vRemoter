# 更新日志

## v1.4.0 · 2026-11-22

> 从本版本起，vRemoter 小卒优化版正式成为 **macOS + Windows 双平台项目**：保留完整 macOS（Apple Silicon，macOS 12+）支持的同时，新增对 **Windows 10（1809 / Build 17763+）与 Windows 11（x64）** 的原生桌面端适配。

### 新功能

- **macOS & Windows 双平台原生支持**：
  - **macOS**：支持 Apple Silicon（macOS 12 及以上），延续 `vRemoteDriver.driver`、ATVV 语音直连、按住/点按触发、应用切换自动聚焦与 `.pkg` / `.dmg` 安装包。
  - **Windows 10 / 11（x64）**：原生 .NET 8 / WPF 桌面端，支持 Chromecast / X6 双遥控器 BLE ATVV 语音直连、WASAPI + VB-Cable 双路麦克风 120ms 对齐混音、豆包输入法 / 微信输入法录音状态实时闭环监控、左右分侧触发键与点按/长按双模式、按键重映射、深色浮层应用切换器、打开指定应用与 UIA3 输入框自动聚焦。
- **打开指定应用并聚焦输入框**：按键映射新增「打开指定应用…」，可选择任意桌面应用（macOS `.app` / Windows `.exe`）；Windows 端弹窗同步提供「同时加入『切换应用自动聚焦』名单」选项（默认勾选），按下遥控器按键即可拉起或切到该应用并自动切入输入框。
- **切换应用时自动聚焦输入框**：控制台新增「应用聚焦」标签页，支持维护自动聚焦应用列表（内置一键添加 Antigravity IDE、从运行中添加、浏览本地应用）。通过遥控器「切换应用」、系统切换快捷键或鼠标切回这些应用时，自动将焦点拉回输入框；支持「按语音键前检查聚焦」。
- **一键聚焦当前输入框**：按键映射新增「聚焦当前输入框」，按下可立即扫描当前前台应用并把光标切入其输入框（支持从代码编辑区等非聊天区直接拉回输入框）。
- **结构指纹指定输入框**：针对同一窗口存在多个文本输入区的应用（如 IDE 同时打开代码区和聊天区），支持 30 秒交互式「指定输入框」绑定，生成与版本绑定的无文本 SHA-256 结构指纹（`v2`），重启后自动找回。
- **GitHub Actions 双平台自动构建与发布**：支持 CI 自动测试与构建，Release 工作流支持打 `v*` 标签同时打包发布 macOS（`.pkg` / `.dmg`）与 Windows（`.exe` 安装包 / `.zip` 便携版），也支持手动单独触发任一平台发布流程。

### 改进与修复

- **修复 Windows 按键映射页面下拉框文字不可见问题**：重写 `ConsoleComboBoxStyle` 与 `ConsoleComboBoxItemStyle` 深色控件模板，解决 Windows WPF 默认亮色下拉背景与白色前景色冲突导致文字看不见的问题，并优化标签页与设备卡片悬停样式。
- **修复 Windows 遥控器按键映射链路与兼容多类 HID / BLE GATT 上报**：
  - 升级 `WudfHostHidTap` + `WudfAlpcGattParser` 原生 x64 旁路捕获引擎：针对 Google Chromecast Voice Remote（`18D1:9450`）部分按键发送 8 字节 BLE GATT（`0x2A4D`）报告超出 2 字节系统 Report Map、在 `WUDFHost.exe` 触发 `STATUS_BUFFER_TOO_SMALL (0xC0000023)` 而无法到达 `RawInput` / `ReadFile` 的问题，新增 `NtAlpcSendWaitReceivePort`（ALPC）WinRT `MEOW` / `IID_IBuffer` 报文提取与 `ChromecastButtonDecoder` 状态差分映射，并兼容 `IOCTL 0x80018483` 3 字节 Consumer Control 报文提取与原地清零抑制；
  - 系统调用号（`NtDeviceIoControlFile` / `NtReadFile` / `NtWriteFile` / `NtRemoveIoCompletionEx` / `NtAlpcSendWaitReceivePort`）在运行时从本机 `ntdll.dll` 动态提取，不硬编码特定小版本的 syscall 表；
  - 自动守护与修复 `BTHLEDevice\{00001812-...}` 系统 `hidbthle.inf`（`mshidumdf`）驱动绑定，支持离线/休眠态 PnP 节点自愈与蓝牙电台自动解卡重连，并在按键活动时即时唤醒 ATVV 语音通道重连；
  - 修复 Windows BLE `VID&0118d1_PID&9450` / `VID&0002xxxx_PID&xxxx` 设备路径解析缺陷与 `cfgmgr32` 父设备节点链回溯解析；
  - 升级 `RawInputMonitor`（支持 `RIDI_PREPARSEDDATA` + `HidP_GetUsagesEx` / `HidP_GetUsages`）与 `KeyCorrelationEngine` 设备级按键来源关联引擎。
- **修复 Windows 切回应用时自动聚焦未及时切入输入框的问题**：
  - 修复 `WinUiaFocusBackend.IsFocused` 读取 `SubtreeCacheRequest` 过期缓存的问题，改为实时读取 `HasKeyboardFocusProperty`；
  - `InputFocusCoordinator` 对 `Confirming` 状态增加 250ms 轮询复检（最多 4 次）与切换日志，等待目标应用激活后再确认焦点；
  - 修复 `LowLevelKeyboardGuard` 在松键（如 `Alt+Tab` / `Win+Tab` 切窗释放修饰键）或遥控器按键触发时误自增输入活动计数并中止自动聚焦的问题；
  - 当手动绑定未生成持久化结构指纹（`Locator` 为空）时，自动切换回退至自动发现流程，避免误卡在 `NeedsBinding`。
- **适配 Electron / WebView 桌面应用（如 Antigravity IDE）**：自动请求 `AXEnhancedUserInterface`（macOS）/ `WM_GETOBJECT` 与 UIA3（Windows）唤醒辅助功能树，并同时识别常见聊天输入框属性。
- **解决切回 IDE 时焦点停留在代码编辑区的问题**：新增「从代码区等位置切回时也拉回输入框」选项（默认开启），从代码编辑区切回时可正确拉回聊天输入框，同时继续保护搜索框、重命名框、密码框与弹窗菜单不被误抢焦点。
- **修复后台线程前台应用状态滞后与误中断问题**：macOS 前台应用改由主线程激活事件同步与实时前台查询联合获取，并移除触控板惯性滚动对输入打断判定的干扰。
- **Windows 系统兼容性与支持边界**：
  - **支持系统与架构**：Windows 10 `1809`（Build `17763`）及以上版本（如 21H2 / 22H2）与 Windows 11（Build `22000+`），仅支持 **x64**（安装包 `MinVersion=10.0.17763`、`x64compatible`）；深色标题栏按 Build 号（`17763+` / `18985+`）与 Win11 圆角（`22000+`）自动适配。
  - **不支持环境**：Windows 7 / 8 / 8.1、Windows 10 `1803` 及更早版本、Windows ARM64 与 32 位 x86（`WUDFHost.exe` 旁路桩为 x64 原生机器码）。
  - **前置依赖与实机覆盖**：需以管理员权限运行（已在 manifest 声明），语音输入法混音需安装虚拟声卡驱动（如 VB-Cable）；Chromecast 遥控器整条语音 + ALPC 全按键映射链路已在 **Windows 10 Build 19045（x64）** 实机验证通过，Windows 11（x64）基于通用 WinRT/UIA3/动态 syscall 设计理论兼容，仍建议实机进一步验证。
- **维护仓库迁移**：更新源切换至 `chaoticjoy/vRemoter`（保留原 Bundle ID，macOS 升级无需重新授予权限）。

## v1.3.2 社区版 · 2026-11-12

### 修复

- 修复 macOS 14.2 以下（macOS 12、13、14.0、14.1）按一下语音键，遥控器麦克风亮一下、约 1 秒就灭的问题。这些系统查不到输入法是否在录音，vRemoter 以前会把这当成“输入法没启动”而自动关麦。现在会自动进入「兼容模式」：只要输入法在运行就保持收音，开关完全听按键的。兼容模式下状态栏会显示“兼容模式”。
- 修复开机启动可能一直打开旧版本的问题：开机启动记录的是开启时那份 App 的路径，重新安装到别的位置后不会更新。现在 vRemoter 启动时会自动把开机启动指向「应用程序」文件夹里的这一份；记录的 App 已不存在时也会自动改正。

## v1.3.1 社区版 · 2026-11-10

### 新功能

- 新增触发方式「按住说话」：输入法里的语音快捷键是按住才录音的那种时选它，识别期间 vRemoter 会一直按住这个键，结束时再松开。原来的做法改名为「点按切换」，仍是默认。

### 修复

- 修复触发键设成输入法“按住说话”快捷键时，按一下遥控器麦克风亮一下就灭、无法识别的问题（选「按住说话」即可）。

### 改进

- 触发键说明不再只写当前输入法名称，豆包和微信输入法通用。

## v1.3.0 社区版 · 2026-11-10

从这一版起，vRemoter 社区版独立维护，不再同步原项目更新。本版包含原版 v1.2.3 的全部功能。

### 新功能

- 支持微信输入法作为语音输入法，并自动切换系统输入。
- 语音触发键可以单独选择左侧或右侧的 Option、Command、Control、Shift；只认所选的一侧，另一侧照常用于快捷键。

### 修复

- Chromecast 遥控器快速轻点语音键时，遥控器麦克风未打开会自动重开。
- 启用按键映射时系统拒绝独占设备，会退回共享模式。
- 遥控器语音连接断开后自动重连。

### 变化

- App 改名为“vRemoter 社区版”，标识改为 `io.github.ccc333cn.vremoter`。首次启动会自动导入原版设置；macOS 权限需要重新授权一次。
- 检查更新改为读取 GitHub 仓库 ccc333cn/vRemoter 的 Releases。
- 去掉使用统计、打赏和购买入口。
- 本次音频驱动没有变化。

## v1.2.3 · 2026-11-09

### 新功能

- 新增系统级「锁屏」「睡眠」「切换应用」三个映射动作，遥控器电源键等均可指定。
- 切换应用采用定时器会话：单击切到上一个应用；会话中连按切换键或按方向键逐个选择，停手 1 秒自动落地。X6 与 Chromecast 同一套交互。
- 主界面副标题改为显示当前版本号，一眼确认测试与正式版本。

### 改进

- 双路麦克风的「静音 / 独奏」按钮合并为拨动开关，提示更直白；混音逻辑不变。
- 按键映射的自定义菜单显示「自定义（当前：…）」，未录制时显示「自定义…」。
- 豆包输入法设置向导新增第 5 步引导图，明确要求将麦克风选为 vRemoteDr 2ch。
- 官网与仓库示意图更新。

### 更新说明

- 本次音频驱动没有变化。首次安装请使用 PKG；已经安装过驱动的用户可使用 DMG 只更新 APP。
- 「睡眠」首次触发时 macOS 会询问是否允许 vRemote 控制"系统事件"，允许一次即可。

## v1.1.1 · 2026-09-03

### 修复

- 修复 Chromecast Voice Remote 使用一段时间后，短按偶尔只打开豆包、遥控器麦克风却未持续收音的问题。
- 修复电脑 Option 关闭豆包时，较晚到达的音频状态可能短暂重新开启遥控器音频的问题。
- 改进双遥控器同时连接时的会话隔离、开麦确认与有限重试，避免一台设备的旧事件干扰另一台设备。
- 修复 X6 第一次短按开麦可能被误判为旧音频事件的问题。
- 更新按键映射页中的 Chromecast Remote 产品图，移除白底与标注线并优化显示尺寸。

### 更新说明

- 本次音频驱动没有变化。首次安装请使用 PKG；已经安装过驱动的用户可使用 DMG 只更新 APP。
- X6 官方支持范围仍为短按切换；Chromecast 官方支持短按切换与长按说话。

## v1.1.0 · 2026-09-03

### 新功能

- 新增 Chromecast Voice Remote（VID `0x18D1` / PID `0x9450`）支持。
- Chromecast 语音键支持短按切换和长按说话、松开结束。
- 新增 X6 Remote 与 Chromecast Voice Remote 按键映射页面。
- 支持方向、确认、返回、Home、音量、静音、YouTube、Netflix、信源及自定义键盘快捷键映射。
- 豆包语音触发键可选择 Option、Command、Control、Shift 或 Fn，默认 Option。
- 按键设置按遥控器型号保存，不绑定某一只实体设备。

### 改进与修复

- 主控制台统一使用“遥控器”措辞，不再把音频能力限定为 X6。
- X6 与 Chromecast Remote 可以同时连接并保持各自的语音链路。
- 启用映射时改为独占匹配型号的 HID 集合，再由 APP 重放映射后的动作，避免原始媒体行为与新映射同时触发。
- 关闭映射时保留系统原行为，用户可随时恢复。

### 支持边界

- Chromecast 当前只确认 VID `0x18D1`、PID `0x9450` 的型号档案，不代表支持所有 Chromecast 遥控器。
- X6 鼠标模式切换键由设备固件内部处理，不能单独重映射；启用映射后仍会透传鼠标移动、点击、滚轮和背面标准键盘。
- 语音键固定用于豆包语音控制，不作为普通按键映射。
- Fn 的触发效果依赖具体 Mac 与豆包输入法配置，首次使用时应现场确认。

## v1.0.1 · 2026-08-28

- 暂停 APP 与官网的购买遥控器入口。
- 没有启用且有效的购买链接时，APP 自动隐藏购买按钮。
- 本次驱动没有变化，已安装用户可使用 DMG 只更新 APP。

## v1.0.0 · 2026-08-27

- 首个公开版本。
- PKG 同时安装 vRemoter APP 与 `vRemoteDr 2ch` 音频驱动。
