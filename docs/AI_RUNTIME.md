# Running Tulu inside O-Studio

O-Studio runs its assistant on a model on the same computer. It does not use a cloud service and does not depend on O-Draft, O-CoreOp or any other suite app. O-CoreOp and O-Draft run their own models.

## What is built

| Part | Where | State |
|---|---|---|
| Runtime manager: finds the bundled server and model, checks the model against its checksum, starts the server on a free loopback port, waits for `/health`, stops it | `native/Osas.Core/Agent/LocalRuntime.cs` | Built and unit-tested (checksum, missing file, manifest escaping its folder, arguments). Starting a real server has **not** been run: no `llama-server.exe` or model is on this machine |
| Server client for llama.cpp (`/v1/chat/completions`), sampling from the profile | `LlamaServerApi` in the same file | Built and tested against a fake server |
| Assistant loop, read-only tools, audit | `Assistant.cs`, `Suite/AssistantBar.cs`, `office-agent` | Built, in use |
| Fallback: Ollama on this computer, any `tulu3` tag | `OllamaApi.cs` | Built, tested against a fake server |
| Model and runtime files | not in the repository | **Not supplied.** Someone has to fetch and check them (see below) |

The assistant uses the bundled runtime when it finds one, and Ollama otherwise.

## Where the files go

Beside the program, or under `%LOCALAPPDATA%\O-Studio`:

```
runtime\windows-x64\llama-server.exe      (llama.cpp build for the target CPU / GPU)
models\tulu\model.gguf                    (the quantized Tulu checkpoint)
models\tulu\manifest.json
models\tulu\LICENSES\                     (licence texts shipped with the model)
```

`manifest.json`:

```json
{ "name": "Tulu 3 8B", "file": "model.gguf", "sha256": "<hex sha-256 of the file>", "license": "Llama 3.1 Community License", "context": 8192 }
```

The file name must stay inside the model folder. A model that does not match its checksum is refused with a clear message; a good result is remembered by file size and time so the check is not repeated on every start.

## Lifecycle

1. The model is not loaded when O-Studio starts. **Check for Tulu** only checks the files.
2. The first question starts the server (`-m <model> -c 8192 --host 127.0.0.1 --port <free port> --parallel 1 --no-webui`) and waits up to three minutes for it to report ready.
3. Questions go to that server only. Nothing leaves the computer.
4. **Unload model** stops it. Closing O-Studio stops it too, and so does the process exiting any other way.

## Profile (starting values, to be measured)

Temperature 0.3, top-p 0.9, 768 new tokens, 8,192 tokens of context, one generation at a time. 4-bit quantization must be compared against a higher-precision reference on a fixed set of tasks before it is accepted.

## Tools

Tulu 3's chat template has no tool-calling, so the tools are described in the prompt and the model replies with a small JSON object to use one. The tools are the existing read-only set (`office-agent`): the engine checks the signed-in person's role on every call, writes the audit entry, and nothing is changed. This is the allowlist; the model has no filesystem access or scripting.

The modelling tools in the original design (create wall, extrude, transform, materials, preview and undo) belong to a 3D modelling engine. O-Studio is the practice-management app and has none, so they are not built here.

## Licence

Tulu 3 8B is built on Llama 3.1 and carries the Llama 3.1 Community License, not Apache 2.0. Read it, and the terms of the specific model file, before bundling the weights in an installer or on a preconfigured machine. Ai2 also lists a Tulu 3.1 8B checkpoint to evaluate before the model is fixed.

## Not yet done

- Obtaining a compatible GGUF build, its checksum and a `llama-server.exe`, then running the real server end to end.
- Measuring quality (quantized against reference), memory and response times on the smallest target machine.
- Installer packaging of the runtime and weights (installers are on hold), offline recovery, and an option to disable the assistant.
- Acceptance tests that need the real model: offline launch, corrupt weights, shutdown releasing memory.
