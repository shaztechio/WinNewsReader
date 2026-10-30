# Draws the app icon and writes src/App/Assets/AppIcon.ico (16-256 px) and AppIcon.png (256 px), and the Microsoft Store images in packaging/windows.
# Run from the repository root with PowerShell 7 on Windows: ./tools/make-icon.ps1
# Shapes are laid out on a 256 x 256 grid and scaled to each size. Sizes of 24 px and under drop
# the page and draw a larger feed symbol, so they stay legible.

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$assets = Join-Path $PSScriptRoot "..\src\App\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
	$path = New-Object System.Drawing.Drawing2D.GraphicsPath
	$d = 2 * $r
	$path.AddArc($x, $y, $d, $d, 180, 90)
	$path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
	$path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
	$path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
	$path.CloseFigure()
	$path
}

# The feed symbol: a dot and two quarter arcs opening up and to the right from (cx, cy).
function FeedSymbol($g, [System.Drawing.Color]$color, [float]$cx, [float]$cy, [float]$dot, [float]$r1, [float]$r2, [float]$stroke) {
	$brush = New-Object System.Drawing.SolidBrush $color
	$g.FillEllipse($brush, $cx - $dot, $cy - $dot, 2 * $dot, 2 * $dot)
	$pen = New-Object System.Drawing.Pen $color, $stroke
	$pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
	foreach ($r in $r1, $r2) {
		$g.DrawArc($pen, $cx - $r, $cy - $r, 2 * $r, 2 * $r, 270, 90)
	}
}

function Draw([int]$size) {
	$bitmap = New-Object System.Drawing.Bitmap $size, $size
	$g = [System.Drawing.Graphics]::FromImage($bitmap)
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$g.ScaleTransform($size / 256, $size / 256)

	# Background: a rounded square with a blue gradient.
	$inset = if ($size -le 24) { 4 } else { 8 }
	$background = RoundedRect $inset $inset (256 - 2 * $inset) (256 - 2 * $inset) 56
	$gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, 256)), (Color "#3D8BFF"), (Color "#1D4ED8")
	$g.FillPath($gradient, $background)

	if ($size -le 24) {
		FeedSymbol $g ([System.Drawing.Color]::White) 76 180 24 72 132 30
	}
	else {
		# A page of news: a white sheet with a headline and lines of text.
		$g.FillPath([System.Drawing.Brushes]::White, (RoundedRect 52 46 130 160 16))
		$line = New-Object System.Drawing.SolidBrush (Color "#B9CEF3")
		$headline = New-Object System.Drawing.SolidBrush (Color "#1D4ED8")
		$g.FillPath($headline, (RoundedRect 72 70 90 16 8))
		foreach ($row in @(@(102, 90), @(124, 76), @(146, 54))) {
			$g.FillPath($line, (RoundedRect 72 $row[0] $row[1] 11 5.5))
		}
		# The feed badge: an orange disc with a ring of background blue around it, over the page corner.
		$g.FillEllipse((New-Object System.Drawing.SolidBrush (Color "#1F55DF")), 120, 120, 112, 112)
		$g.FillEllipse((New-Object System.Drawing.SolidBrush (Color "#FF8A1F")), 130, 130, 92, 92)
		FeedSymbol $g ([System.Drawing.Color]::White) 157 195 9 26 45 11
	}

	$g.Dispose()
	$bitmap
}

# An .ico file holding one PNG per size (supported since Windows Vista).
$images = foreach ($size in $sizes) {
	$bitmap = Draw $size
	$stream = New-Object System.IO.MemoryStream
	$bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
	if ($size -eq 256) { $bitmap.Save((Join-Path $assets "AppIcon.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
	$bitmap.Dispose()
	[pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
}

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
	$dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
	$writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
	$writer.Write([byte]0); $writer.Write([byte]0)
	$writer.Write([uint16]1); $writer.Write([uint16]32)
	$writer.Write([uint32]$image.Bytes.Length); $writer.Write([uint32]$offset)
	$offset += $image.Bytes.Length
}
foreach ($image in $images) { $writer.Write($image.Bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets "AppIcon.ico"), $out.ToArray())
"Wrote AppIcon.ico ($($sizes -join ', ') px) and AppIcon.png"

# The Microsoft Store package's images (packaging/windows/Assets), each drawn at its own size rather than scaled up from 256 px.
# The manifest names Square44x44Logo, Square150x150Logo and StoreLogo; MakePri picks the scale or target-size variant.
$store = Join-Path $PSScriptRoot "..\packaging\windows\Assets"
New-Item -ItemType Directory -Force $store | Out-Null
function SavePng([int]$size, [string]$name) {
	$bitmap = Draw $size
	$bitmap.Save((Join-Path $store $name), [System.Drawing.Imaging.ImageFormat]::Png)
	$bitmap.Dispose()
}
foreach ($scale in 100, 125, 150, 200, 400) {
	SavePng ([int][math]::Round(44 * $scale / 100)) "Square44x44Logo.scale-$scale.png"
	SavePng ([int][math]::Round(150 * $scale / 100)) "Square150x150Logo.scale-$scale.png"
	SavePng ([int][math]::Round(50 * $scale / 100)) "StoreLogo.scale-$scale.png"
}
foreach ($size in 16, 24, 32, 48, 256) {
	foreach ($form in "", "_altform-unplated", "_altform-lightunplated") { SavePng $size "Square44x44Logo.targetsize-$size$form.png" }
}
$listing = Join-Path $PSScriptRoot "..\packaging\windows\StoreListing"
New-Item -ItemType Directory -Force $listing | Out-Null
$bitmap = Draw 300
$bitmap.Save((Join-Path $listing "StoreLogo-300.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
"Wrote the Store images to packaging/windows/Assets and StoreLogo-300.png to packaging/windows/StoreListing"
