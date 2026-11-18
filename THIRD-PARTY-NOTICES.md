# Passport Checker Reborn

Party Scout adapts the Tomestone encounter mappings and response parsing, FFLogs encounter/job mappings and rankings parsing, and adventure-plate name-resolution approach from [PassportCheckerReborn](https://github.com/FFXIV-CombatReborn/PassportCheckerReborn), commit `b6bfcb453d2c5659a3711728964669693154dd40` (2026-10-26 inspection).

Upstream is by the FFXIV-CombatReborn / PassportCheckerReborn contributors and distributed under GNU AGPL version 3. Its license is included in `LICENSE.md`. Party Scout remains AGPL-3.0-or-later. Modified files identify this origin in their headers. Party Scout adds cancellation, bounded in-memory retention and a different UI; it does not bundle or load the upstream plugin assembly.

The upstream member-lookup implementation credits OpenRadar for the approach. Party Scout uses the SDK-supplied FFXIVClientStructs definitions and signatures rather than copying private native layouts.

## DalamudUi

Party Scout uses DalamudUi for Material-inspired controls. License texts for Material UI, MUI X Community, d3-shape, and Material Icons are included as `LICENSE-MUI`, `LICENSE-MUI-X`, `LICENSE-D3-SHAPE`, and `LICENSE-MATERIAL-ICONS`.

## AI assistance

Party Scout was developed with AI assistance, and its logo was AI-generated.
