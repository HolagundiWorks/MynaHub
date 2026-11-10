# Suite assistant model (Olmo 3 7B)

MynaHub **hosts** the shared Olmo 3 7B Instruct model (`Office.Ai` + `AiCoreListener` on `127.0.0.1:8742`). MynaOps and MynaCAD connect as clients when launched from MynaHub (`OSAS_AI_ROLE=client`). Install paths, llama.cpp runtime, and the HTTP contract are documented in **MynaOps** `docs/LOCAL-MODEL.md`.

## What MynaHub adds

| Part | Where |
|---|---|
| AI Core host while a company workspace is open | `SuiteAiCoordinator`, `MainWindow.ShowWorkspace` |
| Floating Ask bar (glowing logo) | `Suite/AssistantBar.cs` |
| HTTP client to the host (`POST /v1/chat`) | `Osas.Core/Agent/OlmoAiCoreApi.cs` |
| Prompt-based tool use (Olmo has no native tool API in this stack) | `Osas.Core/Agent/PromptChat.cs` |
| Assistant loop, read-only tools, audit | `Assistant.cs`, `office-agent` |
| Suite dashboard card | `Suite/SuitePage.cs` |
| Shared Ask bar layout numbers | `o-ui/O.Ui/SuiteAskChrome.cs` |

## Tulu (removed from product path)

The older **Tulu 3 8B** bundle under `%LOCALAPPDATA%\O-Studio` and `LocalRuntime.cs` are no longer used by the assistant bar. They remain in the tree for reference until the bundled runtime is deleted in a later cleanup.

## Lifecycle

1. Sign in to MynaHub → `SuiteAiCoordinator.StartHostIfNeeded()` binds `:8742`.
2. First question (MynaHub, MynaOps, or MynaCAD) loads `llama-server` and the GGUF from `%LOCALAPPDATA%\HumanCentricWorks\models\`.
3. Sign out, close the workspace, or exit MynaHub → listener stops and `LocalLlmHost.Shutdown()` unloads the model.
4. Launch MynaOps or MynaCAD from MynaHub → those processes do not load weights; they use the host.
