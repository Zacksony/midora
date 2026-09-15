# Midora 发布检查清单

最近核对：2026-09-09，产品源码基线 `560082e9`。本表是每个正式候选需重新核验的模板；未勾选不等于相应能力尚未实现，已完成的开发期总体人工验收也不等于正式发布放行。用户计划在真实完整编曲时集中修复 Bug 和查漏补缺。本次仅同步文档，不运行发布流程、创建 tag 或更新 `dist`。

## 1. 版本冻结

- [ ] `eng/Version.props` 是唯一产品版本源。
- [ ] 开发基线为 `1.0.0-dev`；RC 使用 `1.0.0-rc.N`；正式版清空 suffix 得到 `1.0.0`。
- [ ] 运行 `./Test-VersionControl.ps1 -ExpectedVersion <version>`。
- [ ] EXE 的 Product/File/Informational Version 与预期一致。
- [ ] Project manifest 和 MIDI Export Readme 使用同一 Informational Version。
- [ ] `CHANGELOG.md` 已把 Unreleased 内容归档到本次版本和发布日期。

## 2. Project Format 兼容

- [ ] `PersistenceContractV1.FileFormatVersion == 1`，且没有原地改变 Format 1 schema/field semantics。
- [ ] 当前 writer 使用 `PersistenceContractV3.FileFormatVersion == 3` / manifest schema 3；Event Instrument 继续复用 v2 表示，其他组件按各自冻结版本读取。
- [ ] 独立 `settings/project-presentation.json` 使用 schema 2；schema 1 显式读取为 custom 来源模式，音乐内容不依赖视图配置。
- [ ] JSON schema set SHA-256、protobuf descriptor hash 与代表性 golden wire bytes 全部通过。
- [ ] 1.0.0-dev 期间保存的真实完整作品能够由候选版本打开、编译、编辑、保存副本并再次打开。
- [ ] Save Copy 不改变来源或清除 migration-dirty；Format 1/2 迁移会话普通 Save 经确认、冻结副本路径、逐字节一致的可见永久旧版副本和临时包重开校验后，才可原路径原子升级保存。
- [ ] Format 1/2 reader、schema、descriptor 和 golden 持续保留；原路径升级的取消、来源 identity 改变、备份路径冲突及发布失败均不改变来源。
- [ ] 未来 Project Format 及未知音乐组件 schema 在 Project 提交前明确拒绝；未知/损坏的独立 presentation 按隔离规则告警并回退默认视图，不阻止合法音乐内容加载。

## 2.1 程序数据与 portable 目录

- [ ] 正式数据位于 `<ProgramRoot>\Data`，可重建数据位于 `<ProgramRoot>\.tmp`；配置、Preset、Catalog、Diagnostics 与各 owned session 子目录按当前契约分类。
- [ ] 不可写、非本机 fixed drive、reparse-point 或能力探测失败时安全拒绝启动；不 fallback，不探测、迁移或删除旧 `%LOCALAPPDATA%\Midora`。
- [ ] 清理只作用于可证明归属 Midora、非活动且通过路径/manifest/独占锁检查的 session；不删除用户音乐、SoundFont、未知目录或另一活动 portable 副本的数据。

## 3. 自动验证

- [ ] locked restore、warnings-as-errors、deterministic Release build 通过。
- [ ] Persistence、Compiler、Application、MIDI Import/Export、Playback、Audio Render 和 Desktop tests 全通过且无意外 Skip。
- [ ] `Test-NonUIRelease.ps1` 使用固定 .NET SDK、BASS baseline 和真实 SF2 通过。
- [ ] Native AOT `win-x64` Worker 与主程序版本/协议匹配。
- [ ] 大型 MIDI、完整 Midora 作品、连续播放/停止、Mute/Solo、预览、MIDI Export 与 Audio Render 完成人工验收。

## 4. 分发与许可

- [ ] 根 MIT `LICENSE` 和 `THIRD-PARTY-NOTICES.md` 随产物发布。
- [ ] Sora、JetBrains Mono、Fluent System Icons、AvalonEdit、Roslyn、Google.Protobuf 与精确 .NET runtime-pack notices/许可证完整。
- [ ] 终端用户包不包含内部 `Schemas` 或 `release-manifest.json`。
- [ ] BASS/BASSMIDI/BASSWASAPI 版本和 SHA-256 匹配固定 baseline。
- [ ] 发布主体、收入方式、渠道和发布日 BASS 条款已经重新核验；未满足时不分发 BASS DLL。
- [ ] 正式 ZIP/安装产物生成 SHA-256，且从空目录完成一次启动/项目重开验证。

## 5. GitHub Release

- [ ] 使用 `./Publish-MidoraLocal.ps1` 生成并人工检查本地 self-contained `win-x64` 目录、ZIP、单层 `midora/` 结构和 SHA-256；正式候选不得带 `-dirty`。
- [ ] 运行 `git status --short`，工作区干净。
- [ ] 创建 annotated tag：`git tag -a v<version> -m "Midora <version>"`。
- [ ] 运行 `./Test-VersionControl.ps1 -ExpectedVersion <version> -RequireClean -RequireTagAtHead`。
- [ ] 推送 commit 和 tag；不得移动或复用已发布 tag。
- [ ] GitHub Release 附带产物、SHA-256、Release Notes、兼容说明和已知问题。
