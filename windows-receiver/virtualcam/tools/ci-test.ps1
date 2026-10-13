# Prueba las cámaras virtuales en un Windows de verdad (GitHub Actions, como administrador):
#  - registro para todo el PC (regsvr32) y que DirectShow liste "Cámara NDI - Celular 1..4";
#  - captura con dos programas distintos (vcamtest con DirectShow y FFmpeg -f dshow, que se conecta como OBS),
#    en 1080p, 4K, 720p, NV12 / YUY2 / RGB32, con y sin receptor;
#  - la cadena completa: celular simulado -> receptor sin interfaz -> cámara virtual -> captura;
#  - (informativo) si Windows muestra las cámaras registradas sólo para el usuario, sin administrador.
# Uso: pwsh virtualcam/tools/ci-test.ps1 -Build <carpeta con CamaraVirtual.dll y vcamtest.exe> -SimBin <SimSender compilado>
param(
    [Parameter(Mandatory = $true)][string]$Build,
    [string]$SimBin = ""
)
$ErrorActionPreference = "Continue"
$dll = (Resolve-Path (Join-Path $Build "CamaraVirtual.dll")).Path
$t = (Resolve-Path (Join-Path $Build "vcamtest.exe")).Path
$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$results = [System.Collections.Generic.List[object]]::new()
$cam1 = "Cámara NDI - Celular 1"

function Check([string]$name, [bool]$ok, [string]$detail, [switch]$Info) {
    $results.Add([pscustomobject]@{ Prueba = $name; Resultado = $(if ($ok) { "OK" } elseif ($Info) { "info" } else { "FALLA" }); Detalle = $detail })
    $mark = if ($ok) { "OK   " } elseif ($Info) { "INFO " } else { "FALLA" }
    Write-Host "[$mark] $name - $detail"
}

function Regsvr([string]$arguments) {
    $p = Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList $arguments -Wait -PassThru
    return $p.ExitCode
}

function ListCount() {
    $out = & $t list 2>&1 | Out-String
    Write-Host $out
    if ($out -match 'TOTAL (\d+)') { return [int]$Matches[1] }
    return -1
}

function Grab([int]$cam, [int]$frames, [int]$w = 0, [int]$h = 0) {
    $out = & $t grab $cam $frames $w $h 2>&1 | Out-String
    Write-Host $out
    if ($out -match 'RESULTADO imagenes=(\d+) fps=([\d.]+) ancho=(\d+) alto=(\d+) bytes=\d+ cambios=(\d+)') {
        return [pscustomobject]@{ Ok = $true; Frames = [int]$Matches[1]; Fps = [double]$Matches[2]; W = [int]$Matches[3]; H = [int]$Matches[4]; Changes = [int]$Matches[5]; Raw = $out }
    }
    return [pscustomobject]@{ Ok = $false; Raw = $out; NoGrabber = ($out -match 'grabber=0+ |grabber=\(nil\)|grabber=0000000000000000') }
}

function StartFeed([int]$cam, [int]$seconds, [int]$w = 1920, [int]$h = 1080) {
    return Start-Process -FilePath $t -ArgumentList "feed $cam $seconds $w $h 30" -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $tmp "feed$cam.txt")
}

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
function FfCapture([string]$device, [string]$size, [string]$pixfmt, [double]$seconds, [string]$tag) {
    $md5 = Join-Path $tmp "cap-$tag.md5"
    $log = Join-Path $tmp "cap-$tag.log"
    Remove-Item $md5, $log -ErrorAction SilentlyContinue
    $ffArgs = @("-hide_banner", "-f", "dshow", "-rtbufsize", "300M", "-video_size", $size, "-framerate", "30", "-pixel_format", $pixfmt,
                "-i", "video=$device", "-t", "$seconds", "-f", "framemd5", "-y", $md5)
    & $ffmpeg @ffArgs 2> $log
    $code = $LASTEXITCODE
    $lines = if (Test-Path $md5) { @(Get-Content $md5 | Where-Object { $_ -and -not $_.StartsWith("#") }) } else { @() }
    $hashes = @($lines | ForEach-Object { ($_ -split ',')[-1].Trim() } | Sort-Object -Unique)
    $sizes = @($lines | ForEach-Object { [int](($_ -split ',')[4].Trim()) } | Sort-Object -Unique)
    $stream = (Get-Content $log -ErrorAction SilentlyContinue | Select-String "Stream #0:0" | Select-Object -First 1)
    Write-Host "ffmpeg $tag -> código $code, imágenes $($lines.Count), distintas $($hashes.Count), bytes $($sizes -join '/'), $stream"
    if ($code -ne 0) { Get-Content $log -Tail 15 | ForEach-Object { Write-Host "   $_" } }
    return [pscustomobject]@{ Code = $code; Frames = $lines.Count; Distinct = $hashes.Count; Bytes = ($sizes | Select-Object -First 1); Stream = "$stream" }
}

Write-Host "== Proceso: $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
whoami /groups | Select-String "Mandatory Label" | ForEach-Object { Write-Host "   $_" }

# ---------------------------------------------------------------- 1. Registro para todo el PC
$rc = Regsvr "/s `"$dll`""
Check "Registro para todo el PC (regsvr32)" ($rc -eq 0) "código $rc"
$n = ListCount
Check "DirectShow lista las 4 cámaras" ($n -eq 4) "TOTAL $n"

# ---------------------------------------------------------------- 2. Captura con vcamtest (DirectShow)
$feed = StartFeed 1 40
Start-Sleep -Seconds 1
$g = Grab 1 90 1920 1080
$haveGrabber = $g.Ok -or -not $g.NoGrabber
if ($g.Ok) {
    Check "vcamtest 1080p (receptor de prueba)" ($g.Frames -ge 90 -and $g.W -eq 1920 -and $g.H -eq 1080 -and $g.Changes -ge 30 -and $g.Fps -ge 20 -and $g.Fps -le 45) "$($g.Frames) imágenes, $($g.Fps) fps, $($g.W)x$($g.H), $($g.Changes) cambios"
    $g = Grab 1 30 3840 2160
    Check "vcamtest 4K" ($g.Ok -and $g.Frames -ge 30 -and $g.W -eq 3840 -and $g.H -eq 2160 -and $g.Changes -ge 5) "$($g.Frames) imágenes, $($g.Fps) fps, $($g.W)x$($g.H), $($g.Changes) cambios"
    $g = Grab 1 30 1280 720
    Check "vcamtest 720p" ($g.Ok -and $g.Frames -ge 30 -and $g.W -eq 1280 -and $g.H -eq 720) "$($g.Frames) imágenes, $($g.W)x$($g.H)"
} elseif ($g.NoGrabber) {
    Check "vcamtest (SampleGrabber de Windows)" $false "este Windows no tiene qedit.dll: se usa sólo FFmpeg" -Info
} else {
    Check "vcamtest 1080p (receptor de prueba)" $false ($g.Raw -replace "`r?`n", " ")
}

# ---------------------------------------------------------------- 3. Captura con FFmpeg (se conecta como OBS)
if (-not $ffmpeg) {
    Write-Host "Instalando FFmpeg…"
    choco install ffmpeg -y --no-progress | Out-Null
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
    $ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
}
if ($ffmpeg) {
    $devs = & $ffmpeg -hide_banner -list_devices true -f dshow -i dummy 2>&1 | Out-String
    Write-Host $devs
    Check "FFmpeg ve las cámaras" ($devs -match 'Celular 1' -and $devs -match 'Celular 4') "lista de dispositivos dshow"
    if ($feed.HasExited) { $feed = StartFeed 1 40 }
    $c = FfCapture $cam1 "1920x1080" "nv12" 4 "nv12-1080"
    Check "FFmpeg 1080p NV12" ($c.Code -eq 0 -and $c.Frames -ge 90 -and $c.Distinct -ge 30 -and $c.Bytes -eq 3110400) "$($c.Frames) imágenes, $($c.Distinct) distintas, $($c.Bytes) bytes"
    $c = FfCapture $cam1 "3840x2160" "nv12" 3 "nv12-4k"
    Check "FFmpeg 4K NV12" ($c.Code -eq 0 -and $c.Frames -ge 30 -and $c.Distinct -ge 10 -and $c.Bytes -eq 12441600) "$($c.Frames) imágenes, $($c.Distinct) distintas, $($c.Bytes) bytes"
    $c = FfCapture $cam1 "1280x720" "yuyv422" 3 "yuy2-720"
    Check "FFmpeg 720p YUY2" ($c.Code -eq 0 -and $c.Frames -ge 60 -and $c.Distinct -ge 15 -and $c.Bytes -eq 1843200) "$($c.Frames) imágenes, $($c.Distinct) distintas, $($c.Bytes) bytes"
    $c = FfCapture $cam1 "1920x1080" "bgr0" 3 "rgb32-1080"
    Check "FFmpeg 1080p RGB32" ($c.Code -eq 0 -and $c.Frames -ge 45 -and $c.Distinct -ge 15 -and $c.Bytes -eq 8294400) "$($c.Frames) imágenes, $($c.Distinct) distintas, $($c.Bytes) bytes"
} else {
    Check "FFmpeg" $false "no se pudo instalar FFmpeg"
}
if (-not $feed.HasExited) { Stop-Process -Id $feed.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 4  # que la cámara note que el "receptor" se fue

# ---------------------------------------------------------------- 4. Sin receptor: cartel de espera
if ($haveGrabber) {
    $g = Grab 2 20
    Check "Sin receptor: la cámara igual entrega imagen (cartel)" ($g.Ok -and $g.Frames -ge 20 -and $g.W -eq 1920 -and $g.H -eq 1080) "$($g.Frames) imágenes, $($g.W)x$($g.H)"
} elseif ($ffmpeg) {
    $c = FfCapture "Cámara NDI - Celular 2" "1920x1080" "nv12" 2 "cartel"
    Check "Sin receptor: la cámara igual entrega imagen (cartel)" ($c.Code -eq 0 -and $c.Frames -ge 30) "$($c.Frames) imágenes"
}

# ---------------------------------------------------------------- 5. Cadena completa con el receptor
if ($SimBin) {
    $sim = (Resolve-Path (Join-Path $SimBin "SimSender.dll")).Path
    $rxLog = Join-Path $tmp "receptor.txt"
    $rx = Start-Process -FilePath dotnet -ArgumentList "`"$sim`" --receiver --vcam --no-ndi --seconds 45" -PassThru -NoNewWindow -RedirectStandardOutput $rxLog -RedirectStandardError (Join-Path $tmp "receptor-err.txt")
    Start-Sleep -Seconds 3
    $phone = Start-Process -FilePath dotnet -ArgumentList "`"$sim`" --size 1920x1080 --fps 30 --kbps 8000 --seconds 35 --name Simulado" -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $tmp "celular.txt") -RedirectStandardError (Join-Path $tmp "celular-err.txt")
    Start-Sleep -Seconds 6
    if ($haveGrabber) {
        $g = Grab 1 90 1280 720
        Check "Celular simulado -> receptor -> cámara (vcamtest 720p)" ($g.Ok -and $g.Frames -ge 90 -and $g.W -eq 1280 -and $g.H -eq 720 -and $g.Changes -ge 30) "$($g.Frames) imágenes, $($g.Fps) fps, $($g.W)x$($g.H), $($g.Changes) cambios"
        $g = Grab 1 60 1920 1080
        Check "Celular simulado -> receptor -> cámara (vcamtest 1080p)" ($g.Ok -and $g.Frames -ge 60 -and $g.W -eq 1920 -and $g.Changes -ge 20) "$($g.Frames) imágenes, $($g.Fps) fps, $($g.W)x$($g.H), $($g.Changes) cambios"
    }
    if ($ffmpeg) {
        $c = FfCapture $cam1 "1920x1080" "nv12" 4 "cadena-1080"
        Check "Celular simulado -> receptor -> cámara (FFmpeg 1080p)" ($c.Code -eq 0 -and $c.Frames -ge 90 -and $c.Distinct -ge 60) "$($c.Frames) imágenes, $($c.Distinct) distintas"
    }
    $phone | Wait-Process -Timeout 60 -ErrorAction SilentlyContinue
    $rx | Wait-Process -Timeout 60 -ErrorAction SilentlyContinue
    Write-Host "--- receptor ---"; Get-Content $rxLog -Tail 25 | ForEach-Object { Write-Host "   $_" }
    Write-Host "--- celular ---"; Get-Content (Join-Path $tmp "celular.txt") -Tail 5 | ForEach-Object { Write-Host "   $_" }
    $assigned = Select-String -Path $rxLog -Pattern "cámara 1 en uso True" -Quiet
    Check "El receptor asignó el celular a la Cámara 1 y vio a OBS usándola" ([bool]$assigned) "registro del receptor"
}

# ---------------------------------------------------------------- 5b. Procesador de video de la GPU (informativo)
if ($SimBin) {
    $probe = & dotnet $sim --probe-gpu 2>&1 | Out-String
    Write-Host $probe
    Check "Procesador de video D3D11 (camino de la mejora con IA)" ($probe -match '-> OK') (($probe -split "`n" | Select-String "PROCESADOR") -join " ") -Info
}

# ---------------------------------------------------------------- 6. Registro sólo para el usuario (informativo)
$rc = Regsvr "/u /s `"$dll`""
$n = ListCount
Check "Quitar el registro para todo el PC" ($rc -eq 0 -and $n -eq 0) "código $rc, TOTAL $n"
$rc = Regsvr "/s /n /i:user `"$dll`""
$n = ListCount
Check "Registro sólo para el usuario: lo ve un proceso administrador" ($rc -eq 0 -and $n -eq 4) "código $rc, TOTAL $n" -Info
# Mismo usuario pero sin privilegios de administrador (como un OBS normal).
$cmdFile = Join-Path $tmp "list-basico.cmd"
$outFile = Join-Path $tmp "list-basico.txt"
Remove-Item $outFile -ErrorAction SilentlyContinue
Set-Content -Path $cmdFile -Value "@`"$t`" list > `"$outFile`" 2>&1" -Encoding ascii
& runas /trustlevel:0x20000 "$cmdFile" | Out-Null
for ($i = 0; $i -lt 30 -and -not ((Test-Path $outFile) -and ((Get-Content $outFile -Raw) -match 'TOTAL'))) { Start-Sleep -Milliseconds 500 }
$basic = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { "(sin salida)" }
Write-Host $basic
$bn = if ($basic -match 'TOTAL (\d+)') { [int]$Matches[1] } else { -1 }
Check "Registro sólo para el usuario: lo ve un proceso SIN administrador" ($bn -eq 4) "TOTAL $bn" -Info
$rc = Regsvr "/s /u /n /i:user `"$dll`""
$n = ListCount
Check "Quitar el registro del usuario" ($rc -eq 0 -and $n -eq 0) "código $rc, TOTAL $n"

Write-Host ""
$results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object { $_.Resultado -eq "FALLA" })
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) prueba(s) fallaron"
    exit 1
}
Write-Host "Todas las pruebas obligatorias pasaron"
exit 0
