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

- [How many MIDI events are hiding inside one kick?](#how-many-midi-events-are-hiding-inside-one-kick)
- [Design the sound once](#design-the-sound-once)
- [Give the instrument its own playing behavior](#give-the-instrument-its-own-playing-behavior)
- [Turn raw MIDI into controls you choose](#turn-raw-midi-into-controls-you-choose)
- [Let Midora handle the channels](#let-midora-handle-the-channels)
- [Why not just use a DAW?](#why-not-just-use-a-daw)
- [Black MIDI performance](#black-midi-performance)
- [Platform and prerequisites](#platform-and-prerequisites)
- [Build from source](#build-from-source)
- [Documentation](#documentation)
- [Acknowledgements](#acknowledgements)
- [License and third-party components](#license-and-third-party-components)

## How many MIDI events are hiding inside one kick?

Suppose a kick has two parts: a **Sine** for its body and a short **Click** for its attack. The Sine quickly falls in pitch; the Click follows its own volume shape. In a regular MIDI editor, that small sound may require two channels and a pile of notes, controller changes, pitch bends, and setup events.

Building the first hit is not the difficult part. The trouble starts after you copy it across a rhythm: changing the Click, reshaping the pitch drop, or adding a third layer means finding and updating every copy. Miss one event and that hit may sound different from the rest.

## Design the sound once

In Midora, you build the Sine + Click kick once as an **Event Instrument**: a reusable sound design made entirely from MIDI events. After that, every hit in the arrangement is written as an ordinary note.

```text
Design the Sine + Click sound once
              ↓
Place one simple note for each kick hit
              ↓
Midora expands every hit into the full MIDI event sequence
```

Change the instrument once and every place that uses it receives the new design. Make a separate copy only when you actually want a variation, such as a softer kick or a longer one.

Only three Midora terms are needed to get started:

- **Event Instrument**: the reusable sound design, such as the Sine + Click kick.
- **Logical Track**: a track where ordinary-looking notes play that instrument.
- **Pure MIDI Track**: a traditional MIDI track where notes and events are edited directly.

Both track types can live in the same project. Use Event Instruments where repeated event editing becomes painful, and keep Pure MIDI Tracks wherever direct control is more convenient. Midora expands everything into standard MIDI 1.0 for playback and export.

## Give the instrument its own playing behavior

An Event Instrument is not limited to replaying one fixed block. It can describe how the sound is built and how it should react when played:

- **Build in layers**: the Sine, Click, Noise, and any other part can each have its own notes, sound selection, bends, controllers, and curves, while the arrangement still plays them as one instrument. Midora calls each layer a **SubVoice**.
- **Customize the Initial State**: an instrument—or an individual layer—can start with the intended Bank/Program, Expression, Pitch Bend, bend range, and other controller settings. For example, the Sine can begin centered with a wide bend range while the Click selects a different sound and level.
- **Decide what short and long notes mean**: releasing a short note can begin its ending, let it finish as a one-shot, or continue with tail events. A long note can simply hold its last state.
- **Keep sounds alive with a Loop**: select a middle section to repeat for as long as the note is held. Releasing the note leaves the Loop and moves into the instrument's ending, keeping every layer aligned without copied repetitions.
- **Shape attack and release**: reusable ADSR-style envelopes, with additional Delay and Hold stages, can shape any supported MIDI value—not only volume. A release can fade Expression, return Pitch Bend toward center, or change another controller before the actual Note Off is sent.
- **Control overlapping notes**: when two notes need independent bends or controller movement, Midora can give each one isolated channel state instead of letting them interfere with each other.

For example, a held tonal effect can play its opening once, loop the middle for as long as the note is held, then leave the loop and follow its release envelope when the note ends. All layers follow the same instrument lifecycle, so this behavior does not have to be rebuilt in every track.

## Turn raw MIDI into controls you choose

**Logical Parameters** are the controls an instrument chooses to show to the arrangement. They are not a fixed set of knobs: the instrument author defines their names, defaults, ranges, and whether a value is a whole number, a decimal, or a choice such as `Soft / Hard`.

For the kick, you might expose a single `Punch` control from `0` to `100`. Raising it could strengthen the Click layer, deepen the Sine pitch drop, and change Expression in both layers at once. The person writing the rhythm edits `Punch`; the instrument takes care of the underlying MIDI values.

Mappings can be simple ranges and curves, or they can use a **Mapping Function**: a small numeric expression that calculates the result from the current value and musical context. It can, for example, make the same `Punch` setting react differently according to the played pitch, velocity, note length, or position in the song. This allows one Event Instrument to stay compact on the outside while remaining highly customizable inside.

## Let Midora handle the channels

Event Instruments do not ask you to assign a Port or Channel to every layer. Midora finds the required channels, spreads them across Ports when necessary, reuses them when safe, and prevents unrelated sounds from inheriting each other's bends or controller state. In ordinary Logical Track work, channels are something you can largely forget about.

If several Logical Tracks are intended to act as parts of one shared instrument—for example, two lines that should share the same sustain and Expression state—you can explicitly group them. In shared-state mode, Midora keeps them on the same Channel Group; tracks that should be independent remain isolated even when they use the same instrument design.

Pure MIDI Tracks keep the familiar lower-level choice: let Midora assign a route, or pin a track—or a group sharing one route—to an exact `Port.Channel` address. Midora supports up to **16 Ports × 16 Channels = 256 Channel Units**. If that limit is exceeded, compilation fails clearly instead of silently dropping, stealing, or shortening notes.

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
