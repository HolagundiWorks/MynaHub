# Running Tulu inside O-Studio

O-Studio's assistant is **Tulu 3 8B** from the Allen Institute for AI, and it is the only model. It runs on the same computer, in a runtime O-Studio carries and manages itself. There is no cloud service, no key, no Ollama, and no dependency on O-Draft, O-CoreOp or any other suite app. O-CoreOp and O-Draft run their own models.

The engine is a bundled llama.cpp server (`llama-server.exe`), used as a plain local program. It is the lightest way to run the quantized model; the blueprint's other option, Python with Transformers, would need a bundled Python and PyTorch.

## What is built

| Part | Where |
|---|---|
| Runtime manager: finds the server and model, checks the model against its checksum, starts the server on a free loopback port, waits for `/health`, stops it | `native/Osas.Core/Agent/LocalRuntime.cs` |
| Server client (`/v1/chat/completions`) with the Tulu sampling profile | `LlamaServerApi`, same file |
| Prompt-based tool use (Tulu 3's chat template has no tool-calling) | `native/Osas.Core/Agent/PromptChat.cs` |
| Assistant loop, read-only tools, audit | `Assistant.cs`, `Suite/AssistantBar.cs`, `office-agent` |
| The floating assistant (glowing logo), Suite dashboard card | `Suite/AssistantBar.cs`, `Suite/SuitePage.cs` |
| Tests | `Osas.Core.Tests/LocalRuntimeTests.cs` (checksum, missing file, unsafe manifest, arguments, request shape, tool-reply parsing) and a live test that runs the real model when `TULU_DIR` is set |

## Where the files go

Beside the program, or under `%LOCALAPPDATA%\O-Studio`:

```
runtime\windows-x64\llama-server.exe      (and its DLLs: the llama.cpp CPU build for x64)
models\tulu\model.gguf                    (Llama-3.1-Tulu-3-8B, Q4_K_M)
models\tulu\manifest.json
models\tulu\LICENSES\                     (licence texts shipped with the model)
```

`manifest.json`:

```json
{ "name": "Tulu 3 8B (Q4_K_M)", "file": "model.gguf", "sha256": "<hex sha-256 of the file>", "license": "Llama 3.1 Community License", "context": 8192 }
```

The file name must stay inside the model folder. A file that does not match its checksum is refused with a clear message. A good result is remembered by file size and time, so the several-gigabyte hash is not repeated on every start.

Without these files the assistant says that Tulu is not installed and the Suite dashboard shows the same.

## Lifecycle

1. O-Studio does not load the model when it starts. **Check for Tulu** only checks the files.
2. The first question starts the server (`-m <model> -c 8192 --host 127.0.0.1 --port <free port> --parallel 1 --no-webui`) and waits up to three minutes for it to report ready. The panel says so while it starts.
3. Questions go to that server and nowhere else. Nothing leaves the computer.
4. **Unload model** stops it. Closing O-Studio stops it, and so does the process exiting in any other way.

## Profile (starting values, to be measured)

Temperature 0.3, top-p 0.9, 768 new tokens, 8,192 tokens of context, one generation at a time, loopback only. 4-bit quantization must be compared with a higher-precision reference on a fixed set of tasks before it is accepted for release.

## Tools and safety

The tools are the existing read-only set in `office-agent`. The engine checks the signed-in person's role on every call, writes the audit entry, and nothing is changed. The model has no filesystem access and no scripting; it can only name one of these tools. Tool results reach the model as data to read, never as instructions.

The modelling tools in the original blueprint (create wall, extrude, transform, materials, preview and undo) belong to a 3D modelling engine. O-Studio is the practice-management app and has none, so they are not built here.

## Licence

Tulu 3 8B is built on Llama 3.1 and carries the Llama 3.1 Community License, not Apache 2.0. The llama.cpp server is MIT. Read the model licence, and the terms of the specific model file, before bundling the weights in an installer or on a preconfigured machine. Ai2 also lists a Tulu 3.1 8B checkpoint, which is worth evaluating before the model is fixed.

## Running the live test

```
set TULU_DIR=C:\path\with\runtime-and-models
dotnet test native\Osas.Core.Tests --filter LocalRuntimeLiveTests
```

It starts the real server, asks one question, stops it, and checks that no `llama-server` process is left.

## Checked on a real model

On 2026-10-29 the live tests ran against llama.cpp build b11972 (Windows CPU x64) and `Llama-3.1-Tulu-3-8B-Q4_K_M.gguf` (4,920,780,992 bytes, SHA-256 `12c3f945…383c`, from `bartowski/Llama-3.1-Tulu-3-8B-GGUF`): the server started, the checksum matched, Tulu answered a plain question, asked for a tool in the JSON format, read the result and answered from it, and stopping left no `llama-server` process. This was one machine and two questions, so it shows that the path works, not how well.

## Not yet done

- Measuring quality (quantized against reference), memory use and response time on the smallest target machine. CPU-only is slow on a small machine; a GPU build of llama.cpp (CUDA or Vulkan) is a drop-in replacement for the server folder.
- Installer packaging of the runtime and weights (installers are on hold), offline recovery, and an option to disable the assistant.
- A check that Tulu follows the JSON tool-request format reliably across the tool set. Prompt-based tool use is less dependable than native tool-calling, so it needs a test set of real questions.
