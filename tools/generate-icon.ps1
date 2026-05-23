# Generates PhotoLibrarian app icon: a framed photo (sun + mountain) resting on a shelf of books.
# Outputs:
#   - PNG variants at 16/24/32/48/64/128/256 in Assets/icons/
#   - Combined PhotoLibrarian.ico in Assets/

Add-Type -AssemblyName System.Drawing

$ProjectRoot = "D:\Code\GitHub\PhotoLibrarian"
$AssetsDir   = Join-Path $ProjectRoot "src\PhotoLibrarian\Assets"
$IconsDir    = Join-Path $AssetsDir "icons"
New-Item -Path $IconsDir -ItemType Directory -Force | Out-Null

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Scale factor (design uses a 256-unit grid)
    $s = $Size / 256.0

    # ===========================================================
    #  Shelf / books — bottom 28% of canvas
    # ===========================================================
    $shelfLeft   = [int](18 * $s)
    $shelfRight  = $Size - [int](18 * $s)
    $shelfHeight = [int](58 * $s)

    # Shelf board (wood)
    $shelfBoardY = $Size - [int](24 * $s)
    $shelfBoardH = [Math]::Max(2, [int](10 * $s))
    $woodBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 130, 89, 56))
    $g.FillRectangle($woodBrush, $shelfLeft - [int](4*$s), $shelfBoardY, ($shelfRight - $shelfLeft) + [int](8*$s), $shelfBoardH)
    $woodBrush.Dispose()

    # Books — three colored spines of varying heights
    $bookDefs = @(
        @{ Color = [System.Drawing.Color]::FromArgb(255, 198,  61,  61); Width = 0.28; Height = 0.95 },  # red
        @{ Color = [System.Drawing.Color]::FromArgb(255,  60, 114, 180); Width = 0.32; Height = 1.00 },  # blue
        @{ Color = [System.Drawing.Color]::FromArgb(255, 214, 158,  46); Width = 0.30; Height = 0.88 }   # gold
    )
    $bookAreaW = $shelfRight - $shelfLeft
    $x = $shelfLeft
    foreach ($b in $bookDefs) {
        $bw = [int]($bookAreaW * $b.Width)
        $bh = [int]($shelfHeight * $b.Height)
        $by = $shelfBoardY - $bh
        $brush = New-Object System.Drawing.SolidBrush $b.Color
        $g.FillRectangle($brush, $x, $by, $bw, $bh)
        $brush.Dispose()

        # Subtle highlight band near top of each spine
        $highlightBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(50, 255, 255, 255))
        $g.FillRectangle($highlightBrush, $x, $by + [int](4*$s), $bw, [Math]::Max(1,[int](3*$s)))
        $highlightBrush.Dispose()

        $x += $bw + [Math]::Max(1, [int](2*$s))
    }

    # ===========================================================
    #  Framed photo — top portion, slightly overlapping books
    # ===========================================================
    $frameLeft   = [int](36 * $s)
    $frameRight  = $Size - [int](36 * $s)
    $frameTop    = [int](22 * $s)
    $frameBottom = [int](172 * $s)
    $frameW      = $frameRight - $frameLeft
    $frameH      = $frameBottom - $frameTop
    $frameThick  = [Math]::Max(2, [int](10 * $s))

    # Drop shadow under frame
    $shadowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 0, 0, 0))
    $g.FillRectangle($shadowBrush, $frameLeft + [int](6*$s), $frameTop + [int](8*$s), $frameW, $frameH)
    $shadowBrush.Dispose()

    # Outer frame (dark gray, near-black)
    $frameBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 44, 48, 58))
    $g.FillRectangle($frameBrush, $frameLeft, $frameTop, $frameW, $frameH)
    $frameBrush.Dispose()

    # Photo area (sky background with mountain + sun)
    $photoLeft = $frameLeft + $frameThick
    $photoTop  = $frameTop + $frameThick
    $photoW    = $frameW - 2*$frameThick
    $photoH    = $frameH - 2*$frameThick

    # Sky gradient (top: deeper blue, bottom: pale blue)
    $skyRect   = New-Object System.Drawing.Rectangle $photoLeft, $photoTop, $photoW, $photoH
    $skyTop    = [System.Drawing.Color]::FromArgb(255,  70, 130, 200)
    $skyBottom = [System.Drawing.Color]::FromArgb(255, 175, 215, 240)
    $skyBrush  = New-Object System.Drawing.Drawing2D.LinearGradientBrush $skyRect, $skyTop, $skyBottom, ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillRectangle($skyBrush, $skyRect)
    $skyBrush.Dispose()

    # Sun (upper right)
    $sunRadius = [int]($photoW * 0.12)
    $sunCX = $photoLeft + [int]($photoW * 0.72)
    $sunCY = $photoTop  + [int]($photoH * 0.30)
    $sunBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 210, 80))
    $g.FillEllipse($sunBrush, $sunCX - $sunRadius, $sunCY - $sunRadius, 2*$sunRadius, 2*$sunRadius)
    $sunBrush.Dispose()

    # Mountain — single triangle peak (filling most of photo)
    [System.Drawing.Point[]]$mtnPts = @(
        [System.Drawing.Point]::new($photoLeft, $photoTop + $photoH),
        [System.Drawing.Point]::new($photoLeft + [int]($photoW * 0.50), $photoTop + [int]($photoH * 0.25)),
        [System.Drawing.Point]::new($photoLeft + $photoW, $photoTop + $photoH)
    )
    $mtnBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 80, 102, 95))
    $g.FillPolygon($mtnBrush, $mtnPts)
    $mtnBrush.Dispose()

    # Snow cap on mountain
    [System.Drawing.Point[]]$snowPts = @(
        [System.Drawing.Point]::new($photoLeft + [int]($photoW * 0.39), $photoTop + [int]($photoH * 0.42)),
        [System.Drawing.Point]::new($photoLeft + [int]($photoW * 0.50), $photoTop + [int]($photoH * 0.25)),
        [System.Drawing.Point]::new($photoLeft + [int]($photoW * 0.61), $photoTop + [int]($photoH * 0.42)),
        [System.Drawing.Point]::new($photoLeft + [int]($photoW * 0.56), $photoTop + [int]($photoH * 0.45)),
        [System.Drawing.Point]::new($photoLeft + [int]($photoW * 0.50), $photoTop + [int]($photoH * 0.38)),
        [System.Drawing.Point]::new($photoLeft + [int]($photoW * 0.44), $photoTop + [int]($photoH * 0.45))
    )
    $snowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 248, 250))
    $g.FillPolygon($snowBrush, $snowPts)
    $snowBrush.Dispose()

    # Second smaller foreground hill
    if ($Size -ge 32) {
        [System.Drawing.Point[]]$hillPts = @(
            [System.Drawing.Point]::new($photoLeft, $photoTop + $photoH),
            [System.Drawing.Point]::new($photoLeft + [int]($photoW * 0.25), $photoTop + [int]($photoH * 0.55)),
            [System.Drawing.Point]::new($photoLeft + [int]($photoW * 0.50), $photoTop + $photoH)
        )
        $hillBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 56, 78, 70))
        $g.FillPolygon($hillBrush, $hillPts)
        $hillBrush.Dispose()
    }

    $g.Dispose()
    return $bmp
}

# Generate PNGs
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngPaths = @{}
foreach ($size in $sizes) {
    $bmp = New-IconBitmap -Size $size
    $path = Join-Path $IconsDir "icon-$size.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngPaths[$size] = $path
    $bmp.Dispose()
    Write-Host "  wrote $size px -> $path"
}

# Build multi-resolution ICO from the PNGs (Vista+ format embeds PNGs directly)
function Save-MultiIco {
    param([hashtable]$Pngs, [string]$OutPath)

    $sortedSizes = $Pngs.Keys | Sort-Object
    $count = $sortedSizes.Count
    $pngBytes = @{}
    foreach ($sz in $sortedSizes) {
        $pngBytes[$sz] = [System.IO.File]::ReadAllBytes($Pngs[$sz])
    }

    $fs = [System.IO.File]::Open($OutPath, [System.IO.FileMode]::Create)
    $bw = New-Object System.IO.BinaryWriter $fs
    try {
        # ICONDIR
        $bw.Write([UInt16]0)       # reserved
        $bw.Write([UInt16]1)       # type = ICO
        $bw.Write([UInt16]$count)  # count

        # Each ICONDIRENTRY is 16 bytes; image data follows after all entries
        $dataOffset = 6 + (16 * $count)
        foreach ($sz in $sortedSizes) {
            $b = $pngBytes[$sz]
            $width  = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
            $height = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
            $bw.Write([byte]$width)
            $bw.Write([byte]$height)
            $bw.Write([byte]0)      # color count (0 = no palette)
            $bw.Write([byte]0)      # reserved
            $bw.Write([UInt16]1)    # color planes
            $bw.Write([UInt16]32)   # bits per pixel
            $bw.Write([UInt32]$b.Length)
            $bw.Write([UInt32]$dataOffset)
            $dataOffset += $b.Length
        }

        foreach ($sz in $sortedSizes) {
            $bw.Write($pngBytes[$sz])
        }
    } finally {
        $bw.Flush()
        $bw.Dispose()
        $fs.Dispose()
    }
}

$icoPath = Join-Path $AssetsDir "PhotoLibrarian.ico"
Save-MultiIco -Pngs $pngPaths -OutPath $icoPath
Write-Host ""
Write-Host "ICO written: $icoPath ($((Get-Item $icoPath).Length) bytes)"
