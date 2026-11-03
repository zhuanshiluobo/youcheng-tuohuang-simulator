"""API-01采集器回归：防止漏掉行内属性、模板绑定和审阅漂移。"""
import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "validation"))
import lua_api_upgrade_inventory as audit


class InventoryTests(unittest.TestCase):
    def test_inline_attributes_and_overloads_keep_testcase_with_its_method(self):
        source = '''public class C {
 public void Before() { var text = "} not a brace"; }
 [TestCase("Effect.Repeat({effects={}})")]
 [Test] public void Rejected(int count) { /* } */ Run(count); }
 public void Rejected(string text) { Run(text); }
}'''
        found = audit.scopes(source)
        self.assertEqual([s["name"] for s in found], ["Before", "Rejected", "Rejected"])
        self.assertNotIn("TestCase", found[0]["text"])
        self.assertIn("Effect.Repeat", found[1]["text"])
        self.assertTrue(found[1]["text"].rstrip().endswith("}"))

    def test_constant_alias_and_prefix_resolve_without_test_fixture_collision(self):
        values, _ = audit.constants({
            "Assets/YC/A.cs": 'public class A { public const string Prefix="effect."; public const string TypeId=Prefix+"old"; }',
            "Assets/YC/B.cs": 'public class B { public const string Alias=A.TypeId; }',
            "Assets/YC/Tests/F.cs": 'public class A { public const string TypeId="test.collision"; }',
        })
        self.assertEqual(values["B.Alias"], "effect.old")
        self.assertEqual(audit.resolve_symbol("YC.Domain.Effects.A.TypeId", "B", values), "effect.old")

    def test_template_inventory_module_and_presentation_are_all_binding_sources(self):
        prefix = "Assets/StreamingAssets/Content/core/"
        manifest = {"definitions": ["disabled.json"], "facilityInventory": "inventory.json",
                    "presentation": {"defaults": "defaults.json"},
                    "luaModules": [{"moduleId": "shared", "script": "shared.lua", "version": "1", "sha256": "hash"}],
                    "scriptDependencies": [{"script": "new.lua", "modules": [{"moduleId": "shared"}]}]}
        documents = {
            "pack.json": manifest,
            "disabled.json": {"definitionId": "disabled", "enabled": False, "script": "disabled.lua"},
            "inventory.json": {"definitionFiles": ["direct.json"], "groups": [{"definition": {"dataTemplate": "template.json", "data": {"nested": {"script": "new.lua"}}}, "variants": [{"color": "r", "count": 1, "instances": [{"id": "facility.1"}]}]}]},
            "direct.json": {"definitionId": "facility.direct", "script": "direct.lua"},
            "template.json": {"data": {"nested": {"script": "old.lua", "callback": "callback.lua"}, "values": [1, 2]}},
            "defaults.json": {"repeat": {"presentationScript": "presentation.lua"}},
        }
        bindings, _ = audit.content_bindings({prefix + p: json.dumps(d) for p, d in documents.items()})
        self.assertEqual(bindings["new.lua"][0]["definition"], "facility.1")
        self.assertEqual(bindings["callback.lua"][0]["definition"], "facility.1")
        self.assertNotIn("old.lua", bindings)
        self.assertFalse(bindings["disabled.lua"][0]["enabled"])
        self.assertEqual(bindings["shared.lua"][0]["via"], "new.lua")
        self.assertTrue(bindings["direct.lua"])
        self.assertTrue(bindings["presentation.lua"])
        self.assertEqual(audit.merge_data({"a": [1, 2]}, {"a": [3]}), {"a": [3]})

    def test_constructor_result_is_not_a_function_alias(self):
        calls, aliases = audit.lua_calls("local value=Effect.MoveItem({apiVersion=3}); local old=Effect.ReleaseFacilityBinding\nreturn old({})", [])
        self.assertEqual(aliases, {"old": "ReleaseFacilityBinding"})
        self.assertEqual([c["name"] for c in calls], ["MoveItem", "ReleaseFacilityBinding"])

    def test_review_refuses_new_changed_missing_and_invalid_classifications(self):
        data = {"inputs": [{"path": "a", "sha256": "1"}], "catalog": [], "compilerTypes": [],
                "luaAliases": [], "dynamicLuaReferences": [], "contentCalls": []}
        group = {"key": "Repeat|a|Method", "scopeSha256": "source"}
        decision = {**group, "categories": ["失效正向夹具"], "reason": "仍正向构造旧节点"}
        fingerprint = audit.digest(json.dumps(data["inputs"], ensure_ascii=False, sort_keys=True))
        review = {"decisions": [decision], "inputFingerprint": fingerprint, "targets": []}
        self.assertFalse(audit.verify_review(data, [group], review)[0])
        for groups in [[{**group, "scopeSha256": "changed"}], [], [group, {"key": "new", "scopeSha256": "x"}]]:
            self.assertTrue(audit.verify_review(data, groups, review)[0])
        invalid = copy.deepcopy(review)
        invalid["decisions"][0]["categories"] = ["生产文件"]
        self.assertTrue(audit.verify_review(data, [group], invalid)[0])
        invalid = copy.deepcopy(review)
        invalid["decisions"][0]["excluded"] = True
        self.assertTrue(audit.verify_review(data, [group], invalid)[0])
        changed = copy.deepcopy(data)
        changed["inputs"][0]["sha256"] = "2"
        self.assertTrue(audit.verify_review(changed, [group], review)[0])

    def test_retired_type_reintroduced_into_compiler_is_not_accepted_as_complete(self):
        data = {"inputs": [], "catalog": [], "compilerTypes": [{"typeId": "effect.card.load_markers", "registration": ["old"]}],
                "luaAliases": [], "dynamicLuaReferences": [], "contentCalls": []}
        review = {"decisions": [], "inputFingerprint": audit.digest("[]"),
                  "targets": [{"id": "LoadCardMarkers", "typeIds": ["effect.card.load_markers"], "names": ["LoadCardMarkers"], "expectedRetired": True}]}
        self.assertTrue(any("退役入口回流" in e for e in audit.verify_review(data, [], review)[0]))


if __name__ == "__main__":
    unittest.main()
