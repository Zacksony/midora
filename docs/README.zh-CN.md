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

- [一个熟悉的例子：用 Sine + Click 制作 Kick](#一个熟悉的例子用-sine--click-制作-kick)
- [Midora 如何改变这套工作流](#midora-如何改变这套工作流)
- [不只是事件模板：一件完整的 MIDI 乐器](#不只是事件模板一件完整的-midi-乐器)
- [无需管理 Port 和 Channel](#无需管理-port-和-channel)
- [那我为什么不直接用 DAW？](#那我为什么不直接用-daw)
- [关于黑乐谱性能](#关于黑乐谱性能)
- [平台与前置条件](#平台与前置条件)
- [从源码构建](#从源码构建)
- [文档](#文档)
- [特别鸣谢](#特别鸣谢)
- [许可证与第三方组件](#许可证与第三方组件)

## 一个熟悉的例子：用 Sine + Click 制作 Kick

假设你想用两个声部做一个 Kick：

- **Sine** 负责低频主体；
- 很短的 **Click** 负责起音瞬态。

在普通 MIDI 编辑器中，你可能要创建两个 Channel，分别设置 Bank/Program 和 Pitch Bend Range，画出音高下坠与 Expression 曲线，对齐两个 Note，再补上必要的状态清理事件。做一个 Kick 尚可接受，但写一整段鼓点时，每一次击打都要复制这两大组事件。

问题随之而来：修改声音时要找到并更新每一份副本；漏掉一个事件就可能让某次击打听起来不同；快速连打时，Pitch Bend 或 CC 状态可能污染下一次击打；增加声部后，还要手动规划更多 Channel。

## Midora 如何改变这套工作流

在 Midora 中，你只需把 Sine + Click Kick 设计一次，并保存成一个 **Event Instrument（事件乐器）**——也就是一份完全由 MIDI 事件组成、可以反复调用的“声音配方”。之后便可以像编写普通 Note 一样，在轨道中反复使用它。

```text
设计一次 Sine + Click 配方
            ↓
每次 Kick 只放置一个简单 Note
            ↓
Midora 自动展开为完整的 MIDI 事件序列
```

编曲时，你操作的是简洁的 Note，而不是反复复制的大量事件。Midora 会展开这些 Note，分配所需的 Port 和 Channel，并处理事件顺序与清理边界。需要改变声音时，只修改一次配方，整段编曲即可使用更新后的设计，不必逐份寻找事件副本。

理解 Midora 只需要先认识三个概念：

- **Event Instrument（事件乐器）**：可复用的 MIDI 事件配方，例如上面的 Sine + Click Kick。
- **Logical Track（逻辑轨道）**：放置简洁 Note、调用 Event Instrument 的轨道。
- **Pure MIDI Track（纯 MIDI 轨道）**：不经过可复用抽象，像普通 MIDI 编辑器一样直接编辑 Note 和 Channel Event 的轨道。

两种方式可以在同一个 Project 中混用：需要大量重复时使用 Event Instrument，需要直接控制时使用 Pure MIDI Track。Midora 最终会把 Project 转换为标准 MIDI 1.0 数据用于播放和导出，因此结果仍能以 MIDI 的形式离开 Midora 使用。

## 不只是事件模板：一件完整的 MIDI 乐器

Event Instrument 并非只能原样重放一段固定事件的宏。它可以完整描述一件由 MIDI 构造的乐器如何组成、如何控制，以及如何响应演奏：

- **多个独立声部**：每条 **SubVoice（子声部）**都可以拥有自己的 Note、Bank/Program、控制器、Pitch Bend 和曲线。Sine 主体、Click 起音及更多声部可以各自完成不同工作，但在编曲时仍作为一件乐器触发和书写。
- **用音乐含义控制声音**：乐器可以向编曲界面提供 `Punch（冲击感）`、`Brightness（亮度）`、`Pitch Drop（音高下坠）` 等 **Logical Parameter（逻辑参数）**。一个参数可以同时驱动多个声部中的多个底层 MIDI 值，让用户直接塑造声音，而不必重新打开事件堆或逐条修改 CC 曲线。
- **完整的音符生命周期**：乐器可以分别处理短音、长音、释放和重叠。它既可以在 Note Off 时立即截断，也可以作为 One-shot 播放、执行尾部事件、保持状态、循环模板的一部分、跟随 Envelope，并在重叠音符需要独立 Channel 状态时将它们隔离。

乐器定义与使用它的编曲内容分开保存。修改一次定义，所有引用都会使用更新后的设计；引用同一定义的轨道可以保持彼此独立的运行状态，也可以在确实需要时显式组成共享状态组。只有想制作独立变体时，才需要复制一份乐器定义。

## 无需管理 Port 和 Channel

使用 Event Instrument 和 SubVoice 时，用户无需为它们选择 Port 或 Channel，也无需创建或删除 Port。Midora 会计算所需路由、跨 Port 完成分配、安全复用已经释放的 Channel Unit，并避免无关声音之间发生 Channel 状态污染。对于通常的 Logical Track 编曲，用户基本不需要感知 Port/Channel 的存在。

如果确实需要共享状态，用户可以把多个 Logical Track 显式绑定为一个共享组。在乐器的共享状态模式下，这些轨道会固定使用同一个 Channel Group，而不是由系统偶然把它们分配到一起；引用同一乐器但彼此独立的轨道，运行状态仍然互相隔离。

Pure MIDI Track 则保留下层路由控制：既可以让程序自动分配，也可以把一条轨道——或共享同一路由的一组轨道——固定到明确的 `Port.Channel` 地址。Midora 最多支持 **16 Ports × 16 Channels = 256 Channel Units**。如果工程无法放入这一资源上限，编译会明确失败，而不会静默丢弃、抢占或截短 Note。

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
