<p align="center">
  <a href="./README.md">English</a> ·
  <a href="./README.zh-CN.md">简体中文</a> ·
  <a href="../README.md">仓库首页</a>
</p>

<p align="center">
  <img alt="状态：1.0.0-dev" src="https://img.shields.io/badge/status-1.0.0--dev-d52b37?style=flat-square">
  <img alt="平台：Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-17191f?style=flat-square&logo=windows11&logoColor=white">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512bd4?style=flat-square">
  <img alt="MIDI 1.0" src="https://img.shields.io/badge/MIDI-1.0-17191f?style=flat-square">
  <a href="../LICENSE"><img alt="许可证：MIT" src="https://img.shields.io/badge/license-MIT-d52b37?style=flat-square"></a>
</p>

# Midora

**把一套 MIDI 音色事件设计一次，然后像使用普通乐器一样反复调用。**

Midora 面向喜欢用 MIDI 本身塑造声音的用户。如果你用过普通 MIDI 编辑器，应该很熟悉这个问题：一个自制声音往往不只是一个 Note，而是一组精心对齐的 Note、控制器曲线、Program Change、Pitch Bend 和 Reset；每次再次使用这个声音，都要把整组事件重新复制一遍。

> [!IMPORTANT]
> Midora 当前处于 `1.0.0-dev` 活跃开发阶段，尚无稳定的终端用户版本，部分界面和工作流在 1.0 前仍可能调整。

## 目录

- [一个 Kick 背后有多少 MIDI 事件](#一个-kick-背后有多少-midi-事件)
- [声音只设计一次](#声音只设计一次)
- [让事件乐器拥有自己的演奏方式](#让事件乐器拥有自己的演奏方式)
- [把底层 MIDI 变成自己的参数](#把底层-midi-变成自己的参数)
- [让 Midora 接管 Port 和 Channel](#让-midora-接管-port-和-channel)
- [那我为什么不直接用 DAW？](#那我为什么不直接用-daw)
- [关于黑乐谱性能](#关于黑乐谱性能)
- [平台与前置条件](#平台与前置条件)
- [从源码构建](#从源码构建)
- [文档](#文档)
- [特别鸣谢](#特别鸣谢)
- [许可证与第三方组件](#许可证与第三方组件)

## 一个 Kick 背后有多少 MIDI 事件

假设一个 Kick 由两部分组成：**Sine** 负责低频主体，很短的 **Click** 负责起音。Sine 的音高要快速下坠，Click 则有自己的音量变化。在普通 MIDI 编辑器中，这个看似简单的声音可能需要两个 Channel，以及一大堆 Note、控制器、Pitch Bend 和初始化事件。

做出第一声并不难，真正麻烦的是把它复制成一整段鼓点之后：想换掉 Click、调整音高下坠，或再增加一层声音，就得找到并修改每一份副本。只漏掉一条事件，某一次击打就可能和其他位置听起来不同。

## 声音只设计一次

在 Midora 中，你只需把 Sine + Click Kick 设计一次，并保存成一个 **Event Instrument（事件乐器）**——一件完全由 MIDI 事件构成、可以反复使用的自制乐器。之后每一次 Kick 都像普通 Note 一样写在轨道上。

```text
设计一次 Sine + Click 声音
            ↓
每次 Kick 只放置一个简单 Note
            ↓
Midora 自动展开为完整的 MIDI 事件序列
```

修改一次乐器，所有使用它的位置都会采用新的设计。只有确实需要变体——例如更软或尾音更长的 Kick——才需要复制一份新的乐器定义。

开始使用时只需认识三个概念：

- **Event Instrument（事件乐器）**：可复用的声音设计，例如上面的 Sine + Click Kick。
- **Logical Track（逻辑轨道）**：用看起来和普通音符一样的 Note 演奏事件乐器。
- **Pure MIDI Track（纯 MIDI 轨道）**：像传统 MIDI 编辑器一样，直接编辑 Note 和其他 MIDI 事件。

两种轨道可以放在同一个 Project 中：重复编辑很麻烦的地方使用 Event Instrument，需要直接控制时继续使用 Pure MIDI Track。Midora 会把两者展开为标准 MIDI 1.0 数据用于播放和导出。

## 让事件乐器拥有自己的演奏方式

Event Instrument 不只是原样重放一段固定事件。它还可以描述声音由什么组成，以及按下、长按、松开和重叠演奏时应该如何变化：

- **分层构造声音**：Sine、Click、Noise 等部分都可以拥有自己的 Note、音色选择、Pitch Bend、控制器和曲线，但编曲时仍作为一件乐器使用。Midora 把每一层称为 **SubVoice（子声部）**。
- **自定义 Initial State（起始状态）**：整件乐器或单独一层都可以指定开始时需要的 Bank/Program、Expression、Pitch Bend、弯音范围和其他控制器状态。例如，Sine 可以从居中 Pitch Bend 和较大的弯音范围开始，而 Click 自动选择另一种音色和音量。
- **决定短音和长音如何工作**：松开短 Note 时，可以立即进入结束流程、完整播放为 One-shot，或继续执行尾部事件；长 Note 则可以简单地保持最后状态。
- **用 Loop 维持长音**：选择声音中间的一段，在 Note 被按住时不断循环；松开后退出 Loop 并进入收尾。所有 SubVoice 始终对齐，不需要手工复制循环内容。
- **塑造起音与释放**：可复用的 ADSR 风格 Envelope 还提供 Delay 和 Hold 阶段，并且不只控制音量。松开 Note 后，它可以让 Expression 渐弱、让 Pitch Bend 回到中心，或继续改变其他控制器，最后再真正发送 Note Off。
- **处理重叠音符**：如果两个 Note 各自需要独立的 Pitch Bend 或控制器变化，Midora 可以隔离它们使用的 Channel 状态，避免互相干扰。

例如，一个持续音效可以只播放一次开头，在 Note 被按住时循环中间部分；松开后立即退出 Loop，再沿着 Release Envelope 收尾。所有 SubVoice 共用这套演奏过程，不必在每条轨道中重新搭建。

## 把底层 MIDI 变成自己的参数

**Logical Parameter（逻辑参数）**是事件乐器选择暴露给编曲界面的控制项。它并不是一组固定旋钮：乐器作者可以自行决定参数的名称、默认值和范围，也可以决定它是整数、小数，还是 `Soft / Hard` 这样的选项。

仍以 Kick 为例，可以只向外提供一个 `Punch（冲击感）` 参数，范围为 `0–100`。提高它时，可以同时增强 Click、加深 Sine 的音高下坠，并调整两个声部的 Expression。编曲者只需要编辑 `Punch`，不必知道背后改动了多少条 MIDI 事件。

简单需求可以使用范围变换和曲线；更复杂的行为可以交给 **Mapping Function（映射函数）**。它是一条简短的数值表达式，可以根据当前参数值和演奏环境计算结果，例如读取本次 Note 的音高、力度、长度或它在乐曲中的位置。于是，同一个 `Punch` 值也能随按键力度产生不同反应。事件乐器对外保持简单，内部的映射方式则可以高度自定义。

## 让 Midora 接管 Port 和 Channel

使用 Event Instrument 时，用户不需要为每个 SubVoice 选择 Port 或 Channel。Midora 会自动寻找所需 Channel，必要时跨 Port 分配，在安全时复用已经释放的资源，并避免无关声音继承彼此的 Pitch Bend 或控制器状态。对于通常的 Logical Track 编曲，用户基本可以忘掉 Channel 的存在。

如果多个 Logical Track 本来就属于同一件共享状态的乐器——例如两条旋律线需要共用 Sustain 和 Expression——可以把它们显式组成一组。在共享状态模式下，Midora 会让它们固定使用同一个 Channel Group；需要独立的轨道即使引用同一乐器设计，也仍会保持隔离。

Pure MIDI Track 则保留熟悉的下层选择：既可以让 Midora 自动分配，也可以把一条轨道——或共享同一路由的一组轨道——固定到明确的 `Port.Channel` 地址。Midora 最多支持 **16 Ports × 16 Channels = 256 Channel Units**。如果超出上限，编译会明确失败，而不会静默丢弃、抢占或截短 Note。

## 那我为什么不直接用 DAW？

Midora 无意取代 DAW。它的设计目的，是探索完全由 MIDI 1.0 事件构成的编曲究竟能走多远。DAW 提供更大的自由度；Midora 提供的是另一种乐趣——在纯 MIDI 的限制内尽可能做得更多。

## 关于黑乐谱性能

Midora 对黑乐谱流畅编辑的支持目标是百万 Note 级别，即 **少于 10,000,000 个 Note**。

Midora 可以打开包含数千万乃至上亿 Note 的黑乐谱，并通过分页数据和按可见区域渲染，让密集音符浏览尽量保持顺畅。但超出上述支持范围后，Midora 不保证编辑速度和内存占用能够满足你的具体需求。

如果你的主要目标是编辑更高规模的黑乐谱，也请关注这两个针对该类工作负载提供便利和优化的项目：

- [yinhe](https://github.com/BuickMeow/yinhe)
- [lumino-rs](https://github.com/PenguinBMDevs/lumino-rs)

## 平台与前置条件

| 要求         | 初版基线                                                     |
| ------------ | ------------------------------------------------------------ |
| 操作系统     | Windows Desktop，x64                                         |
| 运行时与 UI  | .NET 10、WPF                                                 |
| 仓库 SDK     | .NET SDK `10.0.302`，由 [`global.json`](../global.json) 固定 |
| MIDI         | MIDI 1.0                                                     |
| 音频资源     | 用户自行提供的 SF2/SFZ 文件                                  |
| 原生音频后端 | 固定版本的 x64 BASS、BASSMIDI 和 BASSWASAPI 二进制文件       |

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

| 文档                                                                                                   | 说明                                             |
| ------------------------------------------------------------------------------------------------------ | ------------------------------------------------ |
| [`README.md`](./README.md)                                                                             | 英文 README                                      |
| [`README.zh-CN.md`](./README.zh-CN.md)                                                                 | 简体中文 README                                  |
| [《Midora SRS》](../misc/Midora-SRS-Initial-Release-v0.1/00-Table-of-Contents-and-Document-Control.md) | 初版范围的正式需求基线                           |
| [实施路线图](../misc/Midora-Implementation-Roadmap.md)                                                 | 工程顺序与实现现状记录；与 SRS 冲突时以 SRS 为准 |

## 特别鸣谢

特别感谢 [BuickMeow](https://github.com/BuickMeow) 与 [Enderman-bm](https://github.com/Enderman-bm) 在 Midora 开发期间提供的性能相关建议。

Midora 的密集音符渲染性能优化思路参考了 [yinhe](https://github.com/BuickMeow/yinhe)。

## 许可证与第三方组件

Midora 自有源代码使用 [MIT License](../LICENSE)，版权声明为 Copyright © 2026 Midora contributors。项目官方初版定位为免费、开源、非商业软件；该定位不会对标准 MIT 条款增加额外限制。

BASS、BASSMIDI、BASSWASAPI、用户提供的 SoundFont 及其他第三方材料保留各自许可证，不会被 Midora 重新许可。再分发或商业使用前，请阅读 [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) 并核验届时有效的 [Un4seen BASS 授权条款](https://www.un4seen.com/bass.html)。

---

<p align="center"><sub>设计一次，自由编曲，导出标准 MIDI。</sub></p>
