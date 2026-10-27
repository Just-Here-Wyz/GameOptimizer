# 参与开发

这是一个个人独立项目，但**问题反馈和补丁都欢迎**。开始之前请先读三份文档：

| 文档 | 为什么先读 |
|---|---|
| [docs/04-已知限制](docs/04-已知限制.md) | 温度监控、游戏内悬浮窗 / 帧率、加驱改服务都是**明确不做**的，提了会被关掉 |
| [docs/00-项目总览](docs/00-项目总览.md) | 七个核心机制与 13 条硬约定，每条都对应一次真实事故 |
| [docs/05-开发指南](docs/05-开发指南.md) | 环境、命令、调试与踩过的坑 |

## 报问题

用 [Bug 反馈模板](../../issues/new?template=bug_report.yml)，它要求填版本、是否管理员、硬件与
`%LocalAppData%\GameOptimizer\restore-log.txt` 的摘录——这个工具的原则是「读不到就说读不到」，
没有现场信息很难判断是采不到还是算错了。

**贴日志前请删掉本机用户名与软件路径**，不要把访问令牌、密码之类的凭据放进 issue。

## 本地构建

需要 Windows + .NET 8 SDK。

```powershell
dotnet build
dotnet test                                                   # 全量用例，改完必须全绿
dotnet run --project src/GameOptimizer.App
pwsh -File tools\build-app.ps1                                # 编译 Release 到快捷方式指向的那一份
```

打包安装程序另需 [Inno Setup 6](https://jrsoftware.org/isdl.php)，然后：

```powershell
pwsh -File installer\build-installer.ps1                      # 单文件 exe + 安装包 + SHA256SUMS
```

## 改代码前请确认这几条

来自 [docs/00 第七章](docs/00-项目总览.md)，不是风格偏好：

1. **失败必须可见**：不许 `catch { }` 吞掉；改系统的操作在 `Apply()` 之后必须回读确认。
2. **不可观测的不报结论**：读不到就是「未知」，不要用 0 顶替。
3. **Core 不引用 WPF**：能单测的逻辑一律放 `GameOptimizer.Core`（`ProjectLayoutTests` 会拦）。
4. **口径与实现分离**：判定阈值（多少算紧张、什么颜色算红）要落在独立纯类里并有测试钉住。
5. **破坏性操作默认关闭**：白名单默认全不勾，任何「默认帮你关掉什么」的改动都不接受。
6. **不注入、不加驱、不改系统服务**：这条没有例外。
7. **每次改动都要有测试**，`dotnet test` 全绿才算完成。

## 提交补丁

- 提交信息用中文，前缀 `fix:` / `feat:` / `docs:` / `refactor:` / `chore:`，一行说清「改了什么」；
  根因、取舍写进提交正文或 `docs/06-变更记录.md`。
- 版本号**不要手改**：跑 `pwsh -File tools\bump-version.ps1 -Kind fix|feature|major`，
  它会同步 `Directory.Build.props`、测试断言、文档标题与两份 README
  （规则见 [docs/03-版本规则](docs/03-版本规则.md)）。
- 源码推送 `main` 会触发 CI（`windows-latest`：Release 构建 + 926 例测试）；
  打 `v*` tag 会额外触发 Release 工作流，自动打包单文件 exe 与安装包并附到 Release 上。
- **不要提交** `publish/`、`bin/`、`obj/` 与任何运行时产物，`.gitignore` 已经挡住；也不要提交凭据。

## 关于 AI 协作

本项目的编码与测试用例大量借助 AI Agent 完成，但需求拆解、取舍与验收由人负责。
如果你用 AI 生成补丁，请自己读一遍再提交——这个项目踩过「伪修复」和「多真源不收敛」两个坑，
完整复盘在 [docs/07-AI协作复盘](docs/07-AI协作复盘.md)。
