"""只编译与运行有限策略夹具；不调用 Unity/Player、不复制工程、不修改生产产物。"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
NS = {"m": "http://schemas.microsoft.com/developer/msbuild/2003"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    out = Path(args.output).resolve()
    if not out.is_relative_to(ROOT) or out == ROOT:
        raise ValueError("输出必须是项目内专用子目录")
    out.mkdir(parents=True, exist_ok=True)
    dotnet = "dotnet"
    sdk_lines = subprocess.check_output([dotnet, "--list-sdks"], text=True).strip().splitlines()
    sdk = sdk_lines[-1]
    csc = Path(sdk.split("[", 1)[1].rstrip("]")) / sdk.split()[0] / "Roslyn/bincore/csc.dll"
    refs_root = csc.parents[4] / "packs/Microsoft.NETCore.App.Ref"
    frameworks = sorted(refs_root.glob("*/ref/net9.0"))
    if not frameworks:
        raise RuntimeError("缺少独立策略夹具的 net9.0 引用；不安装或调用 Unity")
    records = []

    def compile_cs(name, sources, refs, defines, executable=False):
        destination = out / (name + ".dll")
        rsp = out / (name + ".rsp")
        lines = ["/nologo", "/nostdlib+", "/langversion:9", "/target:" + ("exe" if executable else "library"),
                 '/out:"' + str(destination) + '"', "/define:" + defines]
        lines += ['/reference:"' + str(path) + '"' for path in dict.fromkeys(refs)]
        lines += ['"' + str(path) + '"' for path in dict.fromkeys(sources)]
        rsp.write_text("\n".join(lines), encoding="utf-8")
        command = [dotnet, str(csc), "/noconfig", "@" + str(rsp)]
        before_hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources + refs}
        result = subprocess.run(command, cwd=ROOT, text=True, encoding="utf-8", errors="replace", capture_output=True)
        (out / (name + ".log")).write_text(result.stdout + result.stderr, encoding="utf-8")
        after_hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources + refs}
        drift = [path for path in before_hashes if before_hashes[path] != after_hashes[path]]
        records.append({"name": name, "exitCode": result.returncode, "inputsStable": not drift, "changedInputs": drift,
                        "sourceCount": len(sources), "referenceCount": len(refs),
                        "sourceHashes": [{"path": str(p), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in sources],
                        "referenceHashes": [{"path": str(p), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in refs]})
        print(name + " 编译退出码：" + str(result.returncode))
        if result.returncode:
            print("\n".join(line for line in (result.stdout + result.stderr).splitlines() if ": error " in line))
        if drift:
            print("编译期间输入变化，不能把此轮当成固定输入通过：" + repr(drift))
        return destination, result.returncode or (3 if drift else 0)

    policy = ROOT / "Assets/YC/Presentation/Validation/ActionCleanupCoveragePolicy.cs"
    fixture, policy_code = compile_cs("ActionCleanupPolicySmoke", [policy, ROOT / "tools/tests/ActionCleanupPolicySmoke.cs"],
                                      list(frameworks[-1].glob("*.dll")), "DEVELOPMENT_BUILD", True)
    run_code = -1
    if policy_code == 0:
        runtime = next(line.split()[1] for line in subprocess.check_output([dotnet, "--list-runtimes"], text=True).splitlines()
                       if line.startswith("Microsoft.NETCore.App 9."))
        fixture.with_suffix(".runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
            "tfm": "net9.0", "framework": {"name": "Microsoft.NETCore.App", "version": runtime}}}), encoding="utf-8")
        result = subprocess.run([dotnet, str(fixture)], text=True, encoding="utf-8", errors="replace", capture_output=True)
        run_code = result.returncode
        (out / "策略夹具运行.log").write_text(result.stdout + result.stderr, encoding="utf-8")
        print(result.stdout + result.stderr)

    project = ET.parse(ROOT / "Assembly-CSharp.csproj").getroot()
    sources = [ROOT / element.attrib["Include"] for element in project.findall(".//m:Compile", NS)]
    sources += [path for path in (ROOT / "Assets/YC/Presentation/Validation").glob("*.cs") if path not in sources]
    sources = list(dict.fromkeys(p.resolve() for p in sources))
    forbidden = ROOT / "游城拓荒/UI素材"
    if any(p.is_relative_to(forbidden) for p in sources):
        raise ValueError("项目编译清单越过受限档案边界")
    refs = [Path(element.text) for element in project.findall(".//m:Reference/m:HintPath", NS)]
    refs += [ROOT / "Library/ScriptAssemblies" / (element.find("m:Name", NS).text + ".dll")
             for element in project.findall(".//m:ProjectReference", NS)]
    missing = [str(p) for p in sources + refs if not p.exists()]
    if missing:
        raise FileNotFoundError("编译清单输入缺失：" + repr(missing))
    defines = project.find(".//m:DefineConstants", NS).text + ";DEVELOPMENT_BUILD"
    _, integration_code = compile_cs("Assembly-CSharp", sources, refs, defines)
    report = {"normalGameAcceptanceCount": 0, "recoveryVerificationCount": 0, "unityStarted": False, "playerStarted": False,
              "policyCompileExitCode": policy_code, "policyRunExitCode": run_code, "driverCompileExitCode": integration_code, "compilations": records,
              "limitations": ["运行输入为模拟，只证明策略逻辑", "集成只做CLR编译，不证明渲染、指针路由或Lua结算", "没有启动正常局或改写存档"]}
    (out / "有限验证结果.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return policy_code or run_code or integration_code


if __name__ == "__main__":
    raise SystemExit(main())
