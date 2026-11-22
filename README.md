# vRemoter 社区版

把蓝牙语音遥控器变成 **macOS 与 Windows** 上的跨平台语音输入与全局快捷键控制器：按遥控器语音键，豆包输入法或微信输入法开始语音识别；对着遥控器说话，文字直接输入到当前应用的指定输入框。遥控器其余按键还可映射为方向键、快捷键、锁屏、切换应用、打开指定应用并自动聚焦等功能。

本项目基于 [ccc333cn/vRemoter](https://github.com/ccc333cn/vRemoter)（原自 [VincentKingHsu/vRemoter](https://github.com/VincentKingHsu/vRemoter)，MIT 协议）继续独立维护。

当前版本：`1.4.0`

## 核心特性（相对上游 v1.3.2）

### 1. macOS + Windows 双平台原生支持

- **macOS（Apple Silicon，macOS 12+）**：原生 Swift 桌面端，延续 `vRemoteDriver.driver` 音频驱动、BLE ATVV 语音直连、按住/点按双触发模式、辅助功能应用切换及 `.pkg` / `.dmg` 安装包。
- **Windows 10 / 11（x64）**：全新原生 .NET 8 / WPF 桌面端，完整移植并适配 Windows 生态：
  - **语音与双路音频**：WinRT BLE ATVV 直连收音、WASAPI + VB-Cable 双路麦克风 120ms 对齐混音、豆包输入法 / 微信输入法录音状态闭环监控、左右分侧触发键与点按/长按双模式。
  - **Chromecast 全按键底层捕获**：自研 `WudfHostHidTap` + `WudfAlpcGattParser` 原生 x64 无锁旁路引擎，从系统 `ntdll.dll` 动态提取 syscall 并拦截 `WUDFHost.exe` 内的 WinRT ALPC GATT（`0x2A4D`）与 `IOCTL 0x80018483` 报文，解决 Google Chromecast 原装遥控器在 Windows 下除语音键外其余按键无法透传到标准 HID / `RawInput` 的问题；同时集成 `hidbthle.inf` 驱动自愈与设备级来源关联。
  - **CI / Release 双平台自动化**：GitHub Actions 支持推送提交自动分平台测试构建，打 `v*` 标签或手动触发一键打包发布 macOS（`.pkg` / `.dmg`）与 Windows（`Setup.exe` / 便携 `.zip`）。

### 2. 应用焦点管理与自动聚焦（双平台）

- **切换应用时自动聚焦输入框**：「应用聚焦」页支持维护自动聚焦应用名单（内置一键添加 Antigravity IDE、从运行中的应用添加、浏览 `.app` / `.exe`）。无论通过遥控器「切换应用」、系统快捷键还是鼠标切回目标应用，自动将输入焦点拉回聊天/文本输入框（支持「按语音键前检查聚焦」）。
- **打开指定应用并直达输入框**：按键映射新增「打开指定应用…」，按下遥控器按键即可拉起或激活指定应用并在窗口就绪后自动切入输入框；支持保存时同步加入「切换应用自动聚焦」名单。
- **一键聚焦当前输入框**：按键映射新增「聚焦当前输入框」，随时一键扫描当前前台应用（macOS AX / Windows UIA3）并将光标切回主输入框。
- **结构指纹指定输入框（`v2`）**：针对同窗多输入框应用（如 IDE 同时存在代码编辑区与聊天输入框），提供 30 秒交互式「指定输入框」绑定，生成与版本绑定的无文本 SHA-256 结构指纹，跨重启自动恢复；支持从代码区切回时拉回聊天框，同时继续保护搜索框、重命名框、密码框与弹窗菜单不被误抢焦点。

## 和上游社区版的其他差异

- **支持微信输入法**：除豆包外，可以选微信输入法作为语音输入法，vRemoter 会自动切换系统输入。
- **触发键分左右**：macOS 的 Option、Command、Control、Shift 与 Windows 的 Ctrl、Shift、Alt、Win 均可单独选左侧或右侧，不干扰另一侧日常快捷键。
- **两种触发方式**：「点按切换」对应输入法按一下开始、再按一下结束的快捷键；「按住说话」对应按住才录音的快捷键，识别期间保持按住状态。
- **稳定性修复**：修复 Chromecast 快速轻点麦克风未开、独占设备失败退回共享模式、语音断连自动重连、切回应用自动聚焦确认与松键防误打断等问题。
- **更新来源 & 精简入口**：App 内更新检查读取 `chaoticjoy/vRemoter` 的 [Releases](https://github.com/chaoticjoy/vRemoter/releases)，移除使用统计上报、打赏和购买入口。

## 支持的遥控器

- **Google Chromecast 语音遥控器**（VID `0x18D1` / PID `0x9450`）
  - 语音键支持短按切换、长按说话 / 松开结束。
  - 方向、确认、返回、Home、YouTube、Netflix、信源、音量、静音、电源等可映射键：
    - **Windows 10/11（x64）**：原装与兼容遥控器通过 `WudfHostHidTap` ALPC / IOCTL 旁路均支持全部 14 个可映射键。
    - **macOS**：Google 原装遥控器在 macOS 上仅发送语音键；同型号第三方兼容遥控器支持全部按键。
- **X6 Remote**（VID `0x1D5A` / PID `0xC081`）：支持语音键短按切换与正面 15 个可映射按键。

## 安装与环境要求

### macOS
1. 到 [Releases](https://github.com/chaoticjoy/vRemoter/releases) 下载最新的 `.pkg`（仅升级 App 可下载 `.dmg`）。
   - 仅支持 **Apple 芯片（M 系列）** 的 Mac，**macOS 12** 或更高版本。
2. 安装包无公证签名，首次打开请到“系统设置 → 隐私与安全性”点击“仍要打开”。
3. 安装时会重启系统音频服务，并按界面引导授予辅助功能、输入监控、蓝牙、麦克风权限。

### Windows
1. 到 [Releases](https://github.com/chaoticjoy/vRemoter/releases) 下载 `Setup.exe` 安装包（或 `Portable.zip` 便携版）。
   - 支持 **Windows 10（1809 / Build `17763` 及以上）** 与 **Windows 11**，**仅限 x64 架构**（不支持 Win7/Win8、Win10 1803 及以下、ARM64 或 x86）。
   - 已在 **Windows 10 Build 19045（x64）** 实机验证通过；Windows 11（x64）基于通用 WinRT / UIA3 / 动态 syscall 设计理论兼容，建议实际使用中进一步验证。
2. 程序需以**管理员权限**运行（用于 `WUDFHost.exe` 按键旁路捕获及 UIPI 全局聚焦）；语音混音需提前安装虚拟声卡驱动（如 **VB-Cable**），并在豆包/微信输入法中将麦克风设为对应虚拟音频输入设备。

**从原版或旧社区版升级（macOS）**：
- 安装后将替换 `/Applications/vRemote.app` 并自动导入原版配置；因应用标识变更需重新授予权限一次。

## 设置输入法

- **豆包输入法**：在豆包设置里把麦克风选为虚拟输入设备（macOS 为 `vRemoteDr 2ch`，Windows 为 VB-Cable 输出端 / 混音通路）。
- **微信输入法**：vRemoter 会自动把系统输入切到遥控器语音混音通道。
- 请确保 vRemoter 中的**触发键**（及左右侧）与**触发方式**（点按切换 / 按住说话）和输入法内快捷键一致。

### macOS 14.2 以下：兼容模式

macOS 12、13、14.0、14.1 无法读取输入法实时录音状态，vRemoter 会自动进入「兼容模式」，建议使用「按住说话」以免状态错位。

## 从源码构建

### macOS
需要 Xcode 命令行工具（Swift 5.9+）：

```bash
Scripts/run-self-tests.sh
Scripts/package-app.sh
VREMOTER_DRIVER=/path/to/vRemoteDriver.driver Scripts/build-pkg.sh
```

### Windows
需要 .NET 8 SDK：

```powershell
dotnet test Windows/vRemoter.sln -c Release
dotnet publish Windows/src/vRemoter.Windows/vRemoter.Windows.csproj -c Release -r win-x64 --self-contained true
```

## 许可

- vRemoter 代码使用 MIT 协议，见 [LICENSE](LICENSE)。
- macOS 音频驱动 `vRemoteDriver.driver` 基于 [BlackHole](https://github.com/ExistentialAudio/BlackHole) 0.4.1（GPL v3），详见 [Driver/README.md](Driver/README.md)。
- 其他第三方代码见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

完整更新记录见 [CHANGELOG.md](CHANGELOG.md)。
