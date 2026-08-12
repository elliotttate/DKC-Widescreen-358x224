from __future__ import annotations

import base64
from typing import Any, Literal

from mcp.server.fastmcp import FastMCP, Image

from .bridge import client, endpoint_path


mcp = FastMCP(
    "SuperZSNES Debugger",
    instructions=(
        "Live debugger for SuperZSNES and Donkey Kong Country widescreen work. "
        "Begin with zsnes_status. Pause before writes. Prefer narrow read/write/PC ranges because tracing hot emulator paths is expensive. "
        "Use captures to preserve WRAM, VRAM, CGRAM, OAM, PPU state, renderer state, and frame PNGs for offline analysis."
    ),
    json_response=True,
)


@mcp.tool()
def zsnes_status() -> dict[str, Any]:
    """Get bridge/attachment, pause, timing, PC, tracing, and session status."""
    return client.call("get_status")


@mcp.tool()
def zsnes_get_rom_info() -> dict[str, Any]:
    """Get whether a ROM is loaded plus parsed SNES header, mapper, vectors, title, region, and chip information."""
    return client.call("get_rom_info")


@mcp.tool()
def zsnes_load_rom(path: str, load_last_state: bool = False) -> dict[str, Any]:
    """Load a .zip/.smc/.sfc/.swc/.ufo ROM from an explicit local path and initialize all emulator cores."""
    return client.call("load_rom", path=path, load_last_state=load_last_state)


@mcp.tool()
def zsnes_reset() -> dict[str, Any]:
    """Hard-reset the loaded SNES ROM and re-arm breakpoints."""
    return client.call("reset")


@mcp.tool()
def zsnes_save_state(suffix: str = "-mcp") -> dict[str, Any]:
    """Save a native SuperZSNES state using a safe suffix such as '-mcp-before-boss'."""
    return client.call("save_state", suffix=suffix)


@mcp.tool()
def zsnes_load_state(suffix: str = "-mcp") -> dict[str, Any]:
    """Load the native SuperZSNES state for a previously used safe suffix."""
    return client.call("load_state", suffix=suffix)


@mcp.tool()
def zsnes_load_state_file(path: str) -> dict[str, Any]:
    """Load a specific existing .szst native save-state file from an explicit local path."""
    return client.call("load_state_file", path=path)


@mcp.tool()
def zsnes_pause() -> dict[str, Any]:
    """Pause SNES emulation. The currently executing emulated frame can finish first."""
    return client.call("pause")


@mcp.tool()
def zsnes_resume() -> dict[str, Any]:
    """Resume SNES emulation and re-arm the latched breakpoint."""
    return client.call("resume")


@mcp.tool()
def zsnes_step_frame() -> dict[str, Any]:
    """Keep emulation paused and advance exactly one SNES frame."""
    return client.call("step_frame")


@mcp.tool()
def zsnes_set_controller(buttons: str = "", frames: int = 0, controller: int = 1) -> dict[str, Any]:
    """Inject named SNES buttons for an exact number of emulated frames. Example: buttons='RIGHT,B', frames=120. Use frames=0 to release."""
    return client.call("set_controller", buttons=buttons, frames=frames, controller=controller)


@mcp.tool()
def zsnes_capture(reason: str = "mcp") -> dict[str, Any]:
    """Save a full diagnostic bundle: WRAM/SRAM/VRAM/CGRAM/OAM/I/O, CPU/PPU/renderer state, settings, and render PNGs."""
    return client.call("capture", reason=reason)


@mcp.tool()
def zsnes_screenshot(
    target: Literal["main", "sub", "composed", "window"] = "main",
    format: Literal["png", "jpeg"] = "png",
    quality: int = 85,
) -> Image:
    """Grab a live MCP image. main/sub are raw PPU planes; composed is the full color-math result."""
    result = client.call("screenshot", target=target, format=format, quality=quality)
    image_format = "jpeg" if result["mimeType"] == "image/jpeg" else "png"
    return Image(data=base64.b64decode(result["base64"]), format=image_format)


@mcp.tool()
def zsnes_cpu_state() -> dict[str, Any]:
    """Get complete 65C816 registers, flags, cycles, current PC, timing, and current disassembly text."""
    return client.call("get_cpu_state")


@mcp.tool()
def zsnes_ppu_state() -> dict[str, Any]:
    """Get PPU latches/scroll/Mode 7 state, important $21xx registers, full I/O image as base64, and renderer controls."""
    return client.call("get_ppu_state")


@mcp.tool()
def zsnes_disassemble(address: str) -> dict[str, Any]:
    """Disassemble one 65C816 instruction at a 24-bit SNES hex address such as 80ABCD."""
    return client.call("disassemble_at", address=address)


@mcp.tool()
def zsnes_read_memory(address: str, length: int = 1) -> dict[str, Any]:
    """Read 1-65536 bytes from the SNES 24-bit address map; returns contiguous hex and base64."""
    return client.call("read_memory", address=address, length=length)


@mcp.tool()
def zsnes_write_memory(address: str, hex_data: str) -> dict[str, Any]:
    """Write bytes into the live SNES address map. Pause first. hex_data accepts forms like '01 FF 20' or '01FF20'."""
    return client.call("write_memory", address=address, hex=hex_data)


@mcp.tool()
def zsnes_get_debug_config() -> dict[str, Any]:
    """Get live typed watches, breakpoints, trace filters, watchpoint ranges, trace toggles, and safety limits."""
    return client.call("get_debug_config")


@mcp.tool()
def zsnes_set_debug_config(
    watches: str | None = None,
    execute_breakpoints: str | None = None,
    trace_pc_ranges: str | None = None,
    write_watchpoints: str | None = None,
    read_watchpoints: str | None = None,
    cpu_trace: bool | None = None,
    ppu_trace: bool | None = None,
    pause_on_watch_change: bool | None = None,
    capture_on_breakpoint: bool | None = None,
    max_instructions_per_frame: int | None = None,
) -> dict[str, Any]:
    """Configure debugging. Addresses are 24-bit hex/ranges; watches use address:type:name, e.g. 7E1234:s16:camera_x."""
    return client.call(
        "set_debug_config",
        watches=watches,
        execute_breakpoints=execute_breakpoints,
        trace_pc_ranges=trace_pc_ranges,
        write_watchpoints=write_watchpoints,
        read_watchpoints=read_watchpoints,
        cpu_trace=cpu_trace,
        ppu_trace=ppu_trace,
        pause_on_watch_change=pause_on_watch_change,
        capture_on_breakpoint=capture_on_breakpoint,
        max_instructions_per_frame=max_instructions_per_frame,
    )


@mcp.tool()
def zsnes_get_watches() -> list[dict[str, Any]]:
    """Read all configured typed WRAM watches with raw and formatted values."""
    return client.call("get_watches")


@mcp.tool()
def zsnes_search_begin_unknown(limit: int = 128) -> dict[str, Any]:
    """Start an unknown-value scan over all 128 KiB WRAM, useful for finding camera/player state."""
    return client.call("search_begin_unknown", limit=limit)


@mcp.tool()
def zsnes_search_begin_exact(value: str, limit: int = 128) -> dict[str, Any]:
    """Start a byte-exact WRAM scan. value is hexadecimal 00-FF."""
    return client.call("search_begin_exact", value=value, limit=limit)


@mcp.tool()
def zsnes_search_filter(
    comparison: Literal["exact", "changed", "unchanged", "increased", "decreased"],
    value: str | None = None,
    limit: int = 128,
) -> dict[str, Any]:
    """Filter current WRAM candidates against the previous scan; exact additionally requires a hex byte value."""
    return client.call("search_filter", comparison=comparison, value=value, limit=limit)


@mcp.tool()
def zsnes_search_results(offset: int = 0, limit: int = 128) -> dict[str, Any]:
    """Page through current WRAM-search candidates and their live byte values."""
    return client.call("search_results", offset=offset, limit=limit)


@mcp.tool()
def zsnes_get_widescreen() -> dict[str, Any]:
    """Get all game-specific enhancement and widescreen parameters for the loaded ROM."""
    return client.call("get_widescreen")


@mcp.tool()
def zsnes_set_widescreen(
    enabled: bool | None = None,
    bg: int | None = None,
    obj: int | None = None,
    mode7: int | None = None,
    color: int | None = None,
    aspect_override: int | None = None,
    dkc_baseline: bool = False,
) -> dict[str, Any]:
    """Change live widescreen settings or apply SuperZSNES's DKC baseline (BG/OBJ 7, Mode7/color 0)."""
    return client.call(
        "set_widescreen", enabled=enabled, bg=bg, obj=obj, mode7=mode7,
        color=color, aspect_override=aspect_override, dkc_baseline=dkc_baseline,
    )


@mcp.tool()
def zsnes_set_layers(
    bg1_visible: bool | None = None,
    bg2_visible: bool | None = None,
    bg3_visible: bool | None = None,
    bg4_visible: bool | None = None,
    sprites_visible: bool | None = None,
    windows_visible: bool | None = None,
) -> dict[str, Any]:
    """Show/hide individual SNES BG layers, sprites, and window masks in the enhanced renderer."""
    return client.call(
        "set_layers", bg1_visible=bg1_visible, bg2_visible=bg2_visible,
        bg3_visible=bg3_visible, bg4_visible=bg4_visible,
        sprites_visible=sprites_visible, windows_visible=windows_visible,
    )


@mcp.tool()
def zsnes_set_renderer_debug(
    first_line: int | None = None,
    last_line: int | None = None,
    sprite_number: int | None = None,
    priority: int | None = None,
) -> dict[str, Any]:
    """Isolate renderer scanlines, one sprite number (-1 for all), or one priority (-1 for all)."""
    return client.call(
        "set_renderer_debug", first_line=first_line, last_line=last_line,
        sprite_number=sprite_number, priority=priority,
    )


@mcp.tool()
def zsnes_recent_events() -> list[str]:
    """Get recent watch changes, breakpoint hits, trace toggles, and capture notifications."""
    return client.call("get_recent_events")


@mcp.tool()
def zsnes_list_captures() -> list[dict[str, Any]]:
    """List diagnostic capture directories and every file available for offline analysis."""
    return client.call("list_captures")


@mcp.resource("superzsnes://status")
def status_resource() -> str:
    """Current live emulator status."""
    return str(client.call("get_status"))


@mcp.resource("superzsnes://debug/config")
def config_resource() -> str:
    """Current live debugger configuration."""
    return str(client.call("get_debug_config"))


@mcp.prompt()
def dkc_widescreen_debug_workflow(symptom: str = "bad graphics at a widescreen edge") -> str:
    """Provide a disciplined live-debug sequence for a DKC widescreen defect."""
    return f"""Investigate this SuperZSNES DKC widescreen symptom: {symptom}

1. Call zsnes_status, pause, inspect CPU and PPU state, then create a baseline capture.
2. Apply/confirm the DKC widescreen baseline and isolate BG/sprite/window layers.
3. If a camera or game-state variable is unknown, begin an unknown WRAM scan. Alternate movement and stillness with changed/unchanged filters; use increased/decreased for direction.
4. Promote likely results to typed watches, then apply a narrow write watchpoint to identify the updating PC.
5. Convert the PC into an execute breakpoint and enable CPU tracing only for a narrow surrounding range. Enable PPU trace only across the reproduction window.
6. Step frames and compare CPU, PPU scroll, important $21xx registers, and render output. Capture the first known-good and first bad frames.
7. Report exact addresses, PCs, register transitions, frame/scanline/dot timing, and which renderer isolation changes the artifact. Avoid conclusions that are not supported by the captured evidence."""


@mcp.tool()
def zsnes_bridge_info() -> dict[str, Any]:
    """Return the endpoint-file path used by this MCP process; does not require a running game."""
    return {"endpoint_file": str(endpoint_path())}


def main() -> None:
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
