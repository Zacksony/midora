# Midora Codex Execution Safety Requirement Trace

## Status

This record covers repository tooling policy only. It does not change Midora product semantics, the `.midora` format, public APIs, concurrency behavior, audio behavior, or the SRS.

## Inputs

- User-requested fail-closed Codex policy: sandbox required, no Full Access or unsandboxed fallback, and approval policy `never`.
- Locally installed `codex-cli 0.142.3` help and redacted `codex doctor` output.
- Current-version OpenAI configuration reference for project-scoped `.codex/config.toml`, `sandbox_mode`, `approval_policy`, and `sandbox_workspace_write`.
- Existing user configuration and current task permission state, inspected only for the security-relevant keys requested by the user.
- 《Midora SRS》document control, Chapter 1, Chapter 21, and Chapter 22. No product requirement directly specifies Codex runtime permissions.

## Required outputs

- `.codex/config.toml` selects `workspace-write` and `approval_policy = "never"`.
- The project config grants no additional writable roots and sets outbound command network access to disabled.
- Root `AGENTS.md` contains persistent fail-closed, no-escalation, file/secrets, network, prompt-injection, nested-instruction, command-audit, deletion, and external-side-effect rules.

## Boundaries and ownership

- Persistence owner: repository `.codex/config.toml` and root `AGENTS.md`.
- Runtime owner: the Codex client/session that loads those files after the trusted project is opened.
- Writable scope: repository/workspace only, subject to the Codex runtime's documented protected paths and unavoidable implementation-specific temporary storage.
- Failure behavior: a denied operation fails and is reported to the model; it must not become an approval or unsandboxed retry.
- Diagnostics: config parsing, effective-mode inspection, an in-workspace write probe, and a harmless non-workspace denial probe.

## Explicit non-goals

- No user/global `config.toml` changes.
- No system `%ProgramData%\OpenAI\Codex\requirements.toml` or enterprise policy changes.
- No administrator/UAC setup, Full Access changes, or UI automation.
- No SRS, product source, dependency, build output, or `dist/` changes.
- No claim that project-local configuration can override an explicit host/UI/CLI Full Access launch; only managed requirements can make that choice non-overridable.

## Verification on 2026-08-26

- `codex doctor` resolved the new project layer as restricted filesystem, restricted network, and approval `Never`.
- A sandboxed write inside the repository succeeded.
- A sandboxed write outside the repository and a write under protected `.codex/` both failed with access denied and created no file.
- A write to the sandbox-provided Windows `%TEMP%` directory succeeded and the probe was removed. `workspace-write` therefore has a bounded temporary-directory exception; it is not literally repository-only.
- The active Windows sandbox implementation is the user-configured `unelevated` fallback. A direct plain-HTTP probe with proxy bypass still reached `example.com` even though `network_access = false`; therefore the current implementation does not provide a hard command-network boundary. The project policy still forbids such bypasses, but that part is behavioral rather than technically fail-closed.
- The task that created this policy was already launched by its host with Full Access. Project config does not retroactively change that active task; a new sandboxed task/session is required.
