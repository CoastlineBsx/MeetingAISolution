# MeetingAI.Worker 去掉 UTF-8 BOM 的脚本
$root = "C:\VisualStudioSource\MeetingAI.Worker"   # 改成你的实际路径
$includeExt = @('*.cpp','*.c','*.h','*.hpp','*.cc','*.inl','*.txt','*.json','*.cmake','*.proto')
$targets = Get-ChildItem -Path $root -Recurse -File -Include $includeExt | Where-Object {
    $p = $_.FullName.ToLower()
    ($p -notmatch '\\models\\') -and
    ($p -notmatch '\\lib\\')
}

foreach ($f in $targets) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        # 可选：先备份
        [System.IO.File]::WriteAllBytes($f.FullName + ".bak", $bytes)
        # 去掉 BOM（不改正文）
        [System.IO.File]::WriteAllBytes($f.FullName, $bytes[3..($bytes.Length-1)])
        Write-Host "Removed BOM:" $f.FullName
    }
}
Write-Host "完成：所有 .cpp/.h 等文件已去掉 BOM（models/ 和 lib/ 未改）" -ForegroundColor Green
