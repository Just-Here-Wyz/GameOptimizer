# 项目约定

## 先读什么

动任何代码之前，先读 [`docs/00-项目总览.md`](docs/00-项目总览.md)：
里面有技术栈、现有功能、七个核心机制、13 条硬约定、常见改动步骤和已知的坑。
需要更细的分工说明时再看 `docs/01` ~ `docs/07`。

## 注意：版本与「发布没发布」不要看散文

这个仓库踩过坑：`docs/00` 曾长期写着「尚未打包发布、最近发布 v2.4.1」，而实际上
v2.5.2 的 Release 早已挂好附件。多个会话照 `AGENTS.md` 的要求去读 `docs/00`，
读到的第一个结论就是错的，于是反复得出「GitHub 上没有最新版本」的判断。

判断版本与发布状态，只认这三个可验证的来源：

| 要判断什么 | 看哪里 |
|---|---|
| 源码版本号 | `Directory.Build.props` 的 `<Version>`（唯一真源） |
| 最近发布了哪一版 | 根 `README.md` 顶部 **版本状态** 行（`tools/bump-version.ps1` 按 Git tag 自动填） |
| 远程到底有没有出包 | `git ls-remote --tags origin`，或 GitHub Releases 页面里那个 tag 有没有附件 |

**注意「源码有 tag」不等于「有 Release 附件」**：v2.4.1 与 v2.5.0 都曾有过 tag 却没有任何
Release，`releases/tag/vX.Y.Z` 返回的是 tag 页而不是 404，光看链接状态码判断不出来。

文档里不许再写「尚未打包发布」「最近发布 vX.Y.Z」这类会过期的句子 ——
`DocsFreshnessTests` 会直接让构建变红。

## 注意：用例数只有一个真源

根 `README.md` 工程概况表里的「N 个 xUnit 用例」是真源；`docs/00`、`docs/01`、
`docs/05`、`CONTRIBUTING.md` 里写的用例数必须与它相等，`DocsFreshnessTests` 会比对。
`docs/06`（变更记录）与 `docs/07`（四天复盘）里的是历史快照，不参与这条校验。

## 注意事项

1. 每次改动完成后，都必须创建一个对应的 Git commit，以便后续追踪和回滚。
2. 每次改动后，都必须编写或更新相关测试，并在交付给用户前，确保所有测试和验证全部通过。
3. **用户实际运行的是 Release 版**：桌面快捷方式指向
   `src/GameOptimizer.App/bin/Release/net8.0-windows/GameOptimizer.App.exe`。
   交付前必须跑 `pwsh -File tools/build-app.ps1` 原地编译这一份；
   只编 Debug、只编到 `.artifacts`、或只提交代码，用户点开都还是旧版本，
   界面上什么都不会变（v2.5.1 当天因此白折腾两轮）。
   程序在运行时输出目录被锁（MSB3027），要先请用户点 × →「退出程序」再编。
