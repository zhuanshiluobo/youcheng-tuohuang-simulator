"""把正式企业目录逐项绑定到实际 NUnit XML；缺失或未通过的能力使收集失败。"""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def collect(catalog_path, xml_paths):
    cases = {}
    for xml_path in xml_paths:
        for case in ET.parse(xml_path).iter("test-case"):
            name = case.get("fullname")
            cases[name] = {"case": name, "result": case.get("result"), "xml": str(xml_path.resolve())}

    explicit = {
        "enterprise.event.collection_platform": "CollectionPlatformLosesScoreBelowZeroAndActivatesSavedSpecialOnSameEnterprise",
        "enterprise.event.cave_ruins": "CaveSponsorOnlyUpgradesAndDoesNotTriggerUnlockedSpecial",
        "city_style_city_construction_frontier": "FrontierLosesScoreAndShufflesDeckAfterDeclinedBuild",
        "city_style_pioneer_outpost": "PioneerPlacesOnlyAtCurrentCityAndUsesAllOwnMarkersOnThisStyle",
        "city_style_core_commercial_area": "CommercialResourceChoicePrecedesRestrictedImmediateActionAndConsumesOnlyOuterBudget",
        "city_style_large_enterprise_park": "LargeEnterpriseParkChoosesOneEnterpriseAndResumesTwoArrivals",
        "city_style_security_district": "SecurityAllowsSameBranchTwiceAndLocksCharactersForWholeActionRound",
        "city_style_pioneer_command_hub": "CommandHubExecutesBothModesAndConsumesCoveredCharacterOnlyOnce",
        "enterprise.component.road": "MountainClearsRealRouteInExecutorOrderScoresDistinctOwnersAndCreatesMappedRoad",
    }
    records, missing = [], []
    for definition in json.loads(catalog_path.read_text(encoding="utf-8-sig")):
        definition_id = definition["DefinitionId"]
        abilities = definition["abilities"] or [{"AbilityId": None, "Script": None, "Version": None}]
        evidence = []
        for ability in abilities:
            script = ability["Script"]
            if definition_id.startswith("facility_enterprise_office_"):
                matches = [value for name, value in cases.items()
                           if "EveryOfficeBuildRunsItsRegisteredEntryAndRecoversSpecialExactlyOnce" in name and definition_id in name]
            elif definition_id in explicit:
                matches = [value for name, value in cases.items() if explicit[definition_id] in name]
            else:
                matches = [value for name, value in cases.items()
                           if "EnterpriseBoardAndCharacterContentTests.DeliveredAbilityRunsThroughLuaHostAndRealEffects" in name and script in name]
            passed = bool(matches) and all(case["result"] == "Passed" for case in matches)
            evidence.append({**ability, "passed": passed, "execution": matches})
            if not passed:
                missing.append({"definitionId": definition_id, "abilityId": ability["AbilityId"], "execution": matches})
        records.append({**definition, "executionEvidence": evidence})
    return {"success": len(records) == 33 and not missing, "definitionCount": len(records),
            "abilityCount": sum(len(d["abilities"]) for d in records),
            "catalog": str(catalog_path.resolve()), "xml": [str(p.resolve()) for p in xml_paths],
            "missingOrFailed": missing, "definitions": records}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path, help="生产目录输出 JSON")
    parser.add_argument("--xml", required=True, action="append", type=Path, help="实际测试 XML，可按执行先后重复传入")
    parser.add_argument("--output", required=True, type=Path, help="证据 JSON 输出路径")
    args = parser.parse_args()
    result = collect(args.catalog, args.xml)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("success", "definitionCount", "abilityCount", "missingOrFailed")}, ensure_ascii=False))
    raise SystemExit(0 if result["success"] else 1)
