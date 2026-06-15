# Generates app.ico for Mixtape: a vibrant teal-gradient tile with a clean, centred cassette.
# The tape-window + reels group is centred on the icon's TRUE centre (label above, head-access
# holes below — balanced top/bottom), so the mark reads as centred rather than bottom-heavy.
# Reels are concentric "wound tape" rings with spokes + bright teal hubs. PNG-compressed
# multi-size ICO (16..256), valid on Vista+. Detail scales down gracefully.
Add-Type -AssemblyName System.Drawing

function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]([Math]::Max(0.1, $r * 2))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function FillRR($g, $br, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = RoundRect $x $y $w $h $r; $g.FillPath($br, $p); $p.Dispose()
}

# A reel = wound-tape disk (two concentric tones) + teal hub + spokes + spindle hole.
function DrawReel($g, [single]$cx, [single]$cy, [single]$R, $tapeOuter, $tapeInner, $teal, $hole, [bool]$spokes) {
    $ob = New-Object System.Drawing.SolidBrush($tapeOuter)      # outer wound tape
    $g.FillEllipse($ob, ($cx - $R), ($cy - $R), ($R * 2), ($R * 2)); $ob.Dispose()
    $r2 = [single]($R * 0.84)                                   # inner wound tape (subtle ring)
    $ib = New-Object System.Drawing.SolidBrush($tapeInner)
    $g.FillEllipse($ib, ($cx - $r2), ($cy - $r2), ($r2 * 2), ($r2 * 2)); $ib.Dispose()
    $hub = [single]($R * 0.62)                                  # teal hub
    $tb = New-Object System.Drawing.SolidBrush($teal)
    $g.FillEllipse($tb, ($cx - $hub), ($cy - $hub), ($hub * 2), ($hub * 2)); $tb.Dispose()
    if ($spokes) {
        $state = $g.Save()
        $g.TranslateTransform($cx, $cy)
        $sw = [single]($hub * 0.24)
        $sb = New-Object System.Drawing.SolidBrush($hole)
        for ($i = 0; $i -lt 6; $i++) {
            $g.RotateTransform(60)
            $g.FillRectangle($sb, (-$sw / 2), (-$hub * 1.02), $sw, ($hub * 1.04))
        }
        $sb.Dispose()
        $g.Restore($state)
    }
    $ch = [single]($hub * 0.34)                                 # spindle hole
    $cb = New-Object System.Drawing.SolidBrush($hole)
    $g.FillEllipse($cb, ($cx - $ch), ($cy - $ch), ($ch * 2), ($ch * 2)); $cb.Dispose()
}

function DrawIconPng([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $full = New-Object System.Drawing.RectangleF(0, 0, $S, $S)
    $rad = [single]([Math]::Max(2, $S * 0.225))

    # --- vibrant teal tile (diagonal gradient) ---
    $bgPath = RoundRect 0 0 ($S - 1) ($S - 1) $rad
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($full, (C 255 26 214 188), (C 255 8 94 122), 55.0)
    $g.FillPath($bgBrush, $bgPath); $bgBrush.Dispose()
    # glossy top-edge highlight on the tile
    if ($S -ge 32) {
        $rim = RoundRect 1.5 1.5 ($S - 4) ($S - 4) ($rad - 1)
        $rp = New-Object System.Drawing.Pen((C 50 255 255 255), [single][Math]::Max(1, $S * 0.006)); $rp.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($rp, $rim); $rp.Dispose(); $rim.Dispose()
    }
    $bgPath.Dispose()

    # --- cassette body (centred in the tile) ---
    $bw = [single]($S * 0.80); $bh = [single]($S * 0.56)
    $bx = [single](($S - $bw) / 2); $by = [single](($S - $bh) / 2)
    $brad = [single]($S * 0.055)
    $mid = [single]($S / 2.0)                                   # the icon's true centre — reels sit here

    if ($S -ge 32) {                                           # drop shadow
        $shB = New-Object System.Drawing.SolidBrush((C 70 0 0 0))
        FillRR $g $shB $bx ($by + $S * 0.02) $bw $bh $brad; $shB.Dispose()
    }
    $bodyRect = New-Object System.Drawing.RectangleF($bx, $by, $bw, $bh)
    $bodyBr = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bodyRect, (C 255 48 52 62), (C 255 22 25 31), 90.0)
    FillRR $g $bodyBr $bx $by $bw $bh $brad; $bodyBr.Dispose()
    if ($S -ge 48) {                                           # inner top highlight (glassy edge)
        $hl = RoundRect ($bx + $S * 0.012) ($by + $S * 0.012) ($bw - $S * 0.024) ($bh - $S * 0.024) ($brad)
        $hp = New-Object System.Drawing.Pen((C 40 255 255 255), [single]($S * 0.006)); $hp.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($hp, $hl); $hp.Dispose(); $hl.Dispose()
    }

    # --- label strip with written lines (sits ABOVE the centred reels) ---
    if ($S -ge 28) {
        $lx = [single]($bx + $bw * 0.11); $lw = [single]($bw * 0.78)
        $ly = [single]($by + $bh * 0.12); $lh = [single]($bh * 0.21)
        $lRect = New-Object System.Drawing.RectangleF($lx, $ly, $lw, $lh)
        $lBr = New-Object System.Drawing.Drawing2D.LinearGradientBrush($lRect, (C 255 242 244 247), (C 255 212 216 222), 90.0)
        FillRR $g $lBr $lx $ly $lw $lh ([single][Math]::Max(1, $S * 0.026)); $lBr.Dispose()
        if ($S -ge 48) {                                       # two "handwritten" lines + a teal tick
            $lineBr = New-Object System.Drawing.SolidBrush((C 255 150 156 166))
            FillRR $g $lineBr ($lx + $lw * 0.10) ($ly + $lh * 0.32) ($lw * 0.60) ([single]($S * 0.013)) ([single]($S * 0.006))
            FillRR $g $lineBr ($lx + $lw * 0.10) ($ly + $lh * 0.60) ($lw * 0.40) ([single]($S * 0.013)) ([single]($S * 0.006))
            $lineBr.Dispose()
            $tickBr = New-Object System.Drawing.SolidBrush((C 255 0 210 178))
            FillRR $g $tickBr ($lx + $lw * 0.80) ($ly + $lh * 0.30) ($lw * 0.11) ([single]($lh * 0.40)) ([single]($S * 0.012)); $tickBr.Dispose()
        }
    }

    # --- tape window + two reels, vertically CENTRED on the icon ---
    $tapeOuter = C 255 40 44 53
    $tapeInner = C 255 30 33 41
    $teal = C 255 0 210 178
    $hole = C 255 13 14 18
    $cy = $mid                                                  # reels centred on the icon's mid-line
    $R = [single]($bh * 0.185)
    $lcx = [single]($bx + $bw * 0.34); $rcx = [single]($bx + $bw * 0.66)
    if ($S -ge 32) {                                           # exposed tape band behind the reels (recessed window)
        $tw = New-Object System.Drawing.SolidBrush((C 255 17 19 24))
        FillRR $g $tw ($lcx) ($cy - $R * 0.82) ($rcx - $lcx) ($R * 1.64) ([single]($R * 0.5)); $tw.Dispose()
    }
    $detail = $S -ge 48
    DrawReel $g $lcx $cy $R $tapeOuter $tapeInner $teal $hole $detail
    DrawReel $g $rcx $cy $R $tapeOuter $tapeInner $teal $hole $detail

    # --- screws (corners) + head-access holes (bottom, centred) ---
    if ($S -ge 64) {
        $scr = New-Object System.Drawing.SolidBrush((C 255 15 17 21))
        $sr = [single]($S * 0.016)
        foreach ($sx in @(($bx + $bw * 0.07), ($bx + $bw * 0.93))) {
            foreach ($sy in @(($by + $bh * 0.12), ($by + $bh * 0.88))) {
                $g.FillEllipse($scr, ($sx - $sr), ($sy - $sr), ($sr * 2), ($sr * 2))
            }
        }
        $scr.Dispose()
        $hh = New-Object System.Drawing.SolidBrush((C 255 13 14 18))
        $hr = [single]($S * 0.019)
        foreach ($hx in @(($bx + $bw * 0.40), $mid, ($bx + $bw * 0.60))) {
            $g.FillEllipse($hh, ($hx - $hr), ($by + $bh * 0.86 - $hr), ($hr * 2), ($hr * 2))
        }
        $hh.Dispose()
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sizes = @(256, 128, 64, 48, 32, 24, 16)
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = DrawIconPng $s }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $w.Write([byte[]]$pngs[$s]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dir "app.ico"), $out.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $dir "icon-preview.png"), $pngs[256])
$w.Dispose(); $out.Dispose()
Write-Output ("app.ico written: " + (Get-Item (Join-Path $dir "app.ico")).Length + " bytes")
