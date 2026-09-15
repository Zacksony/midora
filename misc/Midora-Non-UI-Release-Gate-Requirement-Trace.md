# Midora 初版非 UI 发布门 Requirement Trace

状态：已实现自动门；人工/外部门未全部闭合

上位规范：《Midora SRS》§21.3、§21.5、§21.6，INV-027、INV-028、INV-037、INV-038

执行入口：仓库根目录 `Test-NonUIRelease.ps1`

## 1. 输入

- 仓库固定 `.NET SDK 10.0.400`、`global.json`、38 个 `packages.lock.json` 和六个 `.slnx`。
- 操作员显式提供的 win-x64 BASS 原生目录；必须逐文件匹配 `bass-native-baseline.win-x64.json`。
- 操作员显式提供的现存 SF2，仅作为真实 BASS/BASSMIDI 集成测试资源，不进入仓库或正式产品默认值。
- `misc/Midora-Non-UI-Test-Baseline.json` 固定的十个非 UI 测试项目、逐项目计数与总计数。
- `eng/Version.props`、`Test-VersionControl.ps1` 与冻结 Project Format 1 基线；进入 restore/build 前先验证产品/程序集/文件版本唯一性及 V1 contract 常量。

## 2. 正式自动输出

- 六个 solution 的 Release 构建证据，全部启用 warnings-as-errors 与 CI deterministic build 属性。
- 一个本次运行唯一目录中的 `win-x64` Native AOT Worker 本地测试产物；必须包含 Worker `.exe`、三项固定 BASS DLL、`native-manifest.json`、根 MIT `LICENSE` 与 `THIRD-PARTY-NOTICES.md`。
- 十个测试项目各自的 TRX；当前基线精确为 848 tests，全部 passed、零 failed、零 notExecuted/Skip。
- 非零退出码或结构化 PowerShell 异常作为门失败；成功时打印产物位置，并再次声明 BASS 分发授权仍是独立门。

## 3. 边界与失败条件

- SDK 版本不精确等于基线、locked restore 不一致、测试项目/数量增减但未显式更新基线、任何 Skip/失败、任一 solution 构建失败，整门失败。
- BASS 目录缺失、多余 DLL、版本/hash/manifest/架构不符，进入构建前失败；不得自动下载或采用 vendor latest。
- SF2 缺失时失败，不把真实音频集成测试静默降级为 Skip。普通开发测试允许通过统一环境助手报告 Skip，使“环境未配置”与“代码回归”可区分。
- Worker publish 必须实际出现 Native AOT `.exe`；仅有托管 DLL/self-contained runtime 视为失败。缺少 manifest、MIT License 或 notices 同样失败。
- 产物只写入调用方指定的尚不存在目录或 `artifacts/non-ui-release-gate-<GUID>`；目标已存在即失败，不删除既有目录，不覆盖正式发布目标，不修改 Project 源数据。

## 4. 诊断、持久化与运行时归属

- SDK、restore、build、publish、test 和 TRX 计数错误直接标明失败阶段、项目/文件和期望/实际值。
- lock files、SDK/RID 策略和测试基线属于仓库构建契约；TRX、AOT 二进制与本地 BASS DLL只属于本次验证产物并由 `.gitignore` 排除。
- 环境变量 `MIDORA_BASS_NATIVE_DIR`、`MIDORA_TEST_SOUNDFONT_PATH`、`MIDORA_TEST_NATIVE_AOT_FILE_WORKER` 只在发布门进程中设置，不写入 Project/Application Preferences。

## 5. 明确非目标与剩余门

- 自动门不构建尚未进入本轮范围的 WPF UI/主应用发布包。
- 自动门不证明物理设备移除、默认设备变化、callback deadline、约 200 ms 端到端延迟或人耳听感；这些进入统一人工/硬件验收。
- 自动门不构成 BASS 重新分发许可判断，也不核验发布主体、收入、渠道或发布日条款。含 DLL 的正式对外分发等待 Q-NUI-013；本地产物只能用于开发和验收。
- SDK/lock 精确策略是已实施待确认的小决定 Q-NUI-012；共享状态快照 ABI v2 仍等待 Q-NUI-011。

## 6. 已执行证据

2026-08-06 在 Windows win-x64 上运行：

```powershell
.\Test-NonUIRelease.ps1 `
  -BassNativeDirectory "$env:LOCALAPPDATA\Midora\Native\BASS\win-x64" `
  -SoundFontPath "D:\Soundfonts\sf2\sDetrimental Concert Grand Piano.sf2"
```

结果：固定 BASS baseline 通过；六个 Release solution 0 warning / 0 error；生成 Native AOT Worker；10 个测试项目 848/848 passed、0 skipped。最近一次完整产物位于 `artifacts/non-ui-release-gate-cfd2b382b66b4e238fccaaa1d74e9094/`，只用于本地验证且被 Git 排除。
