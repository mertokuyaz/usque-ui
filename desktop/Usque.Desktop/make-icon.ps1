param(
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-UsqueIcon([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $path = $null; $gradient = $null; $pen = $null; $u = $null
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $s = [single]$Size
        $r = $s * 0.22
        $d = 2 * $r
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($s - $d, 0, $d, $d, 270, 90)
        $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
        $path.AddArc(0, $s - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.RectangleF]::new(0, 0, $s, $s),
            [System.Drawing.Color]::FromArgb(255, 118, 229, 199),
            [System.Drawing.Color]::FromArgb(255, 63, 173, 143),
            [single]90)
        $graphics.FillPath($gradient, $path)

        # Brand-styled "U": two rounded vertical strokes joined by a bottom arc.
        $arm = $s * 0.20
        $wide = $s * 0.17
        $top = $s * 0.20
        $bottom = $s * 0.58
        $cx = $s / 2
        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 14, 48, 39), $wide)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $u = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $u.AddLine($cx - $arm, $top, $cx - $arm, $bottom)
        $u.AddArc([System.Drawing.RectangleF]::new($cx - $arm, $bottom - $arm, 2 * $arm, 2 * $arm), [single]180, [single]-180)
        $u.AddLine($cx + $arm, $bottom, $cx + $arm, $top)
        $graphics.DrawPath($pen, $u)
    } finally {
        if ($u) { $u.Dispose() }
        if ($pen) { $pen.Dispose() }
        if ($gradient) { $gradient.Dispose() }
        if ($path) { $path.Dispose() }
        $graphics.Dispose()
    }
    return $bitmap
}

function Convert-ToPngBytes([System.Drawing.Bitmap]$bitmap) {
    $memory = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        return $memory.ToArray()
    } finally { $memory.Dispose() }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) {
    $bitmap = New-UsqueIcon $size
    $frames.Add((Convert-ToPngBytes $bitmap))
    $bitmap.Dispose()
}

$icoPath = Join-Path $OutputDirectory 'UsqueDesktop.ico'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $size = $sizes[$i]
        $dimension = if ($size -ge 256) { 0 } else { $size }
        $png = $frames[$i]
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$png.Length); $writer.Write([uint32]$offset)
        $offset += $png.Length
    }
    foreach ($png in $frames) { $writer.Write($png) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($icoPath, $stream.ToArray())
} finally {
    $writer.Dispose(); $stream.Dispose()
}

$preview = New-UsqueIcon 256
try { $preview.Save((Join-Path $OutputDirectory 'UsqueDesktop.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
finally { $preview.Dispose() }

Write-Host "Created $icoPath"
Write-Host "Created $(Join-Path $OutputDirectory 'UsqueDesktop.png')"
