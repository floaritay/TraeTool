# Generate TraeTool app icon (brand mark: white T on rounded blue tile).
# Output: TraeTool\app.ico (PNG frames) + preview_256.png
# ASCII-only (CN-locale PS constraint).
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $g.Clear([System.Drawing.Color]::Transparent)

  $pad = [int]($size * 0.04)
  $w = $size - $pad * 2
  $radius = [int]($w * 0.23)
  $rect = New-Object System.Drawing.Rectangle $pad, $pad, $w, $w

  # rounded tile (diagonal blue gradient)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $radius * 2
  $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
  $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
  $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
  $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    $rect,
    [System.Drawing.Color]::FromArgb(255, 63, 111, 252),
    [System.Drawing.Color]::FromArgb(255, 26, 62, 186),
    [float]50)
  $g.FillPath($grad, $path)
  $grad.Dispose()
  $path.Dispose()

  # letter T (Bahnschrift like in-app, fallback chain)
  $fontSize = [float]($size * 0.60)
  $font = $null
  foreach ($name in @("Bahnschrift", "Segoe UI", "Arial")) {
    try {
      $font = [System.Drawing.Font]::new($name, $fontSize,
        [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
      break
    } catch { }
  }
  if (-not $font) { throw "no font available" }
  $sf = [System.Drawing.StringFormat]::new()
  $sf.Alignment = [System.Drawing.StringAlignment]::Center
  $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
  $layout = New-Object System.Drawing.RectangleF 0, ([float]($size * 0.02)), ([float]$size), ([float]$size)
  $g.DrawString("T", $font, [System.Drawing.Brushes]::White, $layout, $sf)
  $font.Dispose()
  $sf.Dispose()
  $g.Dispose()
  return $bmp
}

$dir = Join-Path (Split-Path -Parent $PSScriptRoot) 'TraeTool'
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Frame $s
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,@($s, $ms.ToArray())
  if ($s -eq 256) { [System.IO.File]::WriteAllBytes("$dir\preview_256.png", $ms.ToArray()) }
  $bmp.Dispose()
  $ms.Dispose()
}

# pack ICO: ICONDIR + ICONDIRENTRY[] + PNG blobs
$fs = [System.IO.File]::Create("$dir\app.ico")
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0)           # reserved
$bw.Write([uint16]1)           # type icon
$bw.Write([uint16]$pngs.Count) # count
$offset = 6 + 16 * $pngs.Count
foreach ($entry in $pngs) {
  $s = $entry[0]; $bytes = $entry[1]
  $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width (0=256)
  $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height
  $bw.Write([byte]0)           # colors
  $bw.Write([byte]0)           # reserved
  $bw.Write([uint16]1)         # planes
  $bw.Write([uint16]32)        # bpp
  $bw.Write([uint32]$bytes.Length)
  $bw.Write([uint32]$offset)
  $offset += $bytes.Length
}
foreach ($entry in $pngs) { $bw.Write($entry[1]) }
$bw.Flush()
$fs.Close()
Write-Output "wrote $dir\app.ico ($($pngs.Count) frames), preview_256.png"
