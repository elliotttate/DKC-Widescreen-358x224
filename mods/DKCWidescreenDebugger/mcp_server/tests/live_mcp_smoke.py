from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def run() -> None:
    project = Path(__file__).resolve().parents[1]
    environment = os.environ.copy()
    parameters = StdioServerParameters(
        command="uv",
        args=["run", "--project", str(project), "superzsnes-mcp"],
        env=environment,
    )
    async with stdio_client(parameters) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = await session.list_tools()
            assert len(tools.tools) == 32, len(tools.tools)
            result = await session.call_tool("zsnes_status", {})
            assert not result.isError, result
            text = result.content[0].text
            status = json.loads(text)
            assert status["attached"] is True, status
            print(json.dumps({"tool_count": len(tools.tools), "status": status}, indent=2))


if __name__ == "__main__":
    asyncio.run(run())
