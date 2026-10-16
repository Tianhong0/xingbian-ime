$ErrorActionPreference = 'Stop'
$dll = "D:\Documents\deepseek-harness\default-workspace\xingbian-ime\src\Xingbian.Tsf\bin\Release\Xingbian.Tsf.dll"
Write-Host "=== file ==="
Get-Item $dll | Select-Object FullName, Length, LastWriteTime | Format-List

$an = [System.Reflection.AssemblyName]::GetAssemblyName($dll)
Write-Host "=== identity ==="
Write-Host "FullName          : $($an.FullName)"
Write-Host "Version           : $($an.Version)"
Write-Host "Culture           : [$($an.CultureName)]"
$pk = $an.GetPublicKeyToken()
if ($null -eq $pk -or $pk.Length -eq 0) {
    Write-Host "PublicKeyToken    : <none>  => NOT strong-named"
} else {
    Write-Host "PublicKeyToken    : $([BitConverter]::ToString($pk).Replace('-','').ToLower())"
}
Write-Host "ProcessorArch     : $($an.ProcessorArchitecture)"

$asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($dll)
Write-Host "ImageRuntimeVer   : $($asm.ImageRuntimeVersion)"

Write-Host "=== ComVisible / Guid types ==="
foreach ($t in $asm.GetTypes()) {
    $cads = $t.GetCustomAttributesData()
    $cv = $cads | Where-Object { $_.AttributeType.Name -eq 'ComVisibleAttribute' }
    $gd = $cads | Where-Object { $_.AttributeType.Name -eq 'GuidAttribute' }
    $ci = $cads | Where-Object { $_.AttributeType.Name -eq 'ClassInterfaceAttribute' }
    if ($gd) {
        $g = $gd[0].ConstructorArguments[0].Value
        $c = if ($cv) { $cv[0].ConstructorArguments[0].Value } else { '(absent)' }
        $u = if ($ci) { $ci[0].ConstructorArguments[0].Value } else { '(absent)' }
        Write-Host ("  {0,-50} Guid={1} ComVisible={2} ClassIface={3} Public={4}" -f $t.FullName, $g, $c, $u, $t.IsPublic)
    }
}

Write-Host "=== exported/hosted check: TypeLib / ComImport ==="
Write-Host "=== deps (same dir) ==="
Get-ChildItem (Split-Path $dll) -Filter *.dll | Select-Object Name, Length | Format-Table -AutoSize
