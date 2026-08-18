# GTA World Chat Assistant

A translation assistant for GTA World roleplay (FiveM). It captures the in-game chat, translates it in real time, and lets you review and send your own translated messages — all inside the game.

## Features

- **Real-time chat translation** — every in-game chat message is captured and translated automatically as it appears.
- **Translator window** — press the hotkey while typing to open an in-game overlay showing your original text and its translation side by side. Edit the translation, then send it to the game or apply it to the chat input.
- **Auto-translate hotkey** — toggle automatic translation of incoming chat messages with a single key.
- **Multiple translation providers** — Google, DeepSeek, DeepL, Doubao (Volcano Ark) and Zoom. Configure provider, API key, model, target and source languages in the settings page; changes take effect immediately.
- **Translation cache** — previously translated text is cached, so repeated messages resolve instantly and save API calls.
- **Statistics** — total translations, characters and an English word-frequency ranking (common words are filtered out) shown on the overview page.
- **Realtime log page** — a live view of application / game / hotkey / translation events and errors for troubleshooting.
- **Chat log backup & filtering** — the classic GTA World chat log backup and filtering workflow is preserved.
- **Localized UI** — Simplified Chinese, Traditional Chinese, English and Spanish.
- **Windows notifications** — hotkey presses are reported as Windows toasts.

## Download

No installation is required. Download the latest executable from the [releases page](https://github.com/YuanX1a0/GTAW-Log-Parser/releases) and run it.

## Usage

1. Start FiveM, join a GTA World server, then start the assistant.
2. In the **翻译 / Translation** page, pick a translation provider and enter your API key (Google needs no key).
3. Toggle chat translation or the translator window from the assistant, or use the in-game hotkey (default `Numpad` / configurable).
4. Everything else (statistics, realtime log, chat log backup/filtering) lives in the left-hand menu.

## Building

- .NET Framework 4.8.1, WPF (MahApps.Metro).
- Restore NuGet packages before compiling, e.g. `msbuild Assistant/Assistant.csproj /t:Restore`.

## Project

- Developer: **YuanX1a0**
- Repository: <https://github.com/YuanX1a0/GTAW-Log-Parser>
- Branches: `master` (stable) · `beta` (in-development)

## License

Distributed under the GPLv3 license. See `LICENSE` for more information.
