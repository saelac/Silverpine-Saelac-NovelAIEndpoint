# NovelAI Endpoint

Silverpine BepInEx mod that adds NovelAI as a hosted text-generation endpoint, including streaming responses, model selection, request pacing, and encrypted per-provider API-key storage.

Created by **Saelac and ChatGPT**.

**Current version:** 1.2.6

## Installation

Build the project and place `NovelAIEndpoint.dll` under `BepInEx/plugins/`. Configure the endpoint and API key through the in-game settings; do not store plaintext credentials in the repository.

## Building

The project targets `netstandard2.1` and requires the local Silverpine managed assemblies referenced by the project file.
