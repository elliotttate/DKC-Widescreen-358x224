from __future__ import annotations

import asyncio

from superzsnes_mcp.server import mcp


def test_full_tool_surface() -> None:
    tools = asyncio.run(mcp.list_tools())
    names = {tool.name for tool in tools}
    assert {
        "zsnes_status", "zsnes_get_rom_info", "zsnes_load_rom", "zsnes_reset", "zsnes_save_state", "zsnes_load_state",
        "zsnes_load_state_file", "zsnes_pause", "zsnes_resume", "zsnes_step_frame", "zsnes_set_controller",
        "zsnes_capture", "zsnes_screenshot",
        "zsnes_cpu_state", "zsnes_ppu_state", "zsnes_disassemble", "zsnes_read_memory", "zsnes_write_memory",
        "zsnes_get_debug_config", "zsnes_set_debug_config", "zsnes_get_watches", "zsnes_search_begin_unknown",
        "zsnes_search_begin_exact", "zsnes_search_filter", "zsnes_search_results", "zsnes_get_widescreen",
        "zsnes_set_widescreen", "zsnes_set_layers", "zsnes_set_renderer_debug", "zsnes_recent_events",
        "zsnes_list_captures", "zsnes_bridge_info",
    } == names
