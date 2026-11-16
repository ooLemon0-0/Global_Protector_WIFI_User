$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'}
$output=Join-Path $repository 'dist\branding'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$renderer=Join-Path $output 'BrandRenderer.exe'
& $compiler /nologo /target:exe /reference:System.Drawing.dll ('/out:'+$renderer) (Join-Path $PSScriptRoot 'BrandRenderer.cs')
if($LASTEXITCODE -ne 0){throw 'Logo renderer compilation failed.'}
& $renderer (Join-Path $repository 'assets')
if($LASTEXITCODE -ne 0){throw 'Logo rendering failed.'}
