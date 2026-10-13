"""沿用验收版本导出配置，只切换到补齐左侧撑板的源文件。"""
from pathlib import Path
base=Path('F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter')
source=(base/'ReferenceViewMatch/export_approved.py').read_text(encoding='utf-8')
source=source.replace("OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter/ReferenceViewMatch')", "OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter/LeftSupportRevision')")
source=source.replace('NucleicCrystalSinter_SameView.blend','NucleicCrystalSinter_LeftSupport.blend')
source=source.replace('ReferenceViewMatch/NucleicCrystalSinter_LeftSupport.blend','LeftSupportRevision/NucleicCrystalSinter_LeftSupport.blend')
source=source.replace('用户验收通过的同视角版','已验收同视角版补齐左侧斜撑板')
exec(compile(source,str(base/'ReferenceViewMatch/export_approved.py'),'exec'))
