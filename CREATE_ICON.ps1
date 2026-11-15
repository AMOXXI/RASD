# RASD Icon Generator
# Generates Assets\rasd.ico using System.Drawing
# Run from the RASD\ folder

Add-Type -AssemblyName System.Drawing

$AssetsDir = Join-Path $PSScriptRoot "Assets"
if (-not (Test-Path $AssetsDir)) { New-Item -ItemType Directory -Path $AssetsDir -Force | Out-Null }
$OutputPath = Join-Path $AssetsDir "rasd.ico"

function New-RasdBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- Background circle (dark navy) ---
    $navy = [System.Drawing.Color]::FromArgb(255, 10, 15, 40)
    $bgBrush = New-Object System.Drawing.SolidBrush($navy)
    $g.FillEllipse($bgBrush, 0, 0, $Size - 1, $Size - 1)

    # --- Outer ring (deep blue) ---
    $rimW  = [float]([Math]::Max(1.5, $Size / 14.0))
    $rim   = [int]($Size * 0.06)
    $pen1  = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 30, 58, 138), $rimW)
    $g.DrawEllipse($pen1, $rim, $rim, $Size - $rim * 2 - 1, $Size - $rim * 2 - 1)

    # --- Orange accent arc (top-right quarter) ---
    $arcW  = [float]([Math]::Max(2.0, $Size / 9.0))
    $pen2  = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 249, 115, 22), $arcW)
    $g.DrawArc($pen2, $rim, $rim, $Size - $rim * 2 - 1, $Size - $rim * 2 - 1, -80, 110)

    # --- Teal inner arc ---
    $arc2W = [float]([Math]::Max(1.0, $Size / 18.0))
    $rim2  = [int]($Size * 0.22)
    $pen3  = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 6, 182, 212), $arc2W)
    $g.DrawArc($pen3, $rim2, $rim2, $Size - $rim2 * 2 - 1, $Size - $rim2 * 2 - 1, 95, 85)

    # --- "R" letter centred ---
    $fontSize = [float]([Math]::Max(6.0, $Size * 0.44))
    $font     = New-Object System.Drawing.Font("Arial", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $white    = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 248, 250, 252))
    $sf       = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, 0, [float]$Size, [float]$Size)
    $g.DrawString("R", $font, $white, $rect, $sf)

    $g.Dispose(); $bgBrush.Dispose(); $pen1.Dispose(); $pen2.Dispose()
    $pen3.Dispose(); $font.Dispose(); $white.Dispose(); $sf.Dispose()

    return $bmp
}

# Generate sizes: 16, 32, 48, 256
$sizes = @(16, 32, 48, 256)
$pngChunks = @()

foreach ($sz in $sizes) {
    $bmp = New-RasdBitmap -Size $sz
    $ms  = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngChunks += , $ms.ToArray()
    $ms.Dispose(); $bmp.Dispose()
}

# Write ICO file (ICONDIR + ICONDIRENTRY[] + PNG data)
$count      = $pngChunks.Count
$dataOffset = 6 + $count * 16   # header=6, each entry=16

$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter($out)

# ICONDIR header
$bw.Write([uint16]0)           # reserved
$bw.Write([uint16]1)           # type: ICO
$bw.Write([uint16]$count)      # image count

# ICONDIRENTRY array
$offset = $dataOffset
for ($i = 0; $i -lt $count; $i++) {
    $sz  = $sizes[$i]
    $w   = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
    $h   = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
    $bw.Write($w)
    $bw.Write($h)
    $bw.Write([byte]0)          # color count
    $bw.Write([byte]0)          # reserved
    $bw.Write([uint16]1)        # planes
    $bw.Write([uint16]32)       # bit count
    $bw.Write([uint32]$pngChunks[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngChunks[$i].Length
}

# PNG image data
foreach ($chunk in $pngChunks) { $bw.Write($chunk) }
$bw.Flush()

[System.IO.File]::WriteAllBytes($OutputPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()

$kb = [Math]::Round((Get-Item $OutputPath).Length / 1KB, 1)
Write-Host "rasd.ico created: $OutputPath ($kb KB, $count sizes: $($sizes -join ','))" -ForegroundColor Green
