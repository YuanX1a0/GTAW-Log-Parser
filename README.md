# GTA World Chat Assistant / GTA World 聊天助手

A translation assistant for GTA World roleplay (FiveM). It captures the in-game chat, translates it in real time, and lets you review and send your own translated messages — all inside the game.

面向 GTA World 角色扮演（FiveM）的翻译助手。自动捕获游戏内聊天、实时翻译，并支持在游戏内编辑并发送自己的翻译消息。

## Features / 功能

- **Real-time chat translation** — every in-game chat message is captured and translated automatically as it appears.
  **聊天实时翻译** — 游戏内每条聊天消息自动捕获并即时翻译。
- **Translator window** — press the hotkey while typing to open an in-game overlay showing your original text and its translation side by side. Edit the translation, then send it to the game or apply it to the chat input.
  **翻译器窗口** — 输入时按下快捷键，游戏内弹出翻译窗口，原文与译文对照显示；可编辑译文，一键发送到游戏或应用到聊天输入框。
- **Auto-translate hotkey** — toggle automatic translation of incoming chat messages with a single key.
  **自动翻译快捷键** — 一键开关聊天消息的自动翻译。
- **Multiple translation providers** — Google, DeepSeek, DeepL, Doubao (Volcano Ark), Zoom, plus a **custom OpenAI-compatible provider** with 200+ built-in service presets (DeepSeek, OpenRouter, SiliconFlow, Moonshot, Alibaba Bailian, Doubao, Anthropic, Gemini, Ollama, ...). Pick a provider, enter your API key and the model list is fetched automatically.
  **多翻译引擎** — 支持 Google、DeepSeek、DeepL、豆包（火山方舟）、Zoom，以及**自定义 AI 服务（OpenAI 兼容接口）**，内置 200+ 服务商预设（DeepSeek、OpenRouter、硅基流动、Moonshot、阿里百炼、豆包、Anthropic、Gemini、Ollama 等）。选择服务商 + 输入 API Key 自动读取模型列表。
- **Translation cache** — previously translated text is cached, so repeated messages resolve instantly and save API calls.
  **翻译缓存** — 翻译过的文本会被缓存，重复消息秒出结果，节省 API 调用。
- **Statistics** — total translations, characters and an English word-frequency ranking (common words are filtered out) shown on the overview page.
  **翻译统计** — 总览页显示翻译总次数、字符数以及英文单词词频排行（已过滤常用停用词）。
- **Realtime log page** — a live view of application / game / hotkey / translation events and errors for troubleshooting.
  **实时日志页** — 实时显示程序 / 游戏 / 快捷键 / 翻译事件与错误，方便排查问题。
- **Chat log backup & filtering** — the classic GTA World chat log backup and filtering workflow is preserved.
  **聊天日志备份与过滤** — 保留经典的 GTA World 聊天日志备份和过滤功能。
- **Localized UI** — Simplified Chinese, Traditional Chinese, English and Spanish.
  **多语言界面** — 简体中文、繁体中文、英文、西班牙语。
- **Windows notifications** — hotkey presses are reported as Windows toasts.
  **Windows 通知** — 快捷键触发时弹出系统通知。

## Download / 下载

No installation is required. Download the latest executable from the [releases page](https://github.com/YuanX1a0/GTAW-Log-Parser/releases) and run it.

免安装。前往 [Release 页面](https://github.com/YuanX1a0/GTAW-Log-Parser/releases) 下载最新版可执行文件，直接运行即可。

## Usage / 使用说明

1. Start FiveM, join a GTA World server, then start the assistant.
   启动 FiveM 并进入 GTA World 服务器，然后打开本助手。
2. In the **翻译 / Translation** page, pick a translation provider and enter your API key (Google needs no key). For the custom provider, choose a service preset and enter its API key; the model list loads automatically.
   在 **翻译** 页面选择翻译引擎并填入 API Key（Google 无需 Key）。自定义服务选择服务商预设后填入对应 API Key，模型列表会自动加载。
3. Toggle chat translation or the translator window from the assistant, or use the in-game hotkey (default `Numpad` / configurable).
   打开聊天翻译或翻译器窗口，或使用游戏内快捷键（默认小键盘，可自定义）。
4. Everything else (statistics, realtime log, chat log backup/filtering) lives in the left-hand menu.
   其他功能（统计、实时日志、聊天日志备份/过滤）都在左侧菜单中。

## Building / 构建

- .NET Framework 4.8.1, WPF (MahApps.Metro).
  .NET Framework 4.8.1，WPF（MahApps.Metro）。
- Restore NuGet packages before compiling, e.g. `msbuild Assistant/Assistant.csproj /t:Restore`.
  编译前先还原 NuGet 包，例如 `msbuild Assistant/Assistant.csproj /t:Restore`。

## Project / 项目信息

- Developer / 开发者: **YuanX1a0**
- Repository / 项目主页: <https://github.com/YuanX1a0/GTAW-Log-Parser>
- Branches / 分支: `master` (stable / 稳定版) · `beta` (in-development / 开发版)
- Chinese README / 中文说明: [README.zh-CN.md](README.zh-CN.md)

## License / 许可证

Distributed under the GPLv3 license. See `LICENSE` for more information.

基于 GPLv3 协议分发，详见 `LICENSE`。
