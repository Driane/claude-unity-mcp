# claude-unity-mcp

An MCP server that lets Claude Code drive the Unity Editor: read and edit the scene, configure any
component, author assets and prefabs, write C# and check that it compiled, import files, take
screenshots and build the game.

## How it works

```
Claude Code ──stdio (MCP)──▶ mcp-server (Node.js) ──HTTP, localhost:6400──▶ Unity Editor bridge (C#)
```

- **`mcp-server/`** exposes 37 tools over the Model Context Protocol and forwards each call to Unity
  as a JSON command. Timeouts are per tool, so a build gets 15 minutes while a scene query gets 15 seconds.
- **`unity-plugin/Editor/`** is an Editor-only plugin that starts an `HttpListener` on
  `localhost:6400` when Unity loads. Requests are accepted on a background thread and queued; the
  commands themselves run on Unity's main thread (`EditorApplication.update`), because the Unity API
  is not thread-safe.
- Scene changes go through `Undo`, so Ctrl+Z reverts anything Claude did.
- No dependencies on the Unity side: the plugin ships its own JSON parser and serializer.

### Plugin layout

| File | Contents |
|---|---|
| `ClaudeUnityBridge.cs` | HTTP listener, command dispatch, logs, compile capture, shared utilities |
| `ClaudeJson.cs` | JSON reader/writer |
| `ClaudeCommands.Scene.cs` | Scene, GameObject, component and prefab commands |
| `ClaudeCommands.Assets.cs` | Scripts, materials, assets, textures, scenes, lighting |
| `ClaudeCommands.Project.cs` | Packages, menu items, screenshots, play mode, builds |

## Tools

### Inspection

| Tool | Effect |
|---|---|
| `unity_status` | Active scene, play state, compile state, Unity version, project path |
| `unity_get_hierarchy` | GameObject tree, narrowable by `root` and `max_depth` |
| `unity_find_gameobjects` | Search by partial name, component type and/or tag, inactive objects included |
| `unity_get_gameobject` | One object in full: transform, tag, layer, children, and every component with its property names and current values |
| `unity_get_logs` | Recent console messages, errors only on request |
| `unity_compile_status` | Whether Unity is compiling, plus the last compile's errors and warnings |

### Scene editing

| Tool | Effect |
|---|---|
| `unity_create_gameobject` | Empty or primitive, with parent, placement and components in one call |
| `unity_delete_gameobject` | Delete by name or path |
| `unity_duplicate_gameobject` | Up to 500 copies, each offset from the last, prefab links kept |
| `unity_rename_gameobject` | Rename |
| `unity_set_active` | Enable or disable |
| `unity_set_parent` | Reparent, or move to the scene root |
| `unity_set_transform` | Position, rotation (Euler), scale — world or local |
| `unity_set_tag_layer` | Tag and layer, optionally for all children |

### Components

| Tool | Effect |
|---|---|
| `unity_add_component` | Add a component, optionally setting its properties straight away |
| `unity_remove_component` | Remove a component |
| `unity_set_component_property` | Set **any** serialized property — see below |

`unity_set_component_property` is the workhorse. It goes through `SerializedObject`, so it reaches
built-in components and the fields of your own scripts alike:

- accepts the inspector name (`mass`), the serialized name (`m_Mass`) or a dotted path
  (`items.Array.data[0]`);
- object references take an asset path (`Assets/Art/hero.png`) or a scene path (`Player/Model`);
- colors take `[r,g,b,a]` in 0–1 or `"#RRGGBB"`; vectors take `[x,y,z]`; enums take their name;
- on an unknown name it answers with the list of valid ones, so a wrong guess self-corrects.

### Assets

| Tool | Effect |
|---|---|
| `unity_write_script` | Write a C# script and reimport it |
| `unity_create_prefab` | Save a scene object as a prefab |
| `unity_instantiate_prefab` | Place a prefab, or up to 500 at once via `placements` |
| `unity_create_material` | Create or replace a material, shader auto-picked for the render pipeline |
| `unity_assign_material` | Assign a material to a renderer, one slot or all, optionally recursive |
| `unity_create_asset` | Create a ScriptableObject asset and fill its fields |
| `unity_import_file` | Copy a file from disk into the project and import it |
| `unity_set_texture_settings` | Texture import settings (sprite mode, filtering, compression, mipmaps) |
| `unity_list_assets` | List assets by `AssetDatabase` filter, e.g. `t:Prefab` |
| `unity_delete_asset` | Delete assets permanently |

### Scenes and project

| Tool | Effect |
|---|---|
| `unity_save_scene` | Save the open scenes, or save-as to a path |
| `unity_open_scene` | Open a scene, single or additive |
| `unity_new_scene` | New scene, empty or with the default objects |
| `unity_set_lighting` | Ambient light, skybox, fog |
| `unity_install_package` | Add a Unity package, e.g. `com.unity.probuilder` |
| `unity_list_packages` | List the project's package dependencies |
| `unity_execute_menu_item` | Run any Editor menu item — the escape hatch |
| `unity_capture_screenshot` | Render a camera to a PNG and return its path, so Claude can look at it |
| `unity_set_play_mode` | Enter or exit Play Mode |
| `unity_build_player` | Build the game, reporting result, size, duration and error count |

`unity_open_scene` and `unity_new_scene` refuse to throw away unsaved work: they return the list of
dirty scenes until you pass `save: true` or `discard: true`. A modal "save changes?" dialog would
hang the bridge, so the choice is made up front.

## Setup

1. Copy the whole `unity-plugin/Editor/` folder into your project's `Assets/Editor/`. The console
   shows `[ClaudeUnity] Bridge listening on http://localhost:6400/`.
2. Install the server:
   ```bash
   cd mcp-server
   npm install
   ```
3. Check the connection, with Unity open:
   ```bash
   npm run smoke
   ```
4. Register it with Claude Code:
   ```bash
   claude mcp add unity -- node /absolute/path/to/mcp-server/index.js
   ```
5. Ask Claude something like: *"Create a cube named Player at (0, 1, 0), give it a Rigidbody with
   mass 3, write a PlayerController that moves it with the arrow keys, attach it, check it compiled,
   then enter Play Mode and show me a screenshot."*

Requirements: Node.js 18 or later, Unity 2020.2 or later (the plugin uses C# 8).
Set `CLAUDE_UNITY_URL` to point the server at a different host or port.

## Typical loops this enables

- **Write code and know it worked** — `write_script`, then `compile_status`, then `add_component`.
  Claude no longer attaches a script that failed to compile.
- **See the result** — `capture_screenshot` returns a PNG path that Claude can read, so it can judge
  its own scene instead of describing it blind.
- **Lay out a level** — one `instantiate_prefab` call with 200 placements instead of 200 calls.
- **Author data** — `create_asset` builds ScriptableObjects, so items and levels are data, not code.
- **Bring art in** — `import_file` moves a Blender export or a generated texture into the project,
  then `set_texture_settings` configures it (crisp pixel art: `Point`, `Uncompressed`, no mipmaps).
- **Ship** — `build_player`, with the result and error count reported back.

## Not covered yet

- **Running tests.** The Test Framework's API is asynchronous and lives in an optional package, so
  referencing it would break the plugin's compile in projects without it. It needs its own assembly
  definition — a separate job.
- **ProBuilder, Timeline and Animator** have no dedicated tools. `execute_menu_item` and
  `set_component_property` reach much of them in the meantime.
- **Lightmap baking**, which is long-running and would need the same polling model as tests.

## Verifying a change

The plugin compiles against the real Unity assemblies without opening the Editor, which catches a
wrong API name immediately:

```bash
U="/c/Program Files/Unity/Hub/Editor/<version>/Editor/Data"
"$U/NetCoreRuntime/dotnet.exe" "$U/DotNetSdkRoslyn/csc.dll" \
  -target:library -nostdlib+ -langversion:9.0 \
  -r:"$U/NetStandard/ref/2.1.0/netstandard.dll" \
  $(for d in "$U/Managed/UnityEngine/"*.dll; do echo -r:"$d"; done) \
  unity-plugin/Editor/*.cs -out:/tmp/ClaudeUnity.dll
```

## Security

The bridge listens on `localhost` only and accepts nothing but POST requests carrying the
`X-Claude-Unity-Bridge` header, which the MCP server sends. A web page cannot add that header to a
cross-origin request without a CORS preflight, and the bridge refuses preflights, so websites open
in your browser cannot drive the Editor.

It remains a local development tool: any local process that knows the header can write scripts into
the open project, import files from anywhere on disk, delete assets and start a build.
