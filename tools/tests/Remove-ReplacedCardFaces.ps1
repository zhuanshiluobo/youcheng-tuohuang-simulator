param([switch]$Delete)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
$recordsPath = Join-Path $projectRoot 'prompt/卡牌Sprite切片替换/执行记录'
$records = Get-Content -LiteralPath (Join-Path $recordsPath '旧图删除清单.json') -Raw | ConvertFrom-Json
$scan = Get-Content -LiteralPath (Join-Path $recordsPath '删除前引用扫描.json') -Raw | ConvertFrom-Json
if (@($scan.liveAssetReferences).Count -ne 0) { throw '仍有未迁移的运行资产引用。' }
$targets = [Collections.Generic.List[string]]::new()
foreach ($record in $records) {
    foreach ($item in @(@{Path=$record.path; Hash=$record.sha256}, @{Path=$record.metaPath; Hash=$record.metaSha256})) {
        if (-not $item.Path) { continue }
        $resolved = [IO.Path]::GetFullPath($item.Path)
        if ($resolved -ne $item.Path -or -not $resolved.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $resolved.IndexOfAny([char[]]'*?') -ge 0 -or $resolved.Contains('$')) { throw "目标路径不合法：$resolved" }
        $file = Get-Item -LiteralPath $resolved
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "不允许目录或链接：$resolved" }
        if ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash -ne $item.Hash) { throw "清单后文件已变更：$resolved" }
        $targets.Add($resolved)
    }
}
if ($targets.Count -ne ($targets | Sort-Object -Unique).Count) { throw '目标重复。' }
$targets | Set-Content -LiteralPath (Join-Path $recordsPath '删除目标绝对路径.txt') -Encoding utf8
Write-Output "已逐项核验 $($targets.Count) 个精确文件，完整绝对路径见：$recordsPath\删除目标绝对路径.txt"
if ($Delete) {
    foreach ($target in $targets) { Remove-Item -LiteralPath $target }
    foreach ($target in $targets) { if (Test-Path -LiteralPath $target) { throw "删除后仍存在：$target" } }
    git -C $projectRoot status --short
}
