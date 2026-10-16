# Compila o MultiLink Balancer: front (Svelte/Vite) + app (C# com o compilador do .NET Framework do Windows).
# Resultado: um único MultiLinkBalancer.exe com a UI e as DLLs do WebView2 embutidas.
#   ./build.ps1            compila tudo
#   ./build.ps1 -SkipUi    reaproveita ui/dist (só recompila o C#)
#   ./build.ps1 -Version 1.2.0   versão gravada no .exe (padrão: a última tag vX.Y.Z do git)
param([switch]$SkipUi, [string]$Out, [string]$Version)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $SkipUi) {
    Push-Location (Join-Path $root 'ui')
    try {
        if (-not (Test-Path node_modules)) { npm install --no-fund --no-audit; if ($LASTEXITCODE) { throw 'npm install falhou' } }
        npm run build; if ($LASTEXITCODE) { throw 'build do front falhou' }
    }
    finally { Pop-Location }
}

$html = Join-Path $root 'ui\dist\index.html'
if (-not (Test-Path $html)) { throw "Front não compilado: $html não existe (rode sem -SkipUi)." }
$wv = Join-Path $root 'lib\webview2'
$t2s = Join-Path $root 'lib\tun2socks' # captura de todos os programas (ver lib\tun2socks\README.txt)
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = if ($Out) { $Out } else { Join-Path $root 'MultiLinkBalancer.exe' }
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName

# versão: comparada com a última release do GitHub pela atualização automática
if (-not $Version) {
    $tag = $null
    try { $tag = git -C $root describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null; if ($LASTEXITCODE) { $tag = $null } } catch { }
    $Version = if ($tag) { $tag.TrimStart('v') } else { '0.0.0' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versão inválida: $Version (use o formato 1.2.3)" }

# ícone do .exe: gerado pelo mesmo desenho do ícone da bandeja (src/AppIcon.cs)
$tmp = Join-Path $env:TEMP 'multilink-build'
New-Item -ItemType Directory -Force $tmp | Out-Null
$ico = Join-Path $tmp 'app.ico'
$verCs = Join-Path $tmp 'Version.cs'
Set-Content $verCs "[assembly: System.Reflection.AssemblyVersion(`"$Version.0`")]`n[assembly: System.Reflection.AssemblyFileVersion(`"$Version.0`")]"
Set-Content (Join-Path $tmp 'IcoTool.cs') 'namespace MultiLink { static class IcoTool { static void Main(string[] a) { AppIcon.WriteIco(a[0]); } } }'
& $csc /nologo /target:exe "/out:$tmp\IcoTool.exe" /r:System.Drawing.dll (Join-Path $root 'src\AppIcon.cs') (Join-Path $tmp 'IcoTool.cs')
if ($LASTEXITCODE) { throw 'compilação do gerador de ícone falhou' }
& "$tmp\IcoTool.exe" $ico
if ($LASTEXITCODE) { throw 'geração do ícone falhou' }

& $csc /nologo /target:winexe /optimize+ /platform:x64 /nowarn:4014 "/out:$out" "/win32icon:$ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    "/r:$wv\Microsoft.Web.WebView2.Core.dll" `
    "/resource:$html,ui.index.html" `
    "/resource:$wv\Microsoft.Web.WebView2.Core.dll,wv2.core.dll" `
    "/resource:$wv\WebView2Loader.dll,wv2.loader.dll" `
    "/resource:$t2s\tun2socks.exe,t2s.exe" `
    "/resource:$t2s\wintun.dll,t2s.wintun.dll" `
    $sources $verCs
if ($LASTEXITCODE) { throw 'compilação do C# falhou' }
"OK: $out v$Version ({0:N0} KB)" -f ((Get-Item $out).Length / 1KB)
