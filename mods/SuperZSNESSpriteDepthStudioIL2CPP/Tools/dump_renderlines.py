"""Dump the annotated v0.300 PPURenderer.RenderLines native body from IDA."""

import os

import ida_auto
import ida_bytes
import ida_funcs
import ida_lines
import ida_pro
import idc


START = 0x10392470
END = 0x10393DF0


def main():
    ida_auto.auto_wait()
    output = os.environ.get("SPRITE_DEPTH_DUMP")
    if not output:
        raise RuntimeError("SPRITE_DEPTH_DUMP is not set")
    function = ida_funcs.get_func(START)
    if function is None or function.start_ea != START or function.end_ea != END:
        raise RuntimeError("RenderLines function boundary does not match the audited build")
    lines = []
    ea = START
    while ea < END:
        size = idc.get_item_size(ea)
        if size <= 0:
            size = 1
        raw = ida_bytes.get_bytes(ea, size) or b""
        disassembly = ida_lines.generate_disasm_line(ea, 0) or ""
        disassembly = ida_lines.tag_remove(disassembly)
        lines.append(f"{ea:08X}  {raw.hex(' ').upper():<34}  {disassembly}")
        ea += size
    with open(output, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")


exit_code = 0
try:
    main()
except Exception as exception:
    print(f"[sprite-depth-dump] {exception}")
    exit_code = 1
finally:
    ida_pro.qexit(exit_code)
