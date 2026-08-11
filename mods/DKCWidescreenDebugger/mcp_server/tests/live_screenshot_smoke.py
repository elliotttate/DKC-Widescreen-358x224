from __future__ import annotations

import asyncio
import base64
import os
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def run() -> None:
    project = Path(__file__).resolve().parents[1]
    rom = os.environ.get("SUPERZSNES_TEST_ROM")
    if not rom:
        raise RuntimeError("Set SUPERZSNES_TEST_ROM to a locally owned DKC ROM before running this live smoke test.")
    parameters = StdioServerParameters(
        command="uv",
        args=["run", "--project", str(project), "superzsnes-mcp"],
        env=os.environ.copy(),
    )
    async with stdio_client(parameters) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            loaded = await session.call_tool("zsnes_load_rom", {"path": rom, "load_last_state": False})
            assert not loaded.isError, loaded
            await asyncio.sleep(1.0)
            png_result = await session.call_tool("zsnes_screenshot", {"target": "main", "format": "png"})
            assert not png_result.isError, png_result
            png_image = next(block for block in png_result.content if block.type == "image")
            png = base64.b64decode(png_image.data)
            assert png_image.mimeType == "image/png"
            assert png[:8] == b"\x89PNG\r\n\x1a\n"
            assert len(png) > 1000

            jpg_result = await session.call_tool(
                "zsnes_screenshot", {"target": "main", "format": "jpeg", "quality": 80}
            )
            assert not jpg_result.isError, jpg_result
            jpg_image = next(block for block in jpg_result.content if block.type == "image")
            jpg = base64.b64decode(jpg_image.data)
            assert jpg_image.mimeType == "image/jpeg"
            assert jpg[:2] == b"\xff\xd8" and jpg[-2:] == b"\xff\xd9"
            assert len(jpg) > 500
            assert len(jpg) < len(png), (len(jpg), len(png))
            print({"png_bytes": len(png), "jpeg_bytes": len(jpg), "jpeg_quality": 80})


if __name__ == "__main__":
    asyncio.run(run())
