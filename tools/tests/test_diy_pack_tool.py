"""DIY 发布与补装集成测试；只写入显式指定的项目内 TestResults 夹具，不清理现场。"""
import argparse
import binascii
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import time
import unittest
import uuid
import zipfile
import zlib


PROJECT = Path(__file__).resolve().parents[2]
EXAMPLES = PROJECT / 'tools' / 'packaging' / 'examples'


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def png_fixture():
    def chunk(kind, body):
        return struct.pack('>I', len(body)) + kind + body + struct.pack('>I', binascii.crc32(kind + body) & 0xffffffff)
    rows = b'\x00\xff\x00\x00\xff\x00\xff\x00\xff' + b'\x00\x00\x00\xff\xff\xff\xff\xff\xff'
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 2, 2, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b'')


class DiyPackToolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = (Path(SETTINGS.output).resolve() / uuid.uuid4().hex)
        permitted = (PROJECT / 'TestResults').resolve()
        if not cls.root.is_relative_to(permitted) or cls.root == permitted:
            raise ValueError('测试输出必须是项目 TestResults 内的明确子目录')
        cls.root.mkdir(parents=True)
        cls.tool = Path(SETTINGS.tool).resolve(strict=True)
        cls.game = Path(SETTINGS.game).resolve(strict=True)
        cls.sequence = 0
        print('保留测试现场：' + str(cls.root), flush=True)

    def case(self, name):
        type(self).sequence += 1
        path = self.root / f'{self.sequence:02d}-{name}'
        path.mkdir()
        return path

    def invoke(self, action, *args, success=True):
        process = subprocess.run(['dotnet', str(self.tool), action, *map(str, args)], cwd=PROJECT,
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace')
        if success:
            self.assertEqual(process.returncode, 0, process.stdout)
        else:
            self.assertNotEqual(process.returncode, 0, process.stdout)
        return process.stdout

    def publish(self, source, output):
        result = self.invoke('publish', '--game', self.game, '--source', source, '--output', output)
        return json.loads(result)

    def install(self, archive, manifest, data_root, success=True):
        return self.invoke('install', '--game', self.game, '--product', 'NoMadCity', '--zip', archive,
                           '--sha256', sha(archive), '--pack-id', manifest['packId'], '--version', manifest['version'],
                           '--content-hash', manifest['contentHash'], '--fixture-data-root', data_root, '--keep-stage', 'true', success=success)

    def test_reproducible_release_and_export(self):
        case = self.case('发布和导出')
        for name in ('仅模式', '仅扩展', '模式和扩展'):
            first = self.publish(EXAMPLES / name, case / (name + '-一'))
            second = self.publish(EXAMPLES / name, case / (name + '-二'))
            self.assertEqual(first, second)
            first_zip = case / (name + '-一') / first['zipFile']
            second_zip = case / (name + '-二') / second['zipFile']
            self.assertEqual(sha(first_zip), sha(second_zip))
            with zipfile.ZipFile(first_zip) as archive:
                self.assertIn('pack.json', archive.namelist())
                self.assertEqual(archive.namelist(), sorted(archive.namelist()))
            exported = case / (name + '-导出')
            self.invoke('export', '--zip', first_zip, '--sha256', first['zipSha256'], '--output', exported)
            self.assertEqual(sha(exported / first['zipFile']), first['zipSha256'])
            self.invoke('export', '--zip', first_zip, '--sha256', first['zipSha256'], '--output', exported)
            target = exported / first['zipFile']
            target.write_bytes('用户修改的隔离文件'.encode('utf-8'))
            self.assertIn('未覆盖', self.invoke('export', '--zip', first_zip, '--sha256', first['zipSha256'], '--output', exported, success=False))
            self.assertEqual(target.read_bytes(), '用户修改的隔离文件'.encode('utf-8'))

    def test_install_skip_conflict_and_version_coexistence(self):
        case = self.case('补装和版本')
        release = case / '发布'
        first = self.publish(EXAMPLES / '仅模式', release)
        archive = release / first['zipFile']; data = case / '用户数据'
        self.assertIn('已安装', self.install(archive, first, data))
        installed = data / 'Mods' / first['packId'] / first['version']
        self.assertTrue((installed / 'pack.json').is_file())
        self.assertEqual(self.publish(installed, case / '安装内容复核')['contentHash'], first['contentHash'])
        before = (installed / 'pack.json').read_bytes()
        self.assertIn('已跳过', self.install(archive, first, data))
        self.assertEqual((installed / 'pack.json').read_bytes(), before)
        receipts = list((data / 'ModInstallReceipts' / first['packId'] / first['version']).glob('*.json'))
        self.assertEqual(len(receipts), 2)
        self.assertEqual({json.loads(p.read_text(encoding='utf-8'))['outcome'] for p in receipts}, {'installed', 'same_content_skipped'})
        changed = before + b'\n'
        (installed / 'pack.json').write_bytes(changed)
        self.assertIn('未覆盖', self.install(archive, first, data, success=False))
        self.assertEqual((installed / 'pack.json').read_bytes(), changed)
        second_source = case / '第二版源码'
        shutil.copytree(EXAMPLES / '仅模式', second_source)
        manifest_path = second_source / 'pack.json'
        manifest = json.loads(manifest_path.read_text(encoding='utf-8')); manifest['version'] = '2.0.0'
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
        second = self.publish(second_source, case / '第二版发布')
        self.install(case / '第二版发布' / second['zipFile'], second, data)
        self.assertEqual((installed / 'pack.json').read_bytes(), changed)
        self.assertTrue((data / 'Mods' / first['packId'] / '2.0.0' / 'pack.json').exists())
        self.assertFalse((data / 'ModProfiles').exists(), '安装器不得静默修改默认版本')
        reverse = case / '先安装新版的数据'
        self.install(case / '第二版发布' / second['zipFile'], second, reverse)
        profile = reverse / 'ModProfiles' / 'default.json'; profile.parent.mkdir()
        profile.write_text('{"schemaVersion":1,"bindings":{"example.basic":"2.0.0"}}', encoding='utf-8')
        saved_profile = profile.read_bytes()
        self.install(archive, first, reverse)
        self.assertTrue((reverse / 'Mods' / first['packId'] / '1.0.0' / 'pack.json').is_file())
        self.assertEqual(profile.read_bytes(), saved_profile, '旧版补装不得降低本机默认绑定')

    def test_receipt_failure_does_not_publish_new_version(self):
        case = self.case('回执失败事务')
        manifest = self.publish(EXAMPLES / '仅模式', case / '发布')
        data = case / '用户数据'; data.mkdir()
        blocker = data / 'ModInstallReceipts'
        blocker.write_text('夹具：占用回执目录位置', encoding='utf-8')
        original = blocker.read_bytes()
        self.install(case / '发布' / manifest['zipFile'], manifest, data, success=False)
        self.assertEqual(blocker.read_bytes(), original)
        self.assertFalse((data / 'Mods' / manifest['packId'] / manifest['version']).exists(),
                         '回执失败时新版本不能留在正式发现目录')
        self.assertTrue(any((data / 'ModStaging').rglob('pack.json')), '失败的完整包应留在隔离暂存目录')

    def test_post_publish_receipt_failure_rolls_back_only_new_version(self):
        case = self.case('发布后回执故障')
        manifest = self.publish(EXAMPLES / '仅模式', case / '发布')
        archive = case / '发布' / manifest['zipFile']
        def fail_after_publish(data):
            return self.invoke('install', '--game', self.game, '--product', 'NoMadCity', '--zip', archive,
                               '--sha256', sha(archive), '--pack-id', manifest['packId'], '--version', manifest['version'],
                               '--content-hash', manifest['contentHash'], '--fixture-data-root', data, '--keep-stage', 'true',
                               '--fixture-fail-receipt-after-publish', 'true', success=False)
        fresh = case / '新安装'
        self.assertIn('撤回暂存', fail_after_publish(fresh))
        destination = fresh / 'Mods' / manifest['packId'] / manifest['version']
        self.assertFalse(destination.exists())
        self.assertTrue(any((fresh / 'ModStaging').rglob('pack.json')))
        existing = case / '已有版本'
        self.install(archive, manifest, existing)
        existing_file = existing / 'Mods' / manifest['packId'] / manifest['version'] / 'pack.json'
        before = existing_file.read_bytes()
        self.assertIn('已有版本保持不变', fail_after_publish(existing))
        self.assertEqual(existing_file.read_bytes(), before)


    def test_rejects_unsafe_zip_before_installation(self):
        case = self.case('不安全归档')
        manifest = self.publish(EXAMPLES / '仅模式', case / '发布')
        original = (EXAMPLES / '仅模式' / 'pack.json').read_bytes()
        variants = {
            '穿越': [('../outside.txt', b'x')],
            '绝对路径': [('/outside.txt', b'x')],
            '大小写冲突': [('a.txt', b'x'), ('A.txt', b'y')],
            '文件目录冲突': [('folder', b'x'), ('folder/file.txt', b'y')],
        }
        for label, extra in variants.items():
            path = case / (label + '.zip')
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('pack.json', original)
                for name, content in extra:
                    archive.writestr(name, content)
            root = case / (label + '-数据')
            self.install(path, manifest, root, success=False)
            self.assertFalse((root / 'Mods').exists())
        symlink = case / '链接.zip'
        with zipfile.ZipFile(symlink, 'w') as archive:
            archive.writestr('pack.json', original)
            info = zipfile.ZipInfo('link'); info.create_system = 3; info.external_attr = (0o120777 << 16)
            archive.writestr(info, 'elsewhere')
        self.install(symlink, manifest, case / '链接-数据', success=False)
        corrupt = case / 'CRC错误.zip'
        with zipfile.ZipFile(corrupt, 'w', compression=zipfile.ZIP_STORED) as archive:
            archive.writestr('pack.json', original)
        raw = bytearray(corrupt.read_bytes()); offset = raw.find(original); self.assertGreaterEqual(offset, 0)
        raw[offset] ^= 1; corrupt.write_bytes(raw)
        self.install(corrupt, manifest, case / 'CRC-数据', success=False)
        full = case / '先安装的内容'
        self.install(case / '发布' / manifest['zipFile'], manifest, full)
        self.install(corrupt, manifest, full, success=False)
        self.assertTrue((full / 'Mods' / manifest['packId'] / manifest['version'] / 'pack.json').exists())
        too_many = case / '过多文件.zip'
        with zipfile.ZipFile(too_many, 'w') as archive:
            archive.writestr('pack.json', original)
            for i in range(4096):
                archive.writestr(f'files/{i:04}.txt', b'')
        self.install(too_many, manifest, case / '过多文件-数据', success=False)

    def test_concurrent_same_package_installs_are_serialized(self):
        case = self.case('并行安装')
        manifest = self.publish(EXAMPLES / '仅模式', case / '发布')
        archive = case / '发布' / manifest['zipFile']; data = case / '用户数据'
        args = ['dotnet', str(self.tool), 'install', '--game', str(self.game), '--product', 'NoMadCity', '--zip', str(archive),
                '--sha256', sha(archive), '--pack-id', manifest['packId'], '--version', manifest['version'],
                '--content-hash', manifest['contentHash'], '--fixture-data-root', str(data), '--keep-stage', 'true']
        children = [subprocess.Popen(args, cwd=PROJECT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace') for _ in range(2)]
        reports = [child.communicate(timeout=30)[0] for child in children]
        self.assertTrue(all(child.returncode == 0 for child in children), reports)
        self.assertTrue(any('已安装' in report for report in reports), reports)
        self.assertTrue(any('已跳过' in report for report in reports), reports)
        receipts = list((data / 'ModInstallReceipts' / manifest['packId'] / manifest['version']).glob('*.json'))
        self.assertEqual(len(receipts), 2)

    def test_interruption_during_extract_preserves_installed_version(self):
        case = self.case('解压中断')
        first = self.publish(EXAMPLES / '仅模式', case / '第一版发布')
        data = case / '用户数据'
        self.install(case / '第一版发布' / first['zipFile'], first, data)
        existing = data / 'Mods' / first['packId'] / first['version'] / 'pack.json'
        before = existing.read_bytes()
        profile = data / 'ModProfiles' / 'default.json'
        profile.parent.mkdir()
        profile.write_text('{"schemaVersion":1,"bindings":{"example.basic":"1.0.0"}}', encoding='utf-8')
        profile_before = profile.read_bytes()

        source = case / '第二版源码'
        shutil.copytree(EXAMPLES / '仅模式', source)
        manifest_path = source / 'pack.json'
        manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
        manifest['version'] = '2.0.0'
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
        payload_size = 32 * 1024 * 1024
        with (source / 'large-fixture.bin').open('xb') as stream:
            for _ in range(32):
                stream.write(os.urandom(1024 * 1024))
        second = self.publish(source, case / '第二版发布')
        archive = case / '第二版发布' / second['zipFile']
        args = ['dotnet', str(self.tool), 'install', '--game', str(self.game), '--product', 'NoMadCity',
                '--zip', str(archive), '--sha256', second['zipSha256'], '--pack-id', second['packId'],
                '--version', second['version'], '--content-hash', second['contentHash'],
                '--fixture-data-root', str(data), '--keep-stage', 'true']
        child = subprocess.Popen(args, cwd=PROJECT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                 text=True, encoding='utf-8', errors='replace')
        observed = None
        try:
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline and child.poll() is None:
                for blob in (data / 'ModStaging').glob('*/payload/large-fixture.bin'):
                    try:
                        size = blob.stat().st_size
                    except FileNotFoundError:
                        continue
                    if 0 < size < payload_size:
                        observed = {'path': str(blob), 'bytesBeforeInterruption': size}
                        child.kill()
                        break
                if observed:
                    break
                time.sleep(0.003)
        finally:
            if child.poll() is None:
                child.kill()
            output, _ = child.communicate(timeout=10)
        self.assertIsNotNone(observed, '未观察到正在解压的隔离文件：' + output)
        self.assertNotEqual(child.returncode, 0)
        self.assertFalse((data / 'Mods' / second['packId'] / second['version']).exists(), '中断不能发布半包')
        self.assertFalse((data / 'ModInstallReceipts' / second['packId'] / second['version']).exists(), '中断不能产生安装成功回执')
        self.assertEqual(existing.read_bytes(), before, '中断不能改变已有版本')
        self.assertEqual(profile.read_bytes(), profile_before, '中断不能改变默认绑定')
        self.assertTrue(Path(observed['path']).is_file(), '保留中断现场')
        (case / '中断证据.json').write_text(json.dumps(observed, ensure_ascii=False, indent=2), encoding='utf-8')

    def test_zip_hash_mismatch_fails_without_creating_data_root(self):
        case = self.case('归档哈希不符')
        manifest = self.publish(EXAMPLES / '仅模式', case / '发布')
        root = case / '用户数据'
        self.invoke('install', '--game', self.game, '--product', 'NoMadCity', '--zip', case / '发布' / manifest['zipFile'],
                    '--sha256', '0' * 64, '--pack-id', manifest['packId'], '--version', manifest['version'],
                    '--content-hash', manifest['contentHash'], '--fixture-data-root', root, '--keep-stage', 'true', success=False)
        self.assertFalse(root.exists())

    def test_scope_capabilities_reject_unusable_pack_and_report_partial_availability(self):
        case = self.case('玩法能力')
        capability = 'diy.fixture.unavailable.v1'
        for example, collection, id_field, compatible_id in (
            ('仅模式', 'ruleModes', 'modeId', 'example.basic.compatible'),
            ('仅扩展', 'expansions', 'expansionId', 'example.cleanup.compatible'),
        ):
            folder = case / example
            folder.mkdir()
            source = folder / '源码'
            shutil.copytree(EXAMPLES / example, source)
            manifest_path = source / 'pack.json'
            manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
            unavailable = manifest[collection][0]
            unavailable['requiredCapabilities'] = [capability]
            unavailable_id = unavailable[id_field]
            manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')

            # 仅有的玩法不满足目标能力时，发布、校验和补装都要给出具体原因。
            result = self.invoke('publish', '--game', self.game, '--source', source,
                                 '--output', folder / '不可用发布', success=False)
            self.assertIn(capability, result)
            self.assertIn(unavailable_id, result)
            archive = folder / '不可用.zip'
            with zipfile.ZipFile(archive, 'w') as zipped:
                for path in sorted(source.rglob('*')):
                    if path.is_file():
                        zipped.write(path, path.relative_to(source).as_posix())
            args = ('--game', self.game, '--zip', archive, '--sha256', sha(archive), '--pack-id', manifest['packId'],
                    '--version', manifest['version'], '--content-hash', '0' * 64)
            checked = self.invoke('verify', *args, '--output', folder / '不可用校验', success=False)
            self.assertIn(capability, checked)
            isolated = folder / '不可用数据'
            installed = self.invoke('install', *args, '--product', 'NoMadCity', '--fixture-data-root', isolated,
                                    '--keep-stage', 'true', success=False)
            self.assertIn(capability, installed)
            self.assertFalse((isolated / 'Mods' / manifest['packId'] / manifest['version']).exists())

            # 同包仍有其他兼容玩法时可以安装，但结果须指出缺少能力的具体玩法。
            compatible = {'displayName': '兼容玩法', 'description': '隔离夹具', id_field: compatible_id}
            if collection == 'ruleModes':
                compatible['setupRecipeRefs'] = [{'packId': 'core', 'recipeId': 'standard'}]
            manifest[collection].append(compatible)
            manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
            released = self.publish(source, folder / '部分可用发布')
            output = self.install(folder / '部分可用发布' / released['zipFile'], released, folder / '部分可用数据')
            self.assertIn(unavailable_id, output)
            self.assertIn(capability, output)
            self.assertTrue((folder / '部分可用数据' / 'Mods' / released['packId'] / released['version'] / 'pack.json').is_file())

    def test_installer_component_displays_capabilities_from_verified_gameplay(self):
        case = self.case('安装器玩法能力说明')
        source = case / '源码'
        shutil.copytree(EXAMPLES / '仅模式', source)
        manifest_path = source / 'pack.json'
        manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
        manifest['ruleModes'][0]['requiredCapabilities'] = ['diy.pack.v1']
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
        release_dir = case / '发布'
        release = self.publish(source, release_dir)
        spec = importlib.util.spec_from_file_location('diy_installer_test', PROJECT / 'tools' / 'packaging' / 'build_windows_installer.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        tool_exe = self.tool.with_suffix('.exe')
        self.assertTrue(tool_exe.is_file())
        entry = module.read_diy_release(release_dir / (release['packId'] + '-' + release['version'] + '.release.json'),
                                        self.game, tool_exe, case / '安装器核验')
        self.assertEqual(entry['scopeRequirements'], [{'kind': '模式', 'id': manifest['ruleModes'][0]['modeId'],
                                                       'capabilities': ['diy.pack.v1']}])
        _, descriptions, _ = module.make_diy_sections([entry], tool_exe, 'NoMadCity')
        self.assertIn(manifest['ruleModes'][0]['modeId'], descriptions)
        self.assertIn('diy.pack.v1', descriptions)

        # 旧包未显式声明时，安装器仍按实际绑定展示所需能力，不修改发布包。
        legacy = case / '旧包源码'
        shutil.copytree(EXAMPLES / '仅扩展', legacy)
        legacy_path = legacy / 'pack.json'
        legacy_manifest = json.loads(legacy_path.read_text(encoding='utf-8'))
        inferred = 'diy.rule_bindings.cleanup.v1'
        legacy_manifest['requiredCapabilities'].remove(inferred)
        legacy_path.write_text(json.dumps(legacy_manifest, ensure_ascii=False, indent=2), encoding='utf-8')
        legacy_release_dir = case / '旧包发布'
        legacy_release = self.publish(legacy, legacy_release_dir)
        legacy_entry = module.read_diy_release(legacy_release_dir / (legacy_release['packId'] + '-' + legacy_release['version'] + '.release.json'),
                                               self.game, tool_exe, case / '旧包安装器核验')
        self.assertEqual(legacy_entry['scopeRequirements'], [{'kind': '扩展', 'id': legacy_manifest['expansions'][0]['expansionId'],
                                                              'capabilities': [inferred]}])
        _, legacy_descriptions, _ = module.make_diy_sections([legacy_entry], tool_exe, 'NoMadCity')
        self.assertIn(inferred, legacy_descriptions)
        self.assertNotIn(inferred, json.loads(legacy_path.read_text(encoding='utf-8'))['requiredCapabilities'])

        # 准备步骤和模式序号即使未写入包级能力，也应显示正式读取器推导出的玩法能力。
        preparation = case / '多模式准备源码'
        shutil.copytree(EXAMPLES / '准备阶段模式', preparation)
        preparation_path = preparation / 'pack.json'
        preparation_manifest = json.loads(preparation_path.read_text(encoding='utf-8'))
        preparation_manifest['requiredCapabilities'] = ['diy.pack.v1', 'diy.rule_modes.v1']
        preparation_path.write_text(json.dumps(preparation_manifest, ensure_ascii=False, indent=2), encoding='utf-8')
        preparation_release_dir = case / '多模式准备发布'
        preparation_release = self.publish(preparation, preparation_release_dir)
        preparation_entry = module.read_diy_release(preparation_release_dir / (preparation_release['packId'] + '-' + preparation_release['version'] + '.release.json'),
                                                    self.game, tool_exe, case / '多模式准备核验')
        self.assertEqual(preparation_entry['scopeRequirements'], [{'kind': '模式', 'id': preparation_manifest['ruleModes'][0]['modeId'],
                                                                   'capabilities': ['diy.preparation_flow.v1', 'diy.multi_rule_modes.v1']}])

    def test_required_local_expansion_capability_blocks_its_dependent_mode(self):
        case = self.case('依赖扩展能力')
        source = case / '源码'
        shutil.copytree(EXAMPLES / '模式和扩展', source)
        path = source / 'pack.json'
        manifest = json.loads(path.read_text(encoding='utf-8'))
        capability = 'diy.fixture.unavailable.v1'
        manifest['expansions'][0]['requiredCapabilities'] = [capability]
        path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
        output = self.invoke('publish', '--game', self.game, '--source', source, '--output', case / '发布', success=False)
        self.assertIn(capability, output)
        self.assertIn(manifest['expansions'][0]['expansionId'], output)
        self.assertIn(manifest['ruleModes'][0]['modeId'], output)

    def test_preparation_and_mode_order_capabilities_are_inferred(self):
        case = self.case('准备与多模式能力推导')
        source = case / '源码'
        shutil.copytree(EXAMPLES / '准备阶段模式', source)
        manifest_path = source / 'pack.json'
        manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
        manifest['requiredCapabilities'] = ['diy.pack.v1', 'diy.rule_modes.v1']
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
        harness = case / '能力探针'
        harness.mkdir()
        (harness / 'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                                             '<TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>', encoding='utf-8')
        (harness / 'Program.cs').write_text(r'''
using System;
using System.Collections.Generic;
using System.Reflection;
var assembly = Assembly.LoadFrom(args[0]);
var type = assembly.GetType("GameContentBridge", true)!;
var bridge = Activator.CreateInstance(type, new object[] { args[1], "NoMadCity" })!;
var capabilities = (ISet<string>)type.GetProperty("Capabilities")!.GetValue(bridge)!;
var load = type.GetMethod("Load")!;
void Reject(string capability)
{
    capabilities.Remove(capability);
    try { load.Invoke(bridge, new object[] { args[2] }); }
    catch (TargetInvocationException error) when (error.InnerException?.Message.Contains(capability) == true)
    { capabilities.Add(capability); return; }
    throw new Exception("未拒绝缺少的能力：" + capability);
}
Reject("diy.multi_rule_modes.v1");
Reject("diy.preparation_flow.v1");
load.Invoke(bridge, new object[] { args[2] });
Console.WriteLine("能力推导通过");
''', encoding='utf-8')
        build = subprocess.run(['dotnet', 'build', str(harness / 'Probe.csproj'), '--configuration', 'Release', '--nologo'],
                               cwd=PROJECT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace')
        self.assertEqual(build.returncode, 0, build.stdout)
        run = subprocess.run(['dotnet', str(harness / 'bin' / 'Release' / 'net8.0' / 'Probe.dll'), str(self.tool), str(self.game), str(source)],
                             cwd=PROJECT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace')
        self.assertEqual(run.returncode, 0, run.stdout)
        self.assertIn('能力推导通过', run.stdout)

    def test_full_image_decode_and_truncated_image(self):
        case = self.case('图片解码')
        good = case / '完整图片'; bad = case / '截断图片'
        shutil.copytree(EXAMPLES / '仅模式', good); shutil.copytree(EXAMPLES / '仅模式', bad)
        image = png_fixture()
        (good / 'fixture.png').write_bytes(image)
        (bad / 'fixture.png').write_bytes(image[:24])
        self.publish(good, case / '完整发布')
        result = self.invoke('publish', '--game', self.game, '--source', bad, '--output', case / '截断发布', success=False)
        self.assertIn('图片', result)

    @unittest.skipUnless(os.name == 'nt', '该夹具依赖 Windows 禁止删除共享')
    def test_stage_cleanup_preserves_added_directories_and_modified_files(self):
        case = self.case('暂存目录保护')
        probe = PROJECT / 'tools' / 'tests' / 'fixtures' / 'DiyStageCleanupProbe' / 'DiyStageCleanupProbe.csproj'
        output = case / '探针构建'
        intermediate = str(case / '探针中间目录') + os.sep
        build = subprocess.run(
            ['dotnet', 'build', str(probe), '--configuration', 'Release', '--nologo',
             '--output', str(output), '-p:BaseIntermediateOutputPath=' + intermediate,
             '-p:MSBuildProjectExtensionsPath=' + intermediate],
            cwd=PROJECT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding='utf-8', errors='replace')
        self.assertEqual(build.returncode, 0, build.stdout)
        run = subprocess.run(
            ['dotnet', str(output / 'DiyStageCleanupProbe.dll'), str(self.tool), str(self.game),
             str(EXAMPLES), str(case / '保留现场'), str(PROJECT)],
            cwd=PROJECT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding='utf-8', errors='replace')
        self.assertEqual(run.returncode, 0, run.stdout)
        result = json.loads(run.stdout)
        self.assertTrue(result['passed'])
        self.assertEqual(len(result['cases']), 5)
        self.assertTrue(all(item['passed'] and item['preserved'] for item in result['cases']))
        (case / '保护核验.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

    @unittest.skipUnless(os.name == 'nt', '该夹具验证 Windows 目录联接')
    def test_rejects_mods_junction_without_touching_target(self):
        case = self.case('目录联接')
        manifest = self.publish(EXAMPLES / '仅模式', case / '发布')
        data = case / '数据'; external = case / '联接目标'; data.mkdir(); external.mkdir()
        link = data / 'Mods'
        created = subprocess.run(['cmd.exe', '/d', '/c', 'mklink', '/J', str(link), str(external)], capture_output=True, text=True)
        self.assertEqual(created.returncode, 0, created.stdout + created.stderr)
        self.install(case / '发布' / manifest['zipFile'], manifest, data, success=False)
        self.assertEqual(list(external.iterdir()), [])


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--tool', required=True)
    parser.add_argument('--game', required=True)
    parser.add_argument('--output', required=True)
    SETTINGS, remaining = parser.parse_known_args()
    unittest.main(argv=[sys.argv[0], *remaining], verbosity=2)
