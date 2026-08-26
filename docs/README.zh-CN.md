<p align="center">
  <img src="../assets/docs/readme/midora-banner.svg" alt="Midora — MIDI 1.0 事件乐器环境" width="100%">
</p>

<p align="center">
  <a href="./README.md">English</a> ·
  <a href="./README.zh-CN.md">简体中文</a> ·
  <a href="../README.md">仓库首页</a>
</p>

<p align="center">
  <img alt="状态：1.0.0-dev" src="https://img.shields.io/badge/status-1.0.0--dev-d52b37?style=flat-square">
  <img alt="平台：Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-17191f?style=flat-square&logo=windows11&logoColor=white">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512bd4?style=flat-square&logo=dotnet&logoColor=white">
  <img alt="MIDI 1.0" src="https://img.shields.io/badge/MIDI-1.0-17191f?style=flat-square">
  <a href="../LICENSE"><img alt="许可证：MIT" src="https://img.shields.io/badge/license-MIT-d52b37?style=flat-square"></a>
</p>

# Midora

**设计可复用的 MIDI 事件乐器，使用逻辑轨道或直接 MIDI 轨道编曲，再将全部内容确定性地编译为唯一正式结果。**

Midora 是面向 MIDI 1.0 的 Windows 桌面编曲与编译环境。它用于把密集且具有状态的 MIDI 事件序列组织成可复用的 **Event Instrument（事件乐器）**；需要底层控制时，也可以使用直接编辑 MIDI 数据的 **Pure MIDI Track（纯 MIDI 轨道）**工作流。

> [!IMPORTANT]
> Midora 当前处于 `1.0.0-dev` 活跃开发阶段，尚无稳定的终端用户版本。下文描述的是初版目标范围，并不表示当前提交已经完成所有能力。

## 为什么开发 Midora？

一个由 MIDI 驱动的声音可能同时包含 Program 与 Bank 切换、Pitch Bend Range、控制器曲线、RPN/NRPN 状态、音符、Release 行为和清理事件。手工复制整组事件不仅重复，也很容易引入 Channel 状态冲突。

Midora 将这套工作流组织为可复用系统：

```text
Event Instrument 定义 ──► Logical Track ──┐
                                           │
SMF 导入 / 直接 MIDI ─────► Pure MIDI Track ├─► 语义验证
                                           │       │
                                           └───────▼
                                                Canonical 编译
                                                      │
                                   ┌──────────┬────────┴────────┐
                                   ▼          ▼                 ▼
                                  播放      MIDI 导出         音频渲染
```

播放、预览、MIDI 导出和音频渲染共用同一个 Canonical Compiled Result，不会各自重新解释 Project。

## 初版目标方向

- 使用 Note、MIDI Event、SubVoice、Logical Parameter、Mapping、生命周期、Loop、Envelope 和重叠策略构建可复用的 Event Instrument。
- 既可使用高层 Logical Track 编曲，也可在 Pure MIDI Track 中直接编辑 MIDI 1.0 Note 与 Channel Event。
- 以确定性规则分配最多 16 Port × 16 Channel，并明确处理生命周期和 Reset 边界。
- 打开采用 TPQN 时间基准的 SMF 1.0 Format 0/1 文件，并导出确定性的 SMF Type 1 文件。
- 通过程序级有序 SF2/SFZ 列表播放和预览，并渲染 stereo IEEE float32 RIFF/WAVE 音频。
- 将源 Project 保存为 `.midora` package，不写入编译结果、缓存、导出产物或会话 UI 状态。

Midora 明确**不是** DAW、VST 宿主、音频录制工具、MIDI 2.0 工具，也不试图替代完整的通用 MIDI 工作站。

## 平台与前置条件

| 要求 | 初版基线 |
|---|---|
| 操作系统 | Windows Desktop，x64 |
| 运行时与 UI | .NET 10、WPF |
| 仓库 SDK | .NET SDK `10.0.302`，由 [`global.json`](../global.json) 固定 |
| MIDI | MIDI 1.0 |
| 音频资源 | 用户自行提供的 SF2/SFZ 文件 |
| 原生音频后端 | 固定版本的 x64 BASS、BASSMIDI 和 BASSWASAPI 二进制文件 |

本仓库**不保存** BASS 二进制文件或 SoundFont。没有 Enabled SoundFont 时仍可打开、保存、编译和导出 Project；播放、预览和音频渲染则需要至少一个 Enabled SF2/SFZ。

## 从源码构建

安装仓库固定的 .NET SDK 和 Git LFS，然后克隆并构建桌面端 solution：

```powershell
git clone https://github.com/Zacksony/midora.git
cd midora
git lfs pull
dotnet restore src/midora-desktop/midora-desktop.slnx --locked-mode
dotnet build src/midora-desktop/midora-desktop.slnx -c Release --no-restore
```

如需随正式音频 Worker 启动桌面应用，请合法取得精确版本的 x64 BASS 二进制文件，并将其目录传给仓库运行脚本：

```powershell
.\Run-MidoraDesktop.ps1 -Configuration Release -BassNativeDirectory "C:\path\to\bass\win-x64"
```

脚本会依据 [`bass-native-baseline.win-x64.json`](../src/midora-audio/bass-native-baseline.win-x64.json) 固定的完整版本和 SHA-256 逐文件校验；缺失或版本不同的二进制文件会被拒绝。

## 文档

| 文档 | 说明 |
|---|---|
| [`README.md`](./README.md) | 英文 README |
| [`README.zh-CN.md`](./README.zh-CN.md) | 简体中文 README |
| [《Midora SRS》](../misc/Midora-SRS-Initial-Release-v0.1/00-Table-of-Contents-and-Document-Control.md) | 初版范围的正式需求基线 |
| [实施路线图](../misc/Midora-Implementation-Roadmap.md) | 工程顺序与实现现状记录；与 SRS 冲突时以 SRS 为准 |

## 许可证与第三方组件

Midora 自有源代码使用 [MIT License](../LICENSE)，版权声明为 Copyright © 2026 Midora contributors。项目官方初版定位为免费、开源、非商业软件；该定位不会对标准 MIT 条款增加额外限制。

BASS、BASSMIDI、BASSWASAPI、用户提供的 SoundFont 及其他第三方材料保留各自许可证，不会被 Midora 重新许可。再分发或商业使用前，请阅读 [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) 并核验届时有效的 [Un4seen BASS 授权条款](https://www.un4seen.com/bass.html)。

---

<p align="center"><sub>以 MIDI Event 为材料，以确定性编译塑造乐器。</sub></p>
