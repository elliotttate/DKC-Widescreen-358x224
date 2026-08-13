import ida_auto
import ida_bytes
import ida_kernwin
import idc
import idautils


def main():
    ida_auto.auto_wait()
    start = 0x10393D70
    end = 0x10393DF0
    for ea in idautils.Heads(start, end):
        line = idc.generate_disasm_line(ea, 0) or ""
        size = idc.get_item_size(ea)
        raw = ida_bytes.get_bytes(ea, size) or b""
        print(f"{ea:08X}: {raw.hex(' ').upper():<28} {line}")
    idc.qexit(0)


main()
