# KeepAwake（Windows 定时防休眠小工具）

一个轻量级、单文件零依赖的 Windows 桌面防休眠工具，用于在指定星期与时间段内阻止系统自动休眠和屏幕关闭。

## 功能特性

- **按星期与时间段生效**：支持周一至周日自由勾选，支持自定义起止时间（`HH:mm`），兼容跨午夜时段。
- **可控的设置持久化**：默认不写入系统配置；勾选“记住设置（写入注册表）”并点击确定后，配置保存至当前用户注册表（`HKCU\Software\SimpleKeepAwake08301730`），下次启动自动恢复；若取消勾选或写入失败则仅在当前运行内存中生效。
- **状态来源一目了然**：界面实时显示当前状态与配置来源（如“默认时间”、“已保存设置”或“临时生效”）。
- **低开销与托盘驻留**：每 15 秒检测一次状态；支持单实例防多开、最小化隐藏至系统托盘，退出时自动恢复系统默认电源策略。
- **广泛兼容**：基于 Windows 自带 .NET Framework 编译，兼容 Windows 7 / Windows 10 / Windows 11。

## 快速编译（无需安装 Visual Studio / .NET SDK）

Windows 系统内置 C# 编译器 `csc.exe`，在源码目录打开 PowerShell 执行以下命令即可生成单个 `.exe`：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /out:KeepAwake.exe /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll /optimize+ KeepAwake.cs