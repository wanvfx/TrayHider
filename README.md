# TrayHider

> Windows 11 托盘图标隐藏工具 · 作者 **wanvfx** · 开源（MIT）

隐藏 Windows 11 通知区域（托盘）里的指定图标，**被隐藏的程序继续在后台正常运行**。
单个 exe、约 75 KB，打开即用，不需要安装 .NET 运行时（直接跑在 Windows 10/11 自带的
.NET Framework 4.8 上）。

- 纯 Win32 + C#，不注入 Explorer、不修改其他软件、不需要管理员权限。
- 快捷键完全可自定义：组合键，或**按键序列**（可录制，任意键、任意次数）。
- 被动监听，不占用按键，其他软件的快捷键不受影响。
- 隐藏规则持久化，可开机自启，重启后自动恢复隐藏。

## 使用方法

到 [Releases](../../releases) 下载 `TrayHider.exe`，双击即可。

```
TrayHider.exe                启动图形界面（常驻托盘）
TrayHider.exe --list         列出所有活动托盘图标
TrayHider.exe --hide 微信     按进程名 / 提示文字 / 路径关键字隐藏
TrayHider.exe --show 微信     恢复
TrayHider.exe --about        关于（含原理说明）
TrayHider.exe --hotkey       快捷键设置面板
TrayHider.exe --hotkey-test  自检快捷键触发逻辑
TrayHider.exe --selftest     自检隐藏/恢复
```

图形界面：列表中选中图标 → 「隐藏选中」；「选项」里可设置开机自启、关闭缩到托盘、
隐藏本工具自己的托盘图标、以及快捷键。

## 快捷键

在「选项 → 快捷键设置…」里选择触发方式，**按一次呼出主窗口，再按一次隐藏**：

- **组合键**：Ctrl / Alt / Shift / Win 任意组合 + 任意按键（默认 `Ctrl + Alt + T`）。
- **按键序列**：点「● 录制按键」后按下一串键，自动识别出按了哪些键、各按几次；也可以
  用下拉框手动逐个添加。支持不同键、不同次数，例如 `T → T → NumLock`、`A → B → B → C`，
  任意键都行（含 NumLock、F10、方向键），并能设置整段序列的时间窗口。

两种方式都是**被动监听**：只观察按键、从不占用，别的软件的快捷键该怎么触发还怎么触发。

## 工作原理

Windows 11 把每个托盘图标的记录放在注册表 `HKCU\Control Panel\NotifyIconSettings`
（含程序路径、UID / IconGuid、提示文字、图标快照）。本工具据此枚举出正在运行的图标
（用进程路径匹配活动窗口，并用 `Shell_NotifyIconGetRect` 过滤掉已退出程序的缓存记录），
然后调用 `Shell_NotifyIcon(NIM_MODIFY)` 把图标状态设为 `NIS_HIDDEN`。

`NIS_HIDDEN` 是 Windows 11 官方识别的状态，资源管理器会让图标同时从任务栏和
「显示隐藏的图标」溢出窗口中消失，而图标所属程序本身完全不受影响。隐藏规则保存到
`%LOCALAPPDATA%\TrayHider\settings.json`，后台定时重新应用，因此程序重启或资源管理器
重建托盘之后隐藏状态依然保持。

## 从源码编译

只需要一个 .NET SDK 自带的 Roslyn 编译器（`csc.dll`）和 .NET Framework 4.8 参考程序集
（随 Visual Studio / Windows SDK 安装），不需要安装 .NET 运行时。

```
build.bat
```

产物是单个 `../编译/TrayHider.exe`。

## 许可

MIT，见 [LICENSE](LICENSE)。
