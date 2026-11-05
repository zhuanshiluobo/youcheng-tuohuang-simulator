param([Parameter(Mandatory=$true)][string]$AssemblyPath,[string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Reflection.Metadata
$resolved=[System.IO.Path]::GetFullPath($AssemblyPath)
$stream=[System.IO.File]::OpenRead($resolved)
try {
    $pe=[System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata=[System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $assembly=$metadata.GetAssemblyDefinition()
        $flags=$null
        $blobHex=$null
        foreach($handle in $assembly.GetCustomAttributes()) {
            $attribute=$metadata.GetCustomAttribute($handle)
            if($attribute.Constructor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) {continue}
            $constructor=$metadata.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if($constructor.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) {continue}
            $type=$metadata.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
            if($metadata.GetString($type.Namespace) -ne 'System.Diagnostics' -or $metadata.GetString($type.Name) -ne 'DebuggableAttribute') {continue}
            $bytes=$metadata.GetBlobBytes($attribute.Value)
            $blobHex=[System.BitConverter]::ToString($bytes)
            if($bytes.Length -eq 8 -and $bytes[0] -eq 1 -and $bytes[1] -eq 0) {$flags=[System.BitConverter]::ToInt32($bytes,2)}
        }
        $module=$metadata.GetModuleDefinition()
        $result=[ordered]@{
            Assembly=$resolved;Name=$metadata.GetString($assembly.Name);Version=$assembly.Version.ToString()
            Mvid=$metadata.GetGuid($module.Mvid).ToString();Sha256=(Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
            DebuggableAttributeBlob=$blobHex;DebugFlags=$flags
            JitOptimizationDisabled=if($null -eq $flags){$null}else{($flags -band 256) -ne 0}
            Note='只读程序集元数据，不加载或运行游戏代码；未把编译标记当作实测性能收益。'
        }
    } finally {$pe.Dispose()}
} finally {$stream.Dispose()}
$json=$result|ConvertTo-Json -Depth 4
if($OutputPath) {
    $output=[System.IO.Path]::GetFullPath($OutputPath)
    if(Test-Path -LiteralPath $output) {throw '不能覆盖已有编译证据。'}
    [System.IO.File]::WriteAllText($output,$json,[System.Text.UTF8Encoding]::new($false))
}
$json
