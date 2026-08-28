<p align="center">
  <a href="./README.md">English</a> ·
  <a href="./README.zh-CN.md">简体中文</a> ·
  <a href="../README.md">Repository home</a>
</p>

<p align="center">
  <img alt="Status: 1.0.0-dev" src="https://img.shields.io/badge/status-1.0.0--dev-d52b37?style=flat-square">
  <img alt="Platform: Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-17191f?style=flat-square&logo=windows11&logoColor=white">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512bd4?style=flat-square">
  <img alt="MIDI 1.0" src="https://img.shields.io/badge/MIDI-1.0-17191f?style=flat-square">
  <a href="../LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-d52b37?style=flat-square"></a>
</p>

# Midora

**Build a MIDI sound once. Use it like an instrument.**

Midora is for musicians who enjoy shaping sounds with MIDI itself. If you have ever built a custom sound in a regular MIDI editor, you already know the problem: the sound is rarely just one note. It is often a carefully timed stack of notes, controller curves, program changes, pitch bends, and resets—and every new hit means copying that stack again.

> [!IMPORTANT]
> Midora is under active development at `1.0.0-dev`. There is no stable end-user release yet, and workflows may still change before 1.0.

## Table of contents

- [One familiar example: a Sine + Click kick](#one-familiar-example-a-sine--click-kick)
- [How Midora changes the workflow](#how-midora-changes-the-workflow)
- [Why not just use a DAW?](#why-not-just-use-a-daw)
- [Black MIDI performance](#black-midi-performance)
- [Platform and prerequisites](#platform-and-prerequisites)
- [Build from source](#build-from-source)
- [Documentation](#documentation)
- [Acknowledgements](#acknowledgements)
- [License and third-party components](#license-and-third-party-components)

## One familiar example: a Sine + Click kick

Suppose you want to make a kick from two layers:

- a **Sine** layer for the body;
- a short **Click** layer for the attack.

In a regular MIDI editor, you may create two channels, choose their Bank/Program settings, configure Pitch Bend Range, draw the pitch drop and Expression curves, align both notes, and add the required cleanup events. Making one kick is manageable. Writing a whole rhythm means copying those two event stacks for every hit.

That creates familiar problems: changing the sound means finding and updating every copy; one missed event can make hits behave differently; fast repeats can leave Pitch Bend or CC state leaking into the next hit; and adding more layers means manually managing even more channels.

## How Midora changes the workflow

In Midora, you design the Sine + Click kick once as an **Event Instrument**—a reusable recipe made entirely from MIDI events. After that, you can write each use on a track as simply as an ordinary note.

```text
Design the Sine + Click recipe once
              ↓
Place one simple note for each kick hit
              ↓
Midora expands every hit into the full MIDI event sequence
```

You arrange the rhythm with compact notes instead of copied event piles. Midora expands those notes, assigns the required ports and channels, and handles event ordering and cleanup boundaries. Change the recipe once, and the arrangement uses the updated design without editing every copied stack.

Three Midora terms are enough to understand the idea:

- **Event Instrument**: the reusable MIDI-event recipe, such as the Sine + Click kick.
- **Logical Track**: a track containing compact notes that call an Event Instrument.
- **Pure MIDI Track**: a familiar direct MIDI track for editing notes and channel events without the reusable layer.

You can combine both approaches in one project: use Event Instruments where repetition is painful, and use Pure MIDI Tracks where direct control is simpler. Midora turns the project into standard MIDI 1.0 data for playback and export, so the result remains usable outside Midora as MIDI.

## Why not just use a DAW?

Midora is not trying to replace a DAW. Its purpose is to explore how far composition made entirely from MIDI 1.0 events can be pushed. A DAW offers far more freedom; Midora offers a different kind of fun: doing more within the limits of pure MIDI.

## Black MIDI performance

Midora's smooth-editing support target is Black MIDI at the million-note scale: **fewer than 10,000,000 notes**.

Midora can open Black MIDI projects containing tens or even hundreds of millions of notes, and it uses paged data and visibility-based rendering to keep dense-note browsing as responsive as practical. Above the supported editing range, however, Midora does not guarantee that editing speed or memory usage will meet your needs.

If your main goal is editing Black MIDI at still larger scales, also see these projects built with that workload in mind:

- [yinhe](https://github.com/BuickMeow/yinhe)
- [lumino-rs](https://github.com/PenguinBMDevs/lumino-rs)

## Platform and prerequisites

| Requirement | Initial-release baseline |
|---|---|
| Operating system | Windows desktop, x64 |
| Runtime and UI | .NET 10, WPF |
| Repository SDK | .NET SDK `10.0.302`, pinned by [`global.json`](../global.json) |
| MIDI | MIDI 1.0 |
| Audio resources | User-provided SF2/SFZ files |
| Native audio backend | Pinned x64 BASS, BASSMIDI, and BASSWASAPI binaries |

The BASS binaries and SoundFonts are **not** stored in this repository. Projects can still be opened, saved, compiled, and exported without an enabled SoundFont; playback, preview, and audio rendering require one.

## Build from source

Install the pinned .NET SDK and Git LFS, then clone and build the desktop solution:

```powershell
git clone https://github.com/Zacksony/midora.git
cd midora
git lfs pull
dotnet restore src/midora-desktop/midora-desktop.slnx --locked-mode
dotnet build src/midora-desktop/midora-desktop.slnx -c Release --no-restore
```

To start the desktop application with its formal audio worker, obtain the exact licensed x64 BASS binaries and pass their directory to the repository runner:

```powershell
.\Run-MidoraDesktop.ps1 -Configuration Release -BassNativeDirectory "C:\path\to\bass\win-x64"
```

The runner validates every native file against the pinned versions and SHA-256 values in [`bass-native-baseline.win-x64.json`](../src/midora-audio/bass-native-baseline.win-x64.json). It will reject missing or different revisions.

## Documentation

| Document | Description |
|---|---|
| [`README.md`](./README.md) | English README |
| [`README.zh-CN.md`](./README.zh-CN.md) | 简体中文 README |
| [Midora SRS](../misc/Midora-SRS-Initial-Release-v0.1/00-Table-of-Contents-and-Document-Control.md) | Normative initial-release requirements |
| [Implementation roadmap](../misc/Midora-Implementation-Roadmap.md) | Engineering sequence and recorded implementation status; the SRS takes precedence |

## Acknowledgements

Special thanks to [BuickMeow](https://github.com/BuickMeow) and [Enderman-bm](https://github.com/Enderman-bm) for their performance-related advice during Midora's development.

Midora's dense-note rendering optimizations were informed by ideas from [yinhe](https://github.com/BuickMeow/yinhe).

## License and third-party components

Midora's own source code is available under the [MIT License](../LICENSE), copyright © 2026 Midora contributors. The project's official initial-release positioning is free, open source, and non-commercial; this positioning does not add restrictions to the standard MIT terms.

BASS, BASSMIDI, BASSWASAPI, user-provided SoundFonts, and other third-party materials retain their own licenses. They are not relicensed by Midora. Review [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) and the current [Un4seen BASS licensing terms](https://www.un4seen.com/bass.html) before redistribution or commercial use.

---

<p align="center"><sub>Build once. Compose freely. Export standard MIDI.</sub></p>
