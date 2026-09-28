"""比较 Dialogs 只读捕获的屏幕矩形；不读取或修改 Unity 资产。"""
import argparse
import json
from collections import Counter
from pathlib import Path


def read(path):
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    return [item for item in data.get("entries", data.get("objects", []))
            if item.get("active", True)], data


def compare(before_path, after_path):
    before, before_data = read(before_path)
    after, after_data = read(after_path)
    matches, unmatched = [], []
    used = set()
    # 重排父级后以最长共同路径后缀配对，后缀在两份报告中都必须唯一。
    before_suffixes = Counter()
    after_suffixes = Counter()
    by_suffix = {}
    for rows, counts in ((before, before_suffixes), (after, after_suffixes)):
        for item in rows:
            parts = item["path"].split("/")
            for length in range(1, len(parts) + 1):
                suffix = "/".join(parts[-length:])
                counts[suffix] += 1
                if rows is after:
                    by_suffix[suffix] = item
    for item in before:
        parts = item["path"].split("/")
        target = None
        for length in range(len(parts), 0, -1):
            suffix = "/".join(parts[-length:])
            if before_suffixes[suffix] == after_suffixes[suffix] == 1:
                candidate = by_suffix[suffix]
                if candidate["path"] not in used:
                    target = candidate
                    break
        if target is None:
            unmatched.append(item["path"])
            continue
        used.add(target["path"])
        a, b = item["screenRect"], target["screenRect"]
        difference = {key: b[key] - a[key] for key in ("x", "y", "width", "height")}
        matches.append(dict(beforePath=item["path"], afterPath=target["path"],
                            before=a, after=b, delta=difference,
                            maxAbsoluteDelta=max(map(abs, difference.values()))))
    return dict(before=str(before_path), after=str(after_path),
                beforeSize=before_data.get("actualSize", [before_data.get("width"), before_data.get("height")]),
                afterSize=after_data.get("actualSize", [after_data.get("width"), after_data.get("height")]),
                activeBefore=len(before), matched=len(matches), unmatched=unmatched,
                differences=[m for m in matches if m["maxAbsoluteDelta"] > .02],
                matches=matches,
                limitation="仅匹配当前活动节点；新对象、隐藏状态、无唯一配对对象及场景外对象不据此判定通过。差异必须人工分类，不能自动豁免。")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = compare(args.before, args.after)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("activeBefore", "matched", "unmatched")}, ensure_ascii=False))
    for change in result["differences"]:
        print(json.dumps({"path": change["beforePath"], "delta": change["delta"]}, ensure_ascii=False))
