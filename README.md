<p align="center">
  <img src="./assets/docs/readme/midora-banner.svg" alt="Midora — MIDI 1.0 event instrument environment" width="100%">
</p>

<p align="center">
  <a href="./docs/README.md">English</a> ·
  <a href="./docs/README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img alt="Status: 1.0.0-dev" src="https://img.shields.io/badge/status-1.0.0--dev-d52b37?style=flat-square">
  <img alt="Platform: Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-17191f?style=flat-square&logo=windows11&logoColor=white">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512bd4?style=flat-square&logo=dotnet&logoColor=white">
  <img alt="MIDI 1.0" src="https://img.shields.io/badge/MIDI-1.0-17191f?style=flat-square">
  <a href="./LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-d52b37?style=flat-square"></a>
</p>

# Midora

**Design reusable MIDI event instruments. Compose with logical or direct MIDI tracks. Compile everything into one deterministic result.**

Midora is a Windows desktop composition and compilation environment for MIDI 1.0. It is being built for musicians who want to turn dense, stateful MIDI event sequences into reusable **Event Instruments**, while retaining a direct **Pure MIDI Track** workflow when low-level control is the better tool.

> [!IMPORTANT]
> Midora is under active development at `1.0.0-dev`. There is no stable end-user release yet. The capabilities below describe the initial-release scope, not a promise that every item is complete in the current commit.

## Why Midora?

A single MIDI-driven sound can involve Program and Bank changes, Pitch Bend Range, controller curves, RPN/NRPN state, notes, release behavior, and cleanup. Copying that event stack by hand is repetitive and makes channel-state conflicts easy to introduce.

Midora turns that workflow into a reusable system:

```text
Event Instrument definitions ──► Logical Tracks ──┐
                                                  │
SMF import / direct MIDI ───────► Pure MIDI Tracks ├─► Semantic validation
                                                  │          │
                                                  └──────────▼
                                                Canonical compilation
                                                          │
                                    ┌───────────┬───────────┴──────────┐
                                    ▼           ▼                      ▼
                                 Playback   MIDI export          Audio render
```

Playback, preview, MIDI export, and audio rendering consume the same canonical compiled result. They do not independently reinterpret the project.

## Initial-release direction

- Build reusable Event Instruments from notes, MIDI events, SubVoices, logical parameters, mappings, lifecycle rules, loops, envelopes, and overlap policies.
- Arrange with high-level Logical Tracks or edit MIDI 1.0 notes and channel events directly in Pure MIDI Tracks.
- Allocate up to 16 ports × 16 channels deterministically, with explicit lifecycle and reset boundaries.
- Open SMF 1.0 Format 0/1 files with TPQN timing and export deterministic SMF Type 1 output.
- Play and preview through an application-level ordered SF2/SFZ list, then render stereo IEEE float32 RIFF/WAVE audio.
- Save source projects as `.midora` packages without embedding compiled results, caches, exports, or session UI state.

Midora is intentionally **not** a DAW, VST host, audio recorder, MIDI 2.0 tool, or general-purpose replacement for a full MIDI workstation.

## Platform and prerequisites

| Requirement | Initial-release baseline |
|---|---|
| Operating system | Windows desktop, x64 |
| Runtime and UI | .NET 10, WPF |
| Repository SDK | .NET SDK `10.0.302`, pinned by [`global.json`](./global.json) |
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

The runner validates every native file against the pinned versions and SHA-256 values in [`bass-native-baseline.win-x64.json`](./src/midora-audio/bass-native-baseline.win-x64.json). It will reject missing or different revisions.

## Documentation

| Document | Description |
|---|---|
| [`docs/README.md`](./docs/README.md) | English README |
| [`docs/README.zh-CN.md`](./docs/README.zh-CN.md) | 简体中文 README |
| [Midora SRS](./misc/Midora-SRS-Initial-Release-v0.1/00-Table-of-Contents-and-Document-Control.md) | Normative initial-release requirements |
| [Implementation roadmap](./misc/Midora-Implementation-Roadmap.md) | Engineering sequence and recorded implementation status; the SRS takes precedence |

## License and third-party components

Midora's own source code is available under the [MIT License](./LICENSE), copyright © 2026 Midora contributors. The project's official initial-release positioning is free, open source, and non-commercial; this positioning does not add restrictions to the standard MIT terms.

BASS, BASSMIDI, BASSWASAPI, user-provided SoundFonts, and other third-party materials retain their own licenses. They are not relicensed by Midora. Review [`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md) and the current [Un4seen BASS licensing terms](https://www.un4seen.com/bass.html) before redistribution or commercial use.

---

<p align="center"><sub>MIDI events are the material. Deterministic compilation is the instrument.</sub></p>
