#!/usr/bin/env python3
"""Append placeholder C# script resources to a Godot 4.x GDPC v3 (unencrypted) pck.

Background: MegaCrit's private "MegaDot" engine packs the game with ZERO .cs
resources in the pck, relying on a private uid->AOT-type mechanism. Stock Godot
4.5.1, however, loads a scene's `[ext_resource type="Script" path="res://...cs"]`
by calling ResourceLoader::load(res://...cs) -> ResourceFormatLoaderCSharpScript,
which needs the .cs file to physically exist in the pck. It then maps the path to
the AOT-compiled type via the ScriptPathAttribute baked into sts2.dll.

Ground truth (verified against an official Godot 4.5.1 C# export): the packed .cs
file's CONTENT is irrelevant -- the official exporter stores a 1-byte placeholder
(a single '\n'). Only the file's EXISTENCE at the exact res:// path matters.

This tool appends a 1-byte '\n' placeholder for every path listed in a paths file
(one res://...cs path per line). The pck directory lives at the end of the file,
so we: overwrite the old directory region with the new file data, then write the
rebuilt directory (old entries untouched + new entries) after it, and fix the
header's dir_offset. Only the tail of the (1.9 GB) file is rewritten.

Usage: pck_add_cs.py <src.pck> <paths.txt> <dst.pck>
"""
import struct, sys, os, shutil, hashlib

PLACEHOLDER = b"\n"                      # matches official export (1 byte)
PMD5 = hashlib.md5(PLACEHOLDER).digest()

def patch(src, paths_file, dst):
    with open(paths_file) as fh:
        want = [ln.strip() for ln in fh if ln.strip()]
    # de-dup preserving order
    seen = set(); paths = []
    for p in want:
        if p not in seen:
            seen.add(p); paths.append(p)

    print(f"copying {src} -> {dst} ...")
    shutil.copyfile(src, dst)
    f = open(dst, "r+b")

    assert f.read(4) == b"GDPC", "bad magic"
    fmt = struct.unpack("<I", f.read(4))[0]
    assert fmt >= 3, f"only format v3+ supported, got {fmt}"
    f.read(12)                                   # version major/minor/patch
    flags = struct.unpack("<I", f.read(4))[0]
    assert not (flags & 1), "directory encrypted"
    files_base = struct.unpack("<Q", f.read(8))[0]
    dir_offset = struct.unpack("<Q", f.read(8))[0]
    rel = bool(flags & 2)
    dir_off_field_pos = 8 + 12 + 4 + 8           # byte offset of dir_offset in header

    # read existing directory
    f.seek(dir_offset)
    count = struct.unpack("<I", f.read(4))[0]
    entries = []                                  # [plen, pathbytes, off_stored, size, md5, eflags]
    existing = set()
    for _ in range(count):
        plen = struct.unpack("<I", f.read(4))[0]
        pathb = f.read(plen)
        off = struct.unpack("<Q", f.read(8))[0]
        size = struct.unpack("<Q", f.read(8))[0]
        md5 = f.read(16)
        ef = struct.unpack("<I", f.read(4))[0]
        entries.append([plen, pathb, off, size, md5, ef])
        existing.add(pathb.rstrip(b"\x00").decode("utf-8"))
    dir_end = f.tell()
    file_size = os.path.getsize(dst)
    assert dir_end == file_size, (
        f"directory not last section (dir_end={dir_end:#x} file_size={file_size:#x})")

    # skip paths already present
    new_paths = [p for p in paths if p not in existing]
    print(f"{len(paths)} requested, {len(new_paths)} new (skipped {len(paths)-len(new_paths)} already present)")

    # Godot stores path padded to a 4-byte boundary in the directory entry.
    def pad_path(p):
        pb = p.encode("utf-8")
        pad = (4 - (len(pb) % 4)) % 4
        return pb + b"\x00" * pad

    # 1. write new file data over the old directory region, keep 8-byte alignment
    data_start = dir_offset
    f.seek(data_start)
    cur = data_start
    new_entries = []
    for p in new_paths:
        # 8-byte align each file's start (harmless; Godot reads by exact offset)
        pad = (8 - (cur % 8)) % 8
        if pad:
            f.write(b"\x00" * pad); cur += pad
        actual_off = cur
        f.write(PLACEHOLDER); cur += len(PLACEHOLDER)
        stored = actual_off - files_base if rel else actual_off
        pb = pad_path(p)
        new_entries.append([len(pb), pb, stored, len(PLACEHOLDER), PMD5, 0])

    # align before directory
    pad = (8 - (cur % 8)) % 8
    if pad:
        f.write(b"\x00" * pad); cur += pad
    new_dir_offset = cur

    # 2. write rebuilt directory (old entries verbatim + new ones)
    all_entries = entries + new_entries
    out = bytearray(struct.pack("<I", len(all_entries)))
    for plen, pathb, off, size, md5, ef in all_entries:
        out += struct.pack("<I", plen) + pathb
        out += struct.pack("<Q", off) + struct.pack("<Q", size) + md5
        out += struct.pack("<I", ef)
    f.seek(new_dir_offset); f.write(out)
    f.truncate(new_dir_offset + len(out))

    # 3. fix header dir_offset
    f.seek(dir_off_field_pos)
    f.write(struct.pack("<Q", new_dir_offset))
    f.close()

    print(f"directory: {count} -> {len(all_entries)} entries")
    print(f"dir_offset: {dir_offset:#x} -> {new_dir_offset:#x}")
    print(f"file size: {file_size} -> {new_dir_offset + len(out)}")

if __name__ == "__main__":
    patch(sys.argv[1], sys.argv[2], sys.argv[3])
    print("OK")
