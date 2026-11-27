# TrayHider

> Windows 10 / 11 托盘图标隐藏工具 · 作者 **wanvfx** · 开源（MIT）

隐藏通知区域（托盘）里指定的图标，**被隐藏的程序继续在后台正常运行**。
单个 exe、约 75 KB，打开即用，不需要安装 .NET 运行时（直接跑在 Windows 自带的
.NET Framework 4.8 上）。

**支持平台：Windows 10 / Windows 11**（不支持 Windows 7 及更早版本）。两台系统的托盘
实现完全不同，本工具对 Win11 和 Win10 各走一条原生路径，见下文「工作原理」。

**随时可恢复**：隐藏只是改变显示状态，在主窗口点「恢复选中 / 恢复全部」，
或命令行 `TrayHider.exe --show <关键字>` / `TrayHider.exe --restore-all`，图标就会回到托盘。

> 「恢复全部」会把**当前所有处于隐藏状态**的图标都恢复（不管是不是本工具隐藏的），
> 所以用旧版本隐藏的图标也能一把救回来。

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
TrayHider.exe --show 微信     恢复显示
TrayHider.exe --restore-all   恢复当前所有处于隐藏状态的图标（含旧版本隐藏的）
TrayHider.exe --about        关于（含原理说明）
TrayHider.exe --hotkey       快捷键设置面板
TrayHider.exe --hotkey-test  自检快捷键触发逻辑
TrayHider.exe --selftest     自检隐藏/恢复
```

图形界面：**一个窗口、三个切换页**——「托盘隐藏」「窗口规则」「运行日志」，在窗口左上角切换。
窗口大小可自由拖拽（无最小尺寸限制）。**底部状态栏随当前页显示**：托盘页显示图标统计、
规则页显示规则与今日拦截数、日志页显示日志条数。

托盘隐藏页支持：
- 右键菜单：隐藏选中 / 显示选中 / 全部显示 / 刷新（右键空白处只有刷新可用）
- **Ctrl + A** 全选；**Ctrl + 点击** 间隔多选；**Shift + 点击** 连选；双击切换显示/隐藏
- 「选项」里可设置跟随开机启动、开机启动隐藏窗口到托盘、隐藏本工具托盘、
  扫描时包含溢出区图标、快捷键。

窗口规则页用一个列表分两组：「当前可见窗口」（勾选后加入规则）和「已保存规则」（右键编辑/删除），
按「标题关键字 / 进程名 / 窗口类名」（或且组合）匹配窗口，命中后执行关闭 / 隐藏 / 最小化；
可点选窗口生成规则。**本工具自身永远不会被拦截**（引擎按进程号和窗口句柄双重排除自己）。

**截图拦截**：点「截图拦截…」框选屏幕区域，工具会截取该区域并生成**点阵特征**（8×8 采样点），
之后只要画面与采集时一致（阈值默认 80%、颜色容差 16，可在规则里调），就自动拦截——
即使弹窗的**标题、进程名每次都变**，只要画面不变就能拦住。规则里还能限定**窗口尺寸范围**。

已保存规则按**软件分组**显示（软件弹窗名称 / 弹窗位置 / 拦截次数 / 操作，带软件图标），
右键可编辑、删除、调整顺序。

**规则导入导出**（菜单「规则导入导出」）：
- 导出规则 / 导入规则：本工具的 JSON 格式，可分享给别人。
- 导入火绒自带规则库：读取火绒弹窗拦截的规则库 JSON（`docs/火绒自带规则库.json`，或自选文件），
  自动转换成 TrayHider 规则（火绒内置 QQ/WinRAR/迅雷/WPS/百度网盘/网易云/向日葵/搜狗输入法等
  9 组共 64 条规则）。

运行日志页按时间倒序列出**窗口拦截**和**托盘隐藏/恢复**两类记录（时间 / 类别 / 名称 / 进程 /
动作 / 结果），可一键清空。

点窗口右上角 × 会把窗口**缩到托盘**（不退出）；要真正退出，用托盘图标右键 →「退出」，
或菜单「操作 → 退出」。

## 快捷键

在「选项 → 快捷键设置…」里选择触发方式，**按一次呼出主窗口，再按一次隐藏**：

- **组合键**：Ctrl / Alt / Shift / Win 任意组合 + 任意按键（默认 `Ctrl + Alt + T`）。
- **按键序列**：点「● 录制按键」后按下一串键，自动识别出按了哪些键、各按几次；也可以
  用下拉框手动逐个添加。支持不同键、不同次数，例如 `T → T → NumLock`、`A → B → B → C`，
  任意键都行（含 NumLock、F10、方向键），并能设置整段序列的时间窗口。

两种方式都是**被动监听**：只观察按键、从不占用，别的软件的快捷键该怎么触发还怎么触发。

## 工作原理

### Windows 11

Windows 11 把每个托盘图标的记录放在注册表 `HKCU\Control Panel\NotifyIconSettings`
（含程序路径、UID / IconGuid、提示文字、图标快照）。本工具据此枚举出正在运行的图标
（用进程路径匹配活动窗口，并用 `Shell_NotifyIconGetRect` 过滤掉已退出程序的缓存记录），
然后调用 `Shell_NotifyIcon(NIM_MODIFY)` 把图标状态设为 `NIS_HIDDEN`。

`NIS_HIDDEN` 是 Windows 11 官方识别的状态，资源管理器会让图标同时从任务栏和
「显示隐藏的图标」溢出窗口中消失，而图标所属程序本身完全不受影响。

### Windows 10

Windows 10 的通知区域是传统的 Explorer 工具栏（`Shell_TrayWnd → TrayNotifyWnd →
SysPager → ToolbarWindow32`），每个图标是工具栏上的一个按钮。本工具跨进程读取这些按钮的
结构和提示文字，再用 `TB_HIDEBUTTON` 把指定按钮移入「显示隐藏的图标」溢出区——
这就是 Windows 10 原生的隐藏方式，图标所属程序同样完全不受影响。
（Win10 没有 `NotifyIconSettings` 那套字段，所以不能复用 Win11 的路径。）

> 说明：**远程桌面会话**里的托盘图标属于另一个会话/桌面，无法枚举，不支持隐藏。
>
> 说明：**Windows 10 默认把新程序的图标放进「显示隐藏的图标」溢出区**，所以本工具刚装好时
> 任务栏上可能看不到自己的图标——点任务栏右下角的 `^` 就能看到，也可以把它拖到任务栏上常驻显示。
> 程序启动时会自动检测并提示一次。

## 同类工具对比

| 工具 | 平台 | 说明 |
| --- | --- | --- |
| TrayHider（本项目） | Win10 / Win11 | 按图标隐藏，快捷键可自定义，被动监听 |
| [LCiZY/HideTrayIcon](https://github.com/LCiZY/HideTrayIcon) | Win10 | `ToolbarWindow32` + `TB_DELETEBUTTON`，命令行 |
| [ChisaJiang/TrayIconHider](https://github.com/ChisaJiang/TrayIconHider) | Win10 | 只读查询 + 隐藏按钮状态，无注入 |
| [HCLonely/TrayPilot](https://github.com/HCLonely/TrayPilot) | Win11 | 功能最全，但依赖 WebView2 + 原生模块 |
| [gohide/GoHide](https://github.com/gohide/GoHide) | Win10 / Win11 | 商业闭源（免费 3 个应用），连窗口和任务栏一起隐藏 |
| [Lonely-Atom/HideTaskbar](https://github.com/Lonely-Atom/HideTaskbar) | Win10 / Win11 | 整条任务栏隐藏（不是按图标） |
| [fcFn/traymond](https://github.com/fcFn/traymond) | Win7+ | 把窗口最小化到托盘（不是隐藏托盘图标） |

## 从源码编译

只需要一个 .NET SDK 自带的 Roslyn 编译器（`csc.dll`）和 .NET Framework 4.8 参考程序集
（随 Visual Studio / Windows SDK 安装），不需要安装 .NET 运行时。

```
build.bat
```

产物是单个 `../编译/TrayHider.exe`。

## 许可

MIT，见 [LICENSE](LICENSE)。
