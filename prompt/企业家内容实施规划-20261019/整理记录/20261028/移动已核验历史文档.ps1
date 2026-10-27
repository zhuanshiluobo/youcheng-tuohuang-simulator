$ErrorActionPreference='Stop'
$project='G:\NoMadCity';$base='G:\NoMadCity\prompt\企业家内容实施规划-20261019';$history=Join-Path $base '历史\20261028整理前\文档'
$rows=Get-Content -LiteralPath (Join-Path $PSScriptRoot '移动前绝对路径清单.json') -Raw|ConvertFrom-Json
$log=Join-Path $PSScriptRoot '移动执行结果.json'
if(Test-Path -LiteralPath $log){throw '已有执行记录，拒绝重复移动'}
foreach($r in $rows){
 foreach($p in @($r.source,$r.destination)){
  if([IO.Path]::GetFullPath($p) -ne $p -or -not $p.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase) -or $p.IndexOfAny([char[]]'*?[]$') -ge 0){throw "非明确项目内绝对路径: $p"}
  $q=$p;while($q -ne $project){if(Test-Path -LiteralPath $q){if((Get-Item -LiteralPath $q -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "重解析路径: $q"}};$q=[IO.Path]::GetDirectoryName($q)}
 }
 if([IO.Path]::GetDirectoryName($r.source) -ne $base -or [IO.Path]::GetDirectoryName($r.destination) -ne $history){throw '不是已列明的顶层文档归档'}
 if(Test-Path -LiteralPath $r.destination){throw '目标已存在，拒绝覆盖'}
 if((Get-Item -LiteralPath $r.source).Length -ne $r.bytes -or (Get-FileHash -LiteralPath $r.source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $r.sha256){throw "封存后有其他写入，停止: $($r.source)"}
}
$done=[Collections.Generic.List[object]]::new()
foreach($r in $rows){
 Move-Item -LiteralPath $r.source -Destination $r.destination -ErrorAction Stop
 if((Get-FileHash -LiteralPath $r.destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $r.sha256){throw '移动后字节不符'}
 $done.Add(@{source=$r.source;destination=$r.destination;sha256=$r.sha256;verified=$true})
 ConvertTo-Json -InputObject @($done) -Depth 5|Set-Content -LiteralPath $log -Encoding utf8
}
@{moved=$done.Count;allOriginalHashesVerified=$true;root=$history}|ConvertTo-Json
