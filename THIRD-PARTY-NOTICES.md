# 第三方许可、署名与合规边界

本项目（GameOptimizer · 游戏优化助手）在设计与实现上参考/使用了下列第三方成果。
本文件用于履行各自的署名与声明义务，同时说明本项目主动划定的技术边界。

---

## 一、PCL（Plain Craft Launcher）

**作者**：龙腾猫跃（Meloong）
**仓库**：<https://github.com/Meloong-Git/PCL>（原地址 `Hex-Dragon/PCL2` 已迁移）
**许可**：作者自定义的《PCL 分发有限许可》与《PCL 存储库合理使用指南》，
**并非 MIT 等标准开源协议**。仓库内许可文件为 `LICENCE`。

**本项目参考了什么**

仅参考其下载器的**交互形态与能力集合**：分片并发、断点续传、下载后校验、
失败重下、任务队列、实时速度显示。即"一个分片下载器应该有哪几件事"，不涉及其实现。

**本项目没有做什么**

- 未复制、未反编译、未移植 PCL 的任何源码，未读取其二进制或中间产物；
- 只使用标准 HTTP/1.1（`GET` + `Range`），不访问任何第三方私有接口，不做镜像加速；
- 不启动 Minecraft、不下载或管理 Minecraft 资源与 Mod、不提供版本隔离与启动参数配置；
- 软件名称、图标、界面风格与 PCL 无关，未使用「PCL」字样命名，不暗示任何关联或官方背书。

**为何属于「轻度使用」**

《PCL 存储库合理使用指南》对「轻度使用」的定义之一是：
*"虽参考了较大量的内容，但制作的软件与 PCL 的实质功能不存在任何重叠。"*

PCL 的实质功能是 Minecraft 启动器（启动游戏、下载游戏资源与 Mod）。本项目是
面向 Windows 通用场景的硬件状态诊断工具，其下载模块为**通用 HTTP 直链下载器**，
两者实质功能无重叠。

**据此履行的义务**

1. **署名**：本条即为署名，同时在各处源码注释（`src/GameOptimizer.Core/Download/Notice.cs`）
   与 `docs/02-功能清单.md` 中重复声明；
2. **不混淆**：软件名称为「游戏优化助手 / GameOptimizer」，界面与发布物中不含
   PCL、Plain Craft Launcher 等字样，不暗示与本项目作者之外的任何关联。

如 PCL 作者对上述判断有任何异议，请通过仓库 Issue 联系，将立即调整或移除相关内容。

---

## 二、Windows 系统 API

本项目大量使用 Windows 公开 API，这些 API 由 Microsoft 以操作系统接口形式提供，
不属于第三方代码复制：

| 用途 | API |
|---|---|
| CPU / 内存 / 磁盘 / GPU 性能计数 | PDH（`PdhOpenQuery` 等）、`System.Diagnostics.PerformanceCounter` |
| 显存容量与占用 | DXGI（`IDXGIAdapter::GetDesc1`） |
| 内存工作集释放 | `EmptyWorkingSet`、`NtSetSystemInformation(SystemMemoryListInformation)` |
| 进程枚举与父进程链 | Toolhelp32（`CreateToolhelp32Snapshot`） |
| 设备枚举与属性 | SetupAPI、Configuration Manager（`CM_Get_DevNode_Property`） |
| 蓝牙外设电量 | `DEVPKEY_Bluetooth_Battery` |
| HID 通信 | `hid.dll`（`HidD_GetProductString`、`CreateFile` + `ReadFile`/`WriteFile`） |
| 电源计划切换 | `powercfg`（系统自带命令行工具） |

> 内存清理与游戏模式所用 API 的调用顺序由 API 语义本身决定。同类工具
> （包括 PCL 的 MemoryOptimizer）都是相同写法，属著作权法上的
> 「唯一或极有限的表达方式」，不构成对任何第三方代码的复制。

---

## 三、外设厂商协议

**VXE / ATK（Compx 方案）鼠标电量读取**

- 通信格式来源于分析厂商**网页驱动**（`hub.atk.pro`，基于浏览器 WebHID）
  与设备之间的公开报文，该驱动由厂商公开发布、任何用户均可在浏览器开发者工具中查看；
- 组包与解析为本项目独立实现，**未复制、未反编译厂商的任何代码或固件**；
- 仅**读取**电量，不写入设备、不修改固件、不绕过任何授权或付费功能；
- 仅在用户本人的计算机、连接用户本人的设备时运行。

**商标声明**：VXE、ATK、Compx、Logitech 等名称与商标归各自权利人所有。
本项目仅作**描述性使用**（说明兼容的设备类型），不代表任何厂商的授权、认证或背书。

**其它厂商**（罗技 HID++ 等）的私有协议本项目未实现，界面上如实显示「未知」，
不猜测、不编造数值。

---

## 四、开源依赖

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| .NET 8 / WPF | 8.0 | MIT | 运行时与 UI 框架 |
| xunit | 2.5.3 | Apache-2.0 | 单元测试框架 |
| xunit.runner.visualstudio | 2.5.3 | Apache-2.0 | 测试运行器 |
| Microsoft.NET.Test.Sdk | 17.8.0 | MIT | 测试宿主 |
| FluentAssertions | 6.12.2 | Apache-2.0 | 断言库（**注意：7.x 起改为商业许可，本项目锁定在 6.12.2**） |
| coverlet.collector | 6.0.0 | MIT | 覆盖率采集 |
| System.Diagnostics.PerformanceCounter | 8.0.1 | MIT | 性能计数器封装 |

**构建期工具（不进入程序产物）**：

| 组件 | 许可 | 用途 |
|---|---|---|
| Inno Setup 6 | Inno Setup License（允许自由使用与再分发） | 生成 Windows 安装程序 |
| `installer/languages/ChineseSimplified.isl` | 随 Inno Setup 源码仓库分发，保留原作者署名 | 安装向导的简体中文界面（取自 `jrsoftware/issrc` 的 `Files/Languages`） |

---

## 五、本项目主动划定的技术边界

以下不是法律要求，是本项目自己定的规矩，写在这里以便他人核查：

1. **不注入游戏进程**、不 Hook 图形 API、不覆盖游戏画面 —— 避免被反作弊误判；
2. **不安装驱动**、不修改系统服务、不静默安装任何组件；
3. **不读写游戏内存**、不修改游戏文件、不做任何形式的游戏辅助；
4. **不伪造设备身份**、不绕过厂商授权，HID 通信只读；
5. 所有会改动系统状态的操作（电源计划、结束进程、释放内存）**均可回滚**，
   回滚结果如实上报，失败不静默。

---

## 六、本项目自身许可

代码以 **MIT** 协议发布，全文见根目录 [`LICENSE`](LICENSE)。
文档（`docs/`）采用 CC BY 4.0。

---

*本文件随代码更新。最后核对日期：2026-10-24。*
