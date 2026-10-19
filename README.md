# 游戏优化助手（MVP）

面向「显存不足 / 卡顿掉帧」的 Windows 桌面诊断工具。只做测量与归因，游戏模式的所有改动均可一键回滚。

## 当前进度

| 模块 | 状态 | 说明 |
|---|---|---|
| 显存 / 内存采集 | ✅ 可用 | WDDM 标准计数器，无需管理员权限 |
| GPU / CPU 单核采集 | ✅ 可用 | 逐核采样，能识别主线程瓶颈 |
| Steam 游戏库扫描 | ✅ 可用 | 解析 `libraryfolders.vdf` + `appmanifest_*.acf`，只读 |
| WeGame 游戏库扫描 | ✅ 可用 | 解析 `rail_apps/名称(ID)`，支持多盘库 |
| 非游戏过滤 | ✅ 可用 | 剔除 Wallpaper Engine / SteamVR 等 |
| 显存达标判定 | ✅ 可用 | 双指标（显存预算占用率 + 系统内存余量） |
| 游戏模式 | ✅ 可用 | 电源计划 + 显卡偏好 + 后台进程，带回滚 |
| 实时曲线 | ✅ 可用 | 帧时间 / 显存 / 内存 / GPU / 单核，保留最近 120 点 |
| 帧时间采集 | ⚠️ 代码就绪，**卡在权限** | 需 PresentMon + ETW 权限，见下 |

## 帧时间为什么还没跑通

代码已按 PresentMon 2.6.0 实测参数接好，但启动 ETW 追踪会话需要**管理员权限**或加入 `Performance Log Users` 组。当前会话令牌是 UAC 降权后的 Medium 完整性级别，无法自行提权（`net localgroup` 与计划任务均被系统拒绝）。

一次性解决办法（管理员 PowerShell，之后注销重登）：

```powershell
powershell -ExecutionPolicy Bypass -File tools/enable-presentmon-etw.ps1
```

撤销：

```powershell
net localgroup "Performance Log Users" <你的用户名> /delete
```

在此之前，数据页的帧时间图会保持空白并显示提示条——**不会**把缺失数据伪装成“帧时间平稳”。

## MVP 的明确边界

**这不是一个「一键提速」工具。** 它的价值是把「显存水位 + 帧时间 + 硬件占用」放进同一条时间轴，告诉你卡顿到底来自哪里。

- 游戏模式只做三类低风险改动，全部可回滚：切高性能电源计划、设显卡性能偏好、结束白名单内的后台进程。
- 不注入游戏进程、不装驱动、不改系统服务。
- 网络延迟监控不在 MVP 范围内（已推迟到 v2）。

## 已知限制

1. **帧时间需要权限**：见上一节。归因引擎在没有帧数据时不会给出“帧时间平稳”这类结论。
2. **显存预算为估算值**：按物理内存的 50% 计算（核显共享上限的经验值），并非系统实际分配量。
3. **仅支持 Steam 与 WeGame**：Epic / Xbox / 独立安装的游戏不会被扫描到。
4. **游戏状态显示「待测」**：当前是全局会话统计，尚未实现按游戏独立统计。
4. **游戏状态显示「待测」**：需要先跑一局采集后才会给出达标结论，目前尚未实现单游戏独立统计。

## 环境要求

- Windows 10/11
- .NET 8 SDK（仅开发时需要）

## 构建与运行

```powershell
dotnet build
dotnet run --project src/GameOptimizer.App
```

## 测试

```powershell
dotnet test
```

135 个用例，覆盖显存判定阈值、帧时间统计与 CSV 解析、归因规则、游戏模式回滚、游戏库扫描与非游戏过滤，以及多个实测踩到的回归问题（单位换算、主显卡选择、powercfg 输出解析、图表坐标计算、vc_redist 识别、路径规范化去重）。

## PresentMon 安装位置

按本项目约定装在 `E:\dsh-插件\PresentMon\`（便携版，不装服务、不进系统目录）：

```
E:\dsh-插件\PresentMon\PresentMon-2.6.0-x64.exe
```

来源：[GitHub Release v2.6.0](https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0)

程序通过 `FrameTimeSource.DefaultPath()` 自动查找，无需配置。

## 项目结构

```
src/GameOptimizer.Core      采集、判定、归因（无 UI 依赖，可单测）
  Collection/               PDH 采样、帧时间源、物理内存信息
  Analysis/                 显存判定、帧时间统计、归因引擎、曲线几何
  Library/                  Steam/WeGame 扫描、非游戏过滤、进程白名单
  GameMode/                 游戏模式动作与总控（带回滚）
src/GameOptimizer.App       WPF 界面（左侧分栏 + 顶部常驻状态栏）
tests/GameOptimizer.Tests   xUnit 用例
tools/                      PresentMon 授权脚本、截图工具
```

## 设计取向

- **失败必须可见**：所有「可回滚的操作」在 Apply 后都会回读系统状态确认真的生效；回滚失败会明确报错，绝不静默显示"已还原"。
- **不可观测的东西不报结论**：没有帧时间数据时不给出"帧时间平稳"这类判断。
