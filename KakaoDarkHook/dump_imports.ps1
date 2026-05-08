# 간이 PE imports dumper — KakaoTalk.exe와 Vox*.dll의 IAT 항목을 출력해
# 어떤 그리기 함수가 import되어 있는지 확인.

param(
    [string[]]$Files = @(
        "C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe",
        "C:\Program Files (x86)\Kakao\KakaoTalk\Vox.dll",
        "C:\Program Files (x86)\Kakao\KakaoTalk\Vox3.dll"
    ),
    [string]$Filter = "(text|fill|rect|draw|color|brush|gdip|d2d|polygon|bitblt|alphablend)"
)

function Get-Imports([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $is64 = $machine -eq 0x8664
    $optHeaderOffset = $peOffset + 24
    $sectionsOffset = $optHeaderOffset + [BitConverter]::ToUInt16($bytes, $peOffset + 20)
    $numSections = [BitConverter]::ToUInt16($bytes, $peOffset + 6)

    # ImageBase + Import directory RVA
    if ($is64) {
        $imageBase = [BitConverter]::ToUInt64($bytes, $optHeaderOffset + 24)
        $importRVA = [BitConverter]::ToUInt32($bytes, $optHeaderOffset + 112)
    } else {
        $imageBase = [BitConverter]::ToUInt32($bytes, $optHeaderOffset + 28)
        $importRVA = [BitConverter]::ToUInt32($bytes, $optHeaderOffset + 104)
    }

    # 섹션 헤더로 RVA → file offset 변환 함수
    $sections = @()
    for ($i = 0; $i -lt $numSections; $i++) {
        $sh = $sectionsOffset + $i * 40
        $sections += [PSCustomObject]@{
            VA = [BitConverter]::ToUInt32($bytes, $sh + 12)
            VS = [BitConverter]::ToUInt32($bytes, $sh + 8)
            FO = [BitConverter]::ToUInt32($bytes, $sh + 20)
            FS = [BitConverter]::ToUInt32($bytes, $sh + 16)
        }
    }
    function RvaToFile($rva) {
        foreach ($s in $sections) {
            if ($rva -ge $s.VA -and $rva -lt ($s.VA + [Math]::Max($s.VS, $s.FS))) {
                return $s.FO + ($rva - $s.VA)
            }
        }
        return -1
    }

    function ReadCStr($offset) {
        $sb = New-Object System.Text.StringBuilder
        while ($offset -lt $bytes.Length -and $bytes[$offset] -ne 0) {
            $sb.Append([char]$bytes[$offset]) | Out-Null
            $offset++
            if ($sb.Length -gt 256) { break }
        }
        return $sb.ToString()
    }

    $importFO = RvaToFile $importRVA
    if ($importFO -lt 0) { return $null }

    $result = @{}
    while ($true) {
        $nameRVA = [BitConverter]::ToUInt32($bytes, $importFO + 12)
        if ($nameRVA -eq 0) { break }
        $firstThunkRVA = [BitConverter]::ToUInt32($bytes, $importFO + 16)
        $origThunkRVA  = [BitConverter]::ToUInt32($bytes, $importFO + 0)
        if ($origThunkRVA -eq 0) { $origThunkRVA = $firstThunkRVA }

        $dllName = ReadCStr (RvaToFile $nameRVA)
        $thunkFO = RvaToFile $origThunkRVA
        if ($thunkFO -lt 0) { $importFO += 20; continue }

        $funcs = @()
        $thunkSize = if ($is64) { 8 } else { 4 }
        while ($true) {
            $thunk = if ($is64) { [BitConverter]::ToUInt64($bytes, $thunkFO) } else { [BitConverter]::ToUInt32($bytes, $thunkFO) }
            if ($thunk -eq 0) { break }
            $isOrdinal = if ($is64) { ($thunk -band 0x8000000000000000) -ne 0 } else { ($thunk -band 0x80000000) -ne 0 }
            if (-not $isOrdinal) {
                $hintNameFO = RvaToFile $thunk
                if ($hintNameFO -ge 0) {
                    $funcs += ReadCStr ($hintNameFO + 2)
                }
            }
            $thunkFO += $thunkSize
        }
        $result[$dllName] = $funcs
        $importFO += 20
    }
    return $result
}

foreach ($file in $Files) {
    if (-not (Test-Path $file)) { continue }
    Write-Host "=== $file ===" -ForegroundColor Cyan
    $imp = Get-Imports $file
    if (-not $imp) { Write-Host "  (no imports)"; continue }
    foreach ($dll in $imp.Keys | Sort-Object) {
        $matches = $imp[$dll] | Where-Object { $_ -match $Filter }
        if ($matches) {
            Write-Host "  [$dll]" -ForegroundColor Yellow
            $matches | ForEach-Object { Write-Host "    $_" }
        }
    }
    Write-Host ""
}
