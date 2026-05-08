# KakaoDarkHook DLL 빌드 스크립트
# MSVC Build Tools가 설치되어 있어야 함.
# 실행: powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

# vswhere로 VS / Build Tools 위치 찾기
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Error "vswhere.exe not found. Visual Studio Build Tools가 설치되지 않았습니다."
}

$vsPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $vsPath) {
    Write-Error "VC++ Build Tools가 설치되지 않았습니다. winget install Microsoft.VisualStudio.2022.BuildTools 를 실행하세요."
}

$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path $vcvars)) {
    Write-Error "vcvarsall.bat을 찾지 못했습니다: $vcvars"
}

# 카카오톡이 32-bit (x86)이므로 DLL도 32-bit로 빌드해야 인젝션 가능.
# 카카오톡 EXE 비트수 확인
$kakao = "C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe"
$arch = "x86"
if (Test-Path $kakao) {
    $fs = [System.IO.File]::OpenRead($kakao)
    try {
        $buf = New-Object byte[] 4096
        $null = $fs.Read($buf, 0, 4096)
        $peOffset = [BitConverter]::ToInt32($buf, 0x3C)
        $machine = [BitConverter]::ToUInt16($buf, $peOffset + 4)
        if ($machine -eq 0x8664) { $arch = "x64" }
        elseif ($machine -eq 0x14C) { $arch = "x86" }
        Write-Host "KakaoTalk arch: $arch (machine=0x$('{0:X}' -f $machine))"
    } finally { $fs.Close() }
}

# vcvarsall.bat이 내부적으로 vswhere를 호출하므로 PATH에 추가해줘야 함.
$vswhereDir = Split-Path -Parent $vswhere
$env:PATH = "$vswhereDir;" + $env:PATH

# vcvarsall.bat을 cmd로 실행해서 환경 변수 추출
$envCmd = "`"$vcvars`" $arch && set"
$envOutput = cmd /c $envCmd 2>&1
foreach ($line in $envOutput) {
    if ($line -match '^([^=]+)=(.*)$') {
        Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] -ErrorAction SilentlyContinue
    }
}

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    Write-Error "vcvarsall 실행 후에도 cl.exe를 찾을 수 없습니다."
}

# 빌드
$out = Join-Path $ScriptDir "KakaoDarkHook.dll"
$outDir = Join-Path $ScriptDir "build"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

Write-Host "Compiling KakaoDarkHook.dll for $arch ..."

$cppFlags = @(
    '/nologo'
    '/std:c++17'
    '/O2'
    '/MT'                  # 정적 CRT — 카카오톡 환경에 의존성 안 만들기
    '/W3'
    '/EHsc'
    '/DWIN32'
    '/D_WINDOWS'
    '/D_USRDLL'
    '/DUNICODE'
    '/D_UNICODE'
    "/Fo$outDir\"
    "/Fd$outDir\KakaoDarkHook.pdb"
    'KakaoDarkHook.cpp'
)
$linkFlags = @(
    '/link'
    '/DLL'
    '/MACHINE:' + ($arch.ToUpper())
    "/OUT:$out"
    "/PDB:$outDir\KakaoDarkHook.pdb"
    'kernel32.lib'
    'user32.lib'
    'gdi32.lib'
    'psapi.lib'
)

& cl.exe $cppFlags $linkFlags
if ($LASTEXITCODE -ne 0) {
    Write-Error "빌드 실패 (exit $LASTEXITCODE)"
}

Write-Host ""
Write-Host "Built: $out"
Get-Item $out | Select-Object Name, Length, LastWriteTime
