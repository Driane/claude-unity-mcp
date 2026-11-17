# claude-unity-mcp

An MCP server that lets Claude Code drive the Unity Editor: read the scene, create and edit GameObjects, add components, write C# scripts, enter Play Mode and read the console.

## How it works

```
Claude Code ──stdio (MCP)──▶ mcp-server (Node.js) ──HTTP, localhost:6400──▶ Unity Editor bridge (C#)
```

- **`mcp-server/`** exposes ten tools over the Model Context Protocol and forwards each call to Unity as a JSON command.
- **`unity-plugin/Editor/ClaudeUnityBridge.cs`** is an Editor script that starts an `HttpListener` on `localhost:6400` when Unity loads. Requests are accepted on a background thread and queued; the commands themselves run on Unity's main thread (`EditorApplication.update`), because the Unity API is not thread-safe.
- Scene changes go through `Undo`, so Ctrl+Z reverts anything Claude did.
- No dependencies on the Unity side: the bridge ships its own minimal JSON parser and serializer.

## Tools

| Tool | Effect |
|---|---|
| `unity_status` | Active scene, play state, Unity version |
| `unity_get_hierarchy` | Full GameObject tree of the active scene, with components |
| `unity_create_gameobject` | Empty object or primitive, with optional parent and position |
| `unity_delete_gameobject` | Delete by name or hierarchy path |
| `unity_write_script` | Write a C# script under `Assets/Scripts/` and trigger a recompile |
| `unity_add_component` | Add a component (`Rigidbody`, `BoxCollider`, your own scripts…) |
| `unity_set_transform` | Position, rotation (Euler) and scale |
| `unity_set_play_mode` | Enter or exit Play Mode |
| `unity_save_scene` | Save the open scenes |
| `unity_get_logs` | Recent console messages, errors included |

## Setup

1. Copy `unity-plugin/Editor/ClaudeUnityBridge.cs` into your project's `Assets/Editor/` folder. The console shows `[ClaudeUnity] Bridge listening on http://localhost:6400/`.
2. Install the server:
   ```bash
   cd mcp-server
   npm install
   ```
3. Register it with Claude Code:
   ```bash
   claude mcp add unity -- node /absolute/path/to/mcp-server/index.js
   ```
4. Ask Claude something like: *"Create a cube named Player at (0, 1, 0), add a Rigidbody, write a PlayerController script that moves it with the arrow keys, attach it and enter Play Mode."*

Requirements: Node.js 18 or later, Unity 2020.2 or later (the bridge uses C# 8).

## Security

The bridge listens on `localhost` only and accepts nothing but POST requests carrying the `X-Claude-Unity-Bridge` header, which the MCP server sends. A web page cannot add that header to a cross-origin request without a CORS preflight, and the bridge refuses preflights, so websites open in your browser cannot drive the Editor.

It remains a local development tool: any local process that knows the header can write scripts into the open project.
