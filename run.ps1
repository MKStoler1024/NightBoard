<#
  启动夜板。

  用法：
    .\run.ps1                  正常全屏启动
    .\run.ps1 -Topmost         置顶
    .\run.ps1 -Scale 1.2       整体字号放大两成
    .\run.ps1 -Gpu dgpu        多卡机器只看独显
#>
param(
    [switch]$Topmost,
    [double]$Scale = 1.0,
    [ValidateSet("all", "igpu", "dgpu")]
    [string]$Gpu = "all"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "bin\Release\net10.0\NightBoard.exe"

if (-not (Test-Path $exe)) {
    Write-Host "还没编译，先跑一次 dotnet build -c Release" -ForegroundColor Yellow
    dotnet build (Join-Path $root "NightBoard.csproj") -c Release --nologo | Out-Null
}

$argsList = @()
if ($Topmost) { $argsList += "--topmost" }
if ($Scale -ne 1.0) {
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    $argsList += @("--scale", $Scale.ToString($inv))
}
if ($Gpu -ne "all") { $argsList += @("--gpu", $Gpu) }

Start-Process -FilePath $exe -ArgumentList $argsList
