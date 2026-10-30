# Imperialism2_Patch

Community bugfix patch for **Imperialism II: The Age of Exploration** (v1.03 GOG release), addressing issues in an older title no longer in active development.

> **Note:** This repository does **not** include the game's data, music, or asset files. You **cannot** play the game with this file alone—a legitimate copy of *Imperialism II* must be purchased from the current rights holders (e.g., on [GOG.com](https://www.gog.com/en/game/imperialism_2_the_age_of_exploration)).

---

## What This Fixes

### `_winCdPlay` CD Audio Assertion Popup (`D:\Epsilon\win_cd.cpp @407`)
On modern Windows systems, *Imperialism II* (v1.03) can intermittently trigger a blocking debug assertion popup when changing music tracks:

```text
ASSERTION
D:\Epsilon\win_cd.cpp @407
_winCdPlay
```

* **Root cause:** In `_winCdPlay`, the game calls `_inmm.dll!mciSendCommandA` (`MCI_SET` with `MCI_FORMAT_TMSF`) before playing a track. If that MCI command returns a non-zero status code, the retail v1.03 binary still contains a leftover debug `ShowAssertDialog` call that freezes the game with a modal popup (and crashes to desktop if *Abort* or *Debugger* is clicked).
* **Fix:** Replaces the 5-byte `call ShowAssertDialog` instruction inside `_winCdPlay` with `NOP`s while leaving the normal playback path untouched:
  * When `_inmm.dll` succeeds (`eax == 0`), soundtrack playback from `MUSIC\Track02.ogg`–`Track14.ogg` works exactly as normal.
  * If MCI reports an error (`eax != 0`), `_winCdPlay` now fails silently and returns cleanly instead of interrupting or crashing your game.

---

## Installation

1. Locate your **Imperialism II** (v1.03 / GOG) installation folder.
2. Back up your original `Imperialism II.exe` (for example, rename it to `Imperialism II.exe.bak`).
3. Download **`ImpII.exe`** from this repository, place it into your game installation folder, and rename it to:
   ```text
   Imperialism II.exe
   ```
4. Keep `_inmm.dll`, `libogg.dll`, `libvorbis.dll`, `libvorbisfile.dll`, and the `MUSIC` folder in the same directory so in-game music plays properly.

---

## Technical Patch Details

| Property | Value |
| :--- | :--- |
| **Target Version** | *Imperialism II: The Age of Exploration* v1.03 (GOG) |
| **File Size** | `4,024,320` bytes |
| **Original SHA-256** | `63346490C488D2A4EF528E3C0884D823E60AC59F602FA82657B39AA040253279` |
| **Patched SHA-256 (`ImpII.exe`)** | `DB5556FEA228CFF2B87CB33D9278A86B789202BDAD7A68D13179971F3C0D01B9` |
| **Function** | `_winCdPlay` (`VA 0x0067BD30`, file offset `0x27B130`) |
| **Patched Offset** | File offset `0x27B16C` (`VA 0x0067BD6C`) |
| **Original Bytes (5)** | `E8 40 AB D8 FF` (`call 0x004068B1` -> `ShowAssertDialog` at `0x0048B550`) |
| **Patched Bytes (5)** | `90 90 90 90 90` (`nop` × 5) |
