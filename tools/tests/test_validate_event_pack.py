"""事件作者校验器的独立包夹具；不修改正式内容或开发者数据。"""

import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
SCHEMA = ROOT / "Assets/StreamingAssets/Content/core/content.schema.json"
TOOL = ROOT / "tools/packaging/validate_event_pack.py"
MANAGED = os.environ.get("NMC_EVENT_VALIDATOR_MANAGED", "")
PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aN0AAAAASUVORK5CYII=")


def fixture():
    root = ROOT / "TestResults/EventPackValidator" / uuid.uuid4().hex
    root.mkdir(parents=True, exist_ok=False)
    choice = lambda name, top, bottom: {"choiceId": name, "description": name,
        "button": {"topPercent": top, "bottomPercent": bottom}, "script": "choice.lua", "version": "1.0.0"}
    data = {"description": "测试事件", "color": 0, "resourceType": 0, "resourceAmount": 1,
        "representativeResourceType": 0, "representativeResourceAmount": 1,
        "eventChoiceSchemaVersion": 1, "choices": [choice("first", 0, 50), choice("second", 50, 100)]}
    card = {"definitionId": "fixture.event", "contentType": "event", "version": "1.0.0",
        "displayName": "测试事件", "artwork": "front.png", "tags": [], "playerMarkerZones": [],
        "expansionId": "test", "replaces": None, "abilities": [], "data": data}
    (root / "pack.json").write_text(json.dumps({"schemaVersion": 2, "packId": "test", "version": "1.0.0",
        "definitions": ["card.json"]}), encoding="utf-8")
    (root / "front.png").write_bytes(PNG)
    (root / "choice.lua").write_text("error('加载时不能执行脚本')\nreturn function(ctx) return {} end", encoding="utf-8")
    return root, card


def validate(root, card):
    (root / "card.json").write_text(json.dumps(card, ensure_ascii=False), encoding="utf-8")
    return subprocess.run([sys.executable, "-X", "utf8", str(TOOL), str(root), str(SCHEMA), MANAGED],
        cwd=ROOT, capture_output=True, text=True, encoding="utf-8")


class EventPackValidatorFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not MANAGED or not (Path(MANAGED) / "YC.Infrastructure.Lua.dll").is_file():
            raise RuntimeError("测试须通过 NMC_EVENT_VALIDATOR_MANAGED 指定目标游戏的 Managed 目录")

    def test_valid_and_immediate_entries_compile_without_executing(self):
        root, card = fixture()
        result = validate(root, card)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("正式加载器校验通过", result.stdout)
        root, card = fixture()
        card["data"]["choices"] = []
        card["data"]["immediateResolve"] = {"script": "choice.lua", "version": "1.0.0"}
        result = validate(root, card)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_schema_and_runtime_invariants_are_both_enforced(self):
        for fault in ("duplicate", "overlap", "syntax", "missing", "unknown", "legacy", "version", "both"):
            with self.subTest(fault=fault):
                root, card = fixture()
                data = card["data"]
                if fault == "duplicate": data["choices"][1]["choiceId"] = "first"
                if fault == "overlap": data["choices"][1]["button"]["topPercent"] = 49
                if fault == "syntax": (root / "choice.lua").write_text("return function(", encoding="utf-8")
                if fault == "missing": data["choices"][0]["script"] = "absent.lua"
                if fault == "unknown": data["choices"][0]["unexpected"] = True
                if fault == "legacy": data["choiceRewards"] = []
                if fault == "version": data["eventChoiceSchemaVersion"] = 2
                if fault == "both": data["immediateResolve"] = {"script": "choice.lua", "version": "1.0.0"}
                result = validate(root, card)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("校验失败", result.stderr)
                if fault in ("duplicate", "overlap", "syntax", "missing"):
                    self.assertIn("正式加载器校验失败", result.stderr)
                else:
                    self.assertIn("事件校验失败", result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
