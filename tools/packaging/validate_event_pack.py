#!/usr/bin/env python3
"""联合校验现有 core 格式事件：JSON Schema 加正式游戏的内容加载器。"""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"JSON 键重复：{key}")
        result[key] = value
    return result


def read_json(path):
    def reject_constant(value):
        raise ValueError(f"JSON 不允许非有限数值：{value}")
    with path.open(encoding="utf-8-sig") as source:
        return json.load(source, object_pairs_hook=unique_object, parse_constant=reject_constant)


def pack_file(root, relative):
    if not isinstance(relative, str) or not relative or any(ord(c) < 32 for c in relative):
        raise ValueError("定义路径必须是非空的包内相对路径")
    parts = relative.replace("\\", "/").split("/")
    if any(not part or part in (".", "..") or ":" in part for part in parts):
        raise ValueError(f"定义路径不是安全的包内相对路径：{relative}")
    path = root
    for part in parts:
        path = path / part
        attributes = path.lstat().st_file_attributes if os.name == "nt" else 0
        if path.is_symlink() or attributes & 0x400:
            raise ValueError(f"定义路径包含重解析点：{relative}")
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pack_root", type=Path, help="现有 core 格式内容包根目录")
    parser.add_argument("schema", type=Path, help="对应游戏版本的 content.schema.json")
    parser.add_argument("managed", type=Path, help="对应游戏版本的 *_Data/Managed 目录")
    args = parser.parse_args()
    try:
        from jsonschema import Draft202012Validator
        root = args.pack_root.resolve(strict=True)
        schema = read_json(args.schema)
        Draft202012Validator.check_schema(schema)
        validator = Draft202012Validator(schema)
        manifest = read_json(pack_file(root, "pack.json"))
        if not isinstance(manifest.get("definitions"), list):
            raise ValueError("pack.json 的 definitions 必须是数组")
        event_count = 0
        for relative in manifest["definitions"]:
            path = pack_file(root, relative)
            definition = read_json(path)
            if definition.get("contentType") != "event":
                continue
            event_count += 1
            for error in validator.iter_errors(definition):
                location = ".".join(str(part) for part in error.absolute_path) or "定义"
                raise ValueError(f"{relative}:{location}: {error.message}")
        print(f"事件 JSON Schema 校验通过：{event_count} 张。", flush=True)
        project = Path(__file__).resolve().parent / "EventPackValidator" / "EventPackValidator.csproj"
        command = ["dotnet", "run", "--project", str(project), "--", str(args.managed.resolve(strict=True)), str(root)]
        return subprocess.run(command, check=False).returncode
    except (ImportError, OSError, ValueError, json.JSONDecodeError) as error:
        print(f"事件校验失败：{error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
