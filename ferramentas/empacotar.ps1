# DSE -- gera o portable (framework-dependent) e o zip da Release.
# Uso: powershell -ExecutionPolicy Bypass -File ferramentas\empacotar.ps1
# O build roda numa copia em pasta temporaria: o projeto nao ganha bin\ nem obj\.
# Saida: dist\DSE\DSE.App.exe e dist\DSE-<versao>-windows.zip. Atualiza dist\DSE NO LUGAR
# (so o que mudou; nada e apagado). DSE de dist\ aberto: fecha pedindo e reabre no fim.
# Relatorio: relatorios\empacotar.txt

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $raiz 'src\DSE.App\DSE.App.csproj'
$versao = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $raiz 'dist'
$saida = Join-Path $dist 'DSE'
$zip = Join-Path $dist "DSE-$versao-windows.zip"
$rel = Join-Path $raiz 'relatorios'
$relArq = Join-Path $rel 'empacotar.txt'
$temp = Join-Path ([IO.Path]::GetTempPath()) ("dse-build-" + [guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory -Force $rel | Out-Null
function Log($t) { Add-Content -Path $relArq -Value $t -Encoding UTF8; Write-Host $t }
Set-Content -Path $relArq -Value "[empacotar] $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') versao $versao" -Encoding UTF8

$ok = $false
$estavaAberto = $false
$exe = Join-Path $saida 'DSE.App.exe'
try {
    # Primeiro dotnet com SDK: o do PATH (pode ser so o runtime) ou o portatil em A:\opt\dotnet.
    $dotnet = @((Get-Command dotnet -All -ErrorAction SilentlyContinue).Source) + 'A:\opt\dotnet\dotnet.exe' |
        Where-Object { $_ -and (Test-Path $_) -and (& $_ --list-sdks) } | Select-Object -First 1
    if (-not $dotnet) { throw '.NET SDK nao encontrado (PATH ou A:\opt\dotnet)' }

    # Copia so o codigo (sem bin/obj) para a pasta temporaria.
    New-Item -ItemType Directory -Force $temp | Out-Null
    Copy-Item (Join-Path $raiz 'DSE.sln'), (Join-Path $raiz 'nuget.config') $temp
    robocopy (Join-Path $raiz 'src') (Join-Path $temp 'src') /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy falhou ($LASTEXITCODE)" }

    Log '[1/3] Publicando...'
    $pub = Join-Path $temp 'pub'
    & $dotnet publish (Join-Path $temp 'src\DSE.App\DSE.App.csproj') -c Release -r win-x64 --self-contained false -o $pub --nologo -v q | Out-File -Append -Encoding UTF8 $relArq
    if ($LASTEXITCODE -ne 0) { throw 'o publish falhou (detalhe acima)' }
    Get-ChildItem $pub -Filter *.pdb -Recurse | Remove-Item -Force
    foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'README.md') {
        $p = Join-Path $raiz $f; if (Test-Path $p) { Copy-Item $p $pub }
    }

    # O DSE de dist\ aberto prende os arquivos: pede pra ele sair (devolve os
    # controles ao Windows); a força só se nao atender. Reabre no fim.
    $exe = Join-Path $saida 'DSE.App.exe'
    $abertos = @(Get-Process DSE.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    $estavaAberto = $abertos.Count -gt 0
    if ($estavaAberto) {
        try { $s = [Threading.EventWaitHandle]::OpenExisting('DSE.App.Sair'); [void]$s.Set(); $s.Dispose() } catch { }
        foreach ($p in $abertos) { if (-not $p.WaitForExit(10000)) { Stop-Process -Id $p.Id -Force; Log '  (o DSE nao fechou sozinho: fechado a forca)' } }
        Log '  DSE aberto foi fechado para atualizar'
    }

    # ATUALIZA NO LUGAR: copia por cima so o que mudou; nada de dist\DSE e apagado
    # (log, relatorios e o que o programa criou ficam onde estao).
    New-Item -ItemType Directory -Force $saida | Out-Null
    robocopy $pub $saida /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copia para dist falhou ($LASTEXITCODE)" }

    Log '[2/3] Compactando...'
    # O zip sai da publicacao limpa (sem o que o programa criou em dist\DSE).
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -Force

    Log '[3/3] Autoteste...'
    if (-not (Test-Path $exe)) { throw 'DSE.App.exe nao foi gerado' }
    foreach ($d in 'DSE.App.dll', 'DSE.Core.dll', 'DSE.App.runtimeconfig.json') {
        if (-not (Test-Path (Join-Path $saida $d))) { throw "falta $d no portable" }
    }
    $vExe = (Get-Item (Join-Path $saida 'DSE.App.dll')).VersionInfo.ProductVersion
    if (-not $vExe.StartsWith($versao)) { throw "versao do exe ($vExe) diferente do csproj ($versao)" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($zip)
    try { if (-not ($z.Entries | Where-Object { $_.FullName -eq 'DSE.App.exe' })) { throw 'zip sem DSE.App.exe' } } finally { $z.Dispose() }
    # O exe monta a janela fora da tela (sem ViGEm/HidHide) e grava relatorios\autoteste.txt.
    # Com $env:DSE_FOTOS definido, grava tambem as fotos de cada tela nessa pasta.
    $argsAuto = @('--autoteste')
    if ($env:DSE_FOTOS) { $argsAuto += $env:DSE_FOTOS }
    $p = Start-Process -FilePath $exe -ArgumentList $argsAuto -PassThru -Wait
    $relAuto = Join-Path $saida 'relatorios\autoteste.txt'
    $txtAuto = if (Test-Path $relAuto) { Get-Content $relAuto -Raw } else { '' }
    # So o arquivo do autoteste sai (a pasta fica se tiver mais coisa).
    Remove-Item -Force $relAuto -ErrorAction SilentlyContinue
    $pRel = Join-Path $saida 'relatorios'
    if ((Test-Path $pRel) -and -not (Get-ChildItem $pRel)) { Remove-Item $pRel }
    if ($p.ExitCode -ne 0 -or $txtAuto -notmatch '(?m)^ok\s*$') { throw "autoteste do exe falhou: $txtAuto" }
    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Log "OK: $exe (versao $vExe) | zip $mb MB"
    $ok = $true
}
catch {
    Log "ERRO: $($_.Exception.Message)"
}
finally {
    if (Test-Path $temp) { Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue }
    if ($estavaAberto -and (Test-Path $exe)) { Start-Process $exe; Log '  DSE reaberto' }
    Log "[fim] $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $(if ($ok) { 'ok' } else { 'com falha' })"
}
if (-not $ok) { exit 1 }
