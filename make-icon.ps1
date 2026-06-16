# Generates app.ico (+ matching PNGs) for Mixtape: the "modern cassette" mark on the app's signature
# teal-gradient tile. A clean, slightly flatter cassette — dark body, a light label strip with a teal
# tick, two wound-tape reels with bright teal hubs over a recessed tape window. No screws/head-holes,
# so it stays legible when shrunk to the 24px sidebar logo. PNG-compressed multi-size ICO (16..256).
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

# A reel = wound-tape disk (two concentric tones) + teal hub + (optional) spokes + spindle hole.
function DrawReel($g, [single]$cx, [single]$cy, [single]$R, $tapeOuter, $tapeInner, $teal, $hole, [bool]$spokes) {
    $ob = New-Object System.Drawing.SolidBrush($tapeOuter)
    $g.FillEllipse($ob, ($cx - $R), ($cy - $R), ($R * 2), ($R * 2)); $ob.Dispose()
    $r2 = [single]($R * 0.84)
    $ib = New-Object System.Drawing.SolidBrush($tapeInner)
    $g.FillEllipse($ib, ($cx - $r2), ($cy - $r2), ($r2 * 2), ($r2 * 2)); $ib.Dispose()
    $hub = [single]($R * 0.62)
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
    $ch = [single]($hub * 0.34)
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
    if ($S -ge 32) {                                           # glossy top-edge highlight
        $rim = RoundRect 1.5 1.5 ($S - 4) ($S - 4) ($rad - 1)
        $rp = New-Object System.Drawing.Pen((C 55 255 255 255), [single][Math]::Max(1, $S * 0.007)); $rp.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($rp, $rim); $rp.Dispose(); $rim.Dispose()
    }
    $bgPath.Dispose()

    # --- cassette body (centred, flatter than before) ---
    $bw = [single]($S * 0.78); $bh = [single]($S * 0.50)
    $bx = [single](($S - $bw) / 2); $by = [single](($S - $bh) / 2)
    $brad = [single]($S * 0.07)

    if ($S -ge 32) {                                           # drop shadow
        $shB = New-Object System.Drawing.SolidBrush((C 60 0 0 0))
        FillRR $g $shB $bx ($by + $S * 0.02) $bw $bh $brad; $shB.Dispose()
    }
    $bodyRect = New-Object System.Drawing.RectangleF($bx, $by, $bw, $bh)
    $bodyBr = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bodyRect, (C 255 52 56 66), (C 255 24 27 33), 90.0)
    FillRR $g $bodyBr $bx $by $bw $bh $brad; $bodyBr.Dispose()
    if ($S -ge 48) {                                           # inner top highlight (glassy edge)
        $hl = RoundRect ($bx + $S * 0.012) ($by + $S * 0.012) ($bw - $S * 0.024) ($bh - $S * 0.024) ($brad)
        $hp = New-Object System.Drawing.Pen((C 40 255 255 255), [single]($S * 0.006)); $hp.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($hp, $hl); $hp.Dispose(); $hl.Dispose()
    }

    # --- light label strip (above the reels) with a single teal tick ---
    if ($S -ge 24) {
        $lx = [single]($bx + $bw * 0.12); $lw = [single]($bw * 0.76)
        $ly = [single]($by + $bh * 0.13); $lh = [single]($bh * 0.22)
        $lRect = New-Object System.Drawing.RectangleF($lx, $ly, $lw, $lh)
        $lBr = New-Object System.Drawing.Drawing2D.LinearGradientBrush($lRect, (C 255 244 246 248), (C 255 214 218 224), 90.0)
        FillRR $g $lBr $lx $ly $lw $lh ([single][Math]::Max(1, $S * 0.03)); $lBr.Dispose()
        if ($S -ge 40) {
            $tickBr = New-Object System.Drawing.SolidBrush((C 255 0 210 178))
            FillRR $g $tickBr ($lx + $lw * 0.80) ($ly + $lh * 0.28) ($lw * 0.12) ($lh * 0.44) ([single]($S * 0.012)); $tickBr.Dispose()
        }
    }

    # --- recessed tape window + two reels ---
    $tapeOuter = C 255 42 46 55
    $tapeInner = C 255 30 33 41
    $teal = C 255 0 210 178
    $hole = C 255 13 14 18
    $cy = [single]($by + $bh * 0.66)
    $R = [single]($bh * 0.20)
    $lcx = [single]($bx + $bw * 0.33); $rcx = [single]($bx + $bw * 0.67)
    if ($S -ge 28) {                                           # exposed tape band behind the reels
        $tw = New-Object System.Drawing.SolidBrush((C 255 16 18 23))
        FillRR $g $tw ($lcx) ($cy - $R * 0.80) ($rcx - $lcx) ($R * 1.60) ([single]($R * 0.5)); $tw.Dispose()
    }
    $detail = $S -ge 48
    DrawReel $g $lcx $cy $R $tapeOuter $tapeInner $teal $hole $detail
    DrawReel $g $rcx $cy $R $tapeOuter $tapeInner $teal $hole $detail

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

# Keep the cross-platform PNG assets (Avalonia app + Linux AppImage) in sync with the same 256px mark.
$avalonia = Join-Path $dir "Mixtape.App\Assets\icon.png"
$linux = Join-Path $dir "packaging\linux\Mixtape.png"
if (Test-Path (Split-Path -Parent $avalonia)) { [System.IO.File]::WriteAllBytes($avalonia, $pngs[256]) }
if (Test-Path (Split-Path -Parent $linux)) { [System.IO.File]::WriteAllBytes($linux, $pngs[256]) }

Write-Output ("app.ico written: " + (Get-Item (Join-Path $dir "app.ico")).Length + " bytes; PNGs synced")
