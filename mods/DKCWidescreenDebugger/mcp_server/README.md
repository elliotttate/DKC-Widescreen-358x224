# SuperZSNES Debug MCP

This stdio MCP server gives an LLM live access to the DKC Widescreen Debugger BepInEx plugin. It uses the official Python MCP SDK and communicates with the game through an authenticated TCP listener bound only to `127.0.0.1`.

## Architecture

`LLM host -> stdio MCP server -> token-authenticated loopback bridge -> Unity main thread -> SuperZSNES cores`

The BepInEx plugin writes its active endpoint and random per-run token to:

`<game>\BepInEx\plugins\DKCWidescreenDebugger\bridge.json`

The file exists only while the plugin is running. Every emulator operation is queued onto Unity's main thread before it touches game state.

## Run

With SuperZSNES running:

```powershell
$env:SUPERZSNES_GAME_DIR = '<superzsnes>'
uv run --project '<superzsnes>\BepInEx\plugins\DKCWidescreenDebugger\mcp_server' superzsnes-mcp
```

The process speaks MCP over stdin/stdout; a terminal will appear idle when it is working correctly. Normally an MCP-capable host launches it for you.

Generic stdio client configuration:

```json
{
  "mcpServers": {
    "superzsnes": {
      "command": "uv",
      "args": [
        "run",
        "--project",
        "D:\\Downloads\\SuperZSNES_v0.230\\BepInEx\\plugins\\DKCWidescreenDebugger\\mcp_server",
        "superzsnes-mcp"
      ],
      "env": {
        "SUPERZSNES_GAME_DIR": "D:\\Downloads\\SuperZSNES_v0.230"
      }
    }
  }
}
```

You may set `SUPERZSNES_BRIDGE_FILE` instead when the endpoint file is in a nonstandard location.

## Tool surface

| Area | Tools |
| --- | --- |
| Lifecycle | `zsnes_status`, `zsnes_get_rom_info`, `zsnes_load_rom`, `zsnes_reset`, `zsnes_pause`, `zsnes_resume`, `zsnes_step_frame`, `zsnes_set_controller`, `zsnes_bridge_info` |
| Native states | `zsnes_save_state`, `zsnes_load_state`, `zsnes_load_state_file` |
| Evidence | `zsnes_screenshot`, `zsnes_capture`, `zsnes_list_captures`, `zsnes_recent_events` |
| CPU | `zsnes_cpu_state`, `zsnes_disassemble` |
| PPU | `zsnes_ppu_state` |
| Memory | `zsnes_read_memory`, `zsnes_write_memory`, `zsnes_get_watches` |
| Search | `zsnes_search_begin_unknown`, `zsnes_search_begin_exact`, `zsnes_search_filter`, `zsnes_search_results` |
| Break/trace | `zsnes_get_debug_config`, `zsnes_set_debug_config` |
| Widescreen | `zsnes_get_widescreen`, `zsnes_set_widescreen` |
| Isolation | `zsnes_set_layers`, `zsnes_set_renderer_debug` |

The server also exposes `superzsnes://status` and `superzsnes://debug/config` resources and a `dkc_widescreen_debug_workflow` prompt.

`zsnes_screenshot` returns an actual MCP image block that multimodal LLM hosts can inspect immediately. Choose `main` for the emulator render, `composed` for the final composed texture (with automatic main fallback), or `window` for the full Unity window including overlays. Use `format="jpeg", quality=85` for a smaller `.jpg`, or `format="png"` for lossless output. Every grabbed image is also saved under the current session's `screenshots` directory.

## Safety model

- The bridge never listens outside localhost.
- A new 128-bit random token is required for every game run.
- The MCP process discovers the token from a local file rather than putting it in client configuration.
- Requests and responses have size limits and timeouts.
- CPU and read-memory hooks are dynamically removed when unused.
- Memory-write tools explicitly require the caller to choose an address and data; the server never patches ROM or game files.

## Tests

```powershell
uv run --project . --with pytest pytest -q
```

The tests validate the authenticated wire encoding and the complete MCP tool registry. A live smoke test is performed by installing the plugin, launching SuperZSNES, verifying BepInEx's log, and calling `zsnes_status` through an MCP client.
