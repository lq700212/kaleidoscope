#Requires -Version 5.1
<#
.SYNOPSIS
  Kaleidoscope 测试用例集一键运行：编译（Roslyn）→ 拷依赖 → 运行 152 用例 → 透传结果。
  （acceptance-test 验收测试技能沉淀，用法见本目录 SKILL.md 与 AGENTS.md「测试」节）

.EXAMPLE
  .\run-smoke.ps1                 # 编译+运行，输出全部用例结果
#>
param(
    [string]$CscPath = ""   # Roslyn csc 路径（留空自动探测）
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
$lib = Join-Path $root "Kaleidoscope/bin/Debug"
$src = Join-Path $PSScriptRoot "SmokeTest.cs"

if (-not (Test-Path $src)) { throw "找不到用例源码：$src" }
foreach ($d in @("Kaleidoscope.dll", "HslCommunication.dll", "NModbus.dll", "NModbus.Serial.dll")) {
    if (-not (Test-Path (Join-Path $lib $d))) {
        throw ("库未构建：" + (Join-Path $lib $d) + " 不存在（先 MSBuild Kaleidoscope.csproj）")
    }
}

# ── 探测 Roslyn csc（系统自带 csc 是 C# 5，不支持 out var/插值，必须用 VS 的）──
if ($CscPath -eq "") {
    $candidates = @(
        "D:/Program Files/Microsoft Visual Studio/18/Enterprise/MSBuild/Current/Bin/Roslyn/csc.exe",
        "C:/Program Files/Microsoft Visual Studio/2022/Enterprise/MSBuild/Current/Bin/Roslyn/csc.exe",
        "C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/Roslyn/csc.exe",
        "C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/csc.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $CscPath = $c; break } }
    if ($CscPath -eq "") { throw "找不到 Roslyn csc（用 -CscPath 指定 VS 的 MSBuild/Current/Bin/Roslyn/csc.exe）" }
}

# ── 编译（输出到 temp，不污染仓库）──
$outDir = Join-Path $env:TEMP "kaleidoscope_smoke"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$out = Join-Path $outDir "SmokeTest.exe"
& $CscPath /nologo /out:$out /r:"$lib/Kaleidoscope.dll" /r:"$lib/HslCommunication.dll" /r:"$lib/NModbus.dll" /r:"$lib/NModbus.Serial.dll" /r:System.Drawing.dll $src
if ($LASTEXITCODE -ne 0) { throw "编译失败（csc 退出码 $LASTEXITCODE）" }

# ── 拷运行依赖 + 授权文件（存在才拷；无授权环境 HSL 授权用例自动 SKIP）──
foreach ($d in @("Kaleidoscope.dll", "HslCommunication.dll", "NModbus.dll", "NModbus.Serial.dll")) {
    Copy-Item (Join-Path $lib $d) $outDir -Force
}
# 授权文件探测两处（库工程目录 / ConfigEditor 目录——现场跑编辑器生成在后者）
$auth = @((Join-Path $lib "hsl.dat"), (Join-Path $root "ConfigEditor/bin/Debug/hsl.dat")) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($auth) { Copy-Item $auth $outDir -Force }

# ── 运行（退出码透传：0=全过，1=有失败）──
& $out
exit $LASTEXITCODE
