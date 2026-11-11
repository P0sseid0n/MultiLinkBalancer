# Compila o MultiLink Balancer: front (Svelte/Vite) + app (C# com o compilador do .NET Framework do Windows).
# Resultado: um único MultiLinkBalancer.exe com a UI e as DLLs do WebView2 embutidas.
#   ./build.ps1            compila tudo
#   ./build.ps1 -SkipUi    reaproveita ui/dist (só recompila o C#)
#   ./build.ps1 -Version 1.2.0   versão gravada no .exe (padrão: a última tag vX.Y.Z do git)
#   ./build.ps1 -Restart   compila, fecha o MultiLink aberto (como pelo "Sair"), troca o .exe e reabre
#   ./build.ps1 -Install   como -Restart, mas troca a cópia instalada em Arquivos de Programas (pede administrador)
param([switch]$SkipUi, [string]$Out, [string]$Version, [switch]$Restart, [switch]$Install)
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
$final = $out
if ($Install) {
    # a cópia instalada pelo app (Ajustes > Instalar no computador, src/Platform/Installation.cs)
    $Restart = $true
    $final = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'MultiLinkBalancer\MultiLinkBalancer.exe'
    if (-not (Test-Path $final)) { throw "O MultiLink não está instalado ($final): instale pelo app em Ajustes > Instalar no computador." }
    $out = Join-Path $env:TEMP 'multilink-build\MultiLinkBalancer.new.exe' # a pasta instalada só aceita gravação de administrador
}
elseif ($Restart) { $out = "$final.new" } # o app aberto segura o .exe: compila ao lado e troca depois que ele fechar
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs -Recurse | ForEach-Object FullName

# versão: comparada com a última release do GitHub pela atualização automática
if (-not $Version) {
    $tag = $null
    try { $tag = git -C $root describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null; if ($LASTEXITCODE) { $tag = $null } } catch { }
    $Version = if ($tag) { $tag.TrimStart('v') } else { '0.0.0' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versão inválida: $Version (use o formato 1.2.3)" }

# ícone do .exe: gerado pelo mesmo desenho do ícone da bandeja (src/Ui/AppIcon.cs)
$tmp = Join-Path $env:TEMP 'multilink-build'
New-Item -ItemType Directory -Force $tmp | Out-Null
$ico = Join-Path $tmp 'app.ico'
$verCs = Join-Path $tmp 'Version.cs'
Set-Content $verCs "[assembly: System.Reflection.AssemblyVersion(`"$Version.0`")]`n[assembly: System.Reflection.AssemblyFileVersion(`"$Version.0`")]"
# build local: grava a pasta do repositório no .exe (src/Platform/DevBuild.cs). A cópia instalada em Arquivos de Programas
# continua no modo de desenvolvimento e recompila a partir daqui. O build do GitHub Actions (release) não leva.
if ($env:GITHUB_ACTIONS -ne 'true') {
    Add-Content $verCs ("[assembly: System.Reflection.AssemblyMetadata(`"DevRepository`", @`"{0}`")]" -f $root.Replace('"', '""'))
}
Set-Content (Join-Path $tmp 'IcoTool.cs') 'namespace MultiLink { static class IcoTool { static void Main(string[] args) { Ui.AppIcon.WriteIco(args[0]); } } }'
& $csc /nologo /target:exe "/out:$tmp\IcoTool.exe" /r:System.Drawing.dll (Join-Path $root 'src\Ui\AppIcon.cs') (Join-Path $tmp 'IcoTool.cs')
if ($LASTEXITCODE) { throw 'compilação do gerador de ícone falhou' }
& "$tmp\IcoTool.exe" $ico
if ($LASTEXITCODE) { throw 'geração do ícone falhou' }

# vigia do proxy do Windows (watchdog\Watchdog.cs): exe pequeno embutido, só com os arquivos de src que ele usa
$watchdog = Join-Path $tmp 'multilink-watchdog.exe'
& $csc /nologo /target:winexe /optimize+ /platform:x64 "/out:$watchdog" "/win32icon:$ico" $verCs `
    (Join-Path $root 'watchdog\Watchdog.cs') `
    (@('Platform\SingleInstance.cs', 'Platform\SystemProxy.cs', 'Platform\RedirectionGuard.cs', 'Storage\AppSettings.cs', 'Storage\AppPaths.cs', 'Common\ActivityLog.cs') | ForEach-Object { Join-Path $root "src\$_" })
if ($LASTEXITCODE) { throw 'compilação do vigia falhou' }

& $csc /nologo /target:winexe /optimize+ /platform:x64 /nowarn:4014 "/out:$out" "/win32icon:$ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Management.dll `
    "/r:$wv\Microsoft.Web.WebView2.Core.dll" `
    "/resource:$html,ui.index.html" `
    "/resource:$wv\Microsoft.Web.WebView2.Core.dll,wv2.core.dll" `
    "/resource:$wv\WebView2Loader.dll,wv2.loader.dll" `
    "/resource:$t2s\tun2socks.exe,t2s.exe" `
    "/resource:$t2s\wintun.dll,t2s.wintun.dll" `
    "/resource:$watchdog,watchdog.exe" `
    $sources $verCs
if ($LASTEXITCODE) { throw 'compilação do C# falhou' }
"OK: $out v$Version ({0:N0} KB)" -f ((Get-Item $out).Length / 1KB)
if (-not $Restart) { return }

# Fecha o MultiLink aberto pelo evento que ele escuta: sai como pelo "Sair" da bandeja, restaurando o proxy do
# Windows e as rotas (matar o processo deixaria o Windows apontando para um proxy parado).
# O processo roda como administrador? Pelo token dele. Sem administrador, nem dá para abrir um processo elevado (isso
# já responde); com administrador (este terminal elevado), dá, e só o token diz.
function Test-Elevated($process) {
    Add-Type -Namespace MultiLinkBuild -Name Token -MemberDefinition @'
[DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
[DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
'@ -ErrorAction SilentlyContinue
    try { $handle = $process.Handle } catch { $handle = $null }
    if ($null -eq $handle) { return $true } # o PowerShell devolve vazio quando o acesso é negado
    $token = [IntPtr]::Zero
    if (-not [MultiLinkBuild.Token]::OpenProcessToken($handle, 0x0008, [ref]$token)) { return $true } # TOKEN_QUERY negado
    try {
        $elevated = 0; $size = 0
        if (-not [MultiLinkBuild.Token]::GetTokenInformation($token, 20, [ref]$elevated, 4, [ref]$size)) { return $true } # TokenElevation
        return $elevated -ne 0
    }
    finally { [MultiLinkBuild.Token]::CloseHandle($token) | Out-Null }
}

$running = Get-Process MultiLinkBalancer -ErrorAction SilentlyContinue | Select-Object -First 1
$wasElevated = $false
if ($running) {
    $wasElevated = Test-Elevated $running
    $exitRequest = $null
    if (-not [System.Threading.EventWaitHandle]::TryOpenExisting('Local\MultiLinkBalancer.Exit', [ref]$exitRequest)) {
        throw 'O MultiLink aberto é de uma versão sem fechamento automático: feche pelo "Sair" da bandeja e rode de novo.'
    }
    $exitRequest.Set() | Out-Null
    $exitRequest.Dispose()
    if (-not $running.WaitForExit(30000)) { throw 'O MultiLink não fechou em 30 s.' }
    "MultiLink fechado."
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Install) {
    # copia (não move): mover levaria junto as permissões e o dono do %TEMP%, e o .exe instalado deixaria de ser
    # protegido. Sem administrador, a cópia roda elevada (o Windows pede confirmação).
    $copy = "Copy-Item -Force -LiteralPath '{0}' -Destination '{1}'" -f $out.Replace("'", "''"), $final.Replace("'", "''")
    if ($isAdmin) { Copy-Item -Force -LiteralPath $out -Destination $final }
    else {
        $copier = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-Command', $copy
        if ($copier.ExitCode) { throw 'Não foi possível trocar o .exe instalado (confirmação de administrador cancelada?).' }
    }
    Remove-Item $out
}
else { Move-Item -Force $out $final }

# Reabre como estava. Elevado: pela tarefa agendada do "iniciar com o Windows" (sem pedir confirmação), se ela
# abre este mesmo .exe; senão o Windows pede a confirmação de administrador.
$task = Get-ScheduledTask -TaskName MultiLinkBalancer -ErrorAction SilentlyContinue
$taskRunsThis = $task -and ($task.Actions | Where-Object { $_.Execute -and ([IO.Path]::GetFullPath($_.Execute) -eq [IO.Path]::GetFullPath($final)) })
# a cópia instalada roda como administrador: reabre pela tarefa mesmo que estivesse aberta sem (ou fechada)
if (($wasElevated -or $Install) -and $taskRunsThis) { Start-ScheduledTask -TaskName MultiLinkBalancer }
elseif ($wasElevated) { Start-Process $final '/tray' -Verb RunAs }
else { Start-Process $final '/tray' }
"MultiLink reaberto."
