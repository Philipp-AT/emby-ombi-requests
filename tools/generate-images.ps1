# Renders the images the plugin shows to users (src/OmbiRequests/Images):
#   banner-<status>-<lang>.png  status banners laid over the channel posters
#   channel-<lang>.png          picture of the "Requests" channel in "My media"
#   folder-<list>-<lang>.png    pictures of the folders in the channel
#   letter-<A-Z|other>.png      pictures of the A-Z folders of "All movies" / "All series"
# Text is rendered here, at build time, because Emby's Docker images often have no fonts for drawing
# text at runtime. Requires Windows (System.Drawing) and, on the first run, internet access for the
# icon font. Run: powershell -File tools\generate-images.ps1

Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\src\OmbiRequests\Images'
New-Item -ItemType Directory -Force $outDir | Out-Null

# Icons: Material Symbols Rounded by Google (Apache License 2.0, https://fonts.google.com/icons), filled.
# Google Fonts serves it as a static TrueType instance, because System.Drawing cannot set the axes of the
# variable font; it is kept in the temp folder.
$iconFile = Join-Path $env:TEMP 'material-symbols-rounded-filled.ttf'
if (-not (Test-Path $iconFile)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $css = Invoke-WebRequest -UseBasicParsing -UserAgent 'Mozilla/4.0' 'https://fonts.googleapis.com/css2?family=Material+Symbols+Rounded:opsz,wght,FILL,GRAD@48,500,1,0'
    $fontUrl = [regex]::Match($css.Content, 'url\((https://[^)]+\.ttf)\)').Groups[1].Value
    Invoke-WebRequest -UseBasicParsing $fontUrl -OutFile $iconFile
}
$iconFonts = New-Object System.Drawing.Text.PrivateFontCollection
$iconFonts.AddFontFile($iconFile)
$iconFamily = $iconFonts.Families[0]

# Draws an icon centered on its visible outline: centering the text line put the icons off center,
# because the font's line box is not centered on its glyphs.
function Add-Icon([System.Drawing.Graphics]$g, [string]$icon, [float]$size, [float]$centerX, [float]$centerY, [System.Drawing.Brush]$brush) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddString($icon, $iconFamily, [int][System.Drawing.FontStyle]::Regular, $size, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $bounds = $path.GetBounds()
    $matrix = New-Object System.Drawing.Drawing2D.Matrix
    $matrix.Translate($centerX - $bounds.X - $bounds.Width / 2, $centerY - $bounds.Y - $bounds.Height / 2)
    $path.Transform($matrix)
    $g.FillPath($brush, $path)
    $matrix.Dispose()
    $path.Dispose()
}

# The banner covers the full poster width; 680x200 is about a fifth of a 2:3 poster. It is laid
# out in these units but rendered at twice the resolution ($scale), so the text stays sharp on
# large posters and high-density screens.
$width = 680
$height = 200
$stripe = 8
$scale = 2

# Icons: favorite, hourglass_top, check, close, play_arrow.
$heart = [string][char]0xE87E
$hourglass = [string][char]0xEA5B
$check = [string][char]0xE668
$cross = [string][char]0xE5CD
$play = [string][char]0xE037

# Short texts: the posters are small in most apps, so the lines have to stay large.
$statuses = @(
    @{ Name = 'None'; Color = '#E91E63'; Icon = $heart
       de = @('Noch nicht in Emby', 'Favorit setzen = anfragen')
       en = @('Not in Emby yet', 'Favorite it to request') },
    @{ Name = 'PartiallyAvailable'; Color = '#00897B'; Icon = $heart
       de = @('Teilweise in Emby', 'Favorit = Rest anfragen')
       en = @('Partially in Emby', 'Favorite it for the rest') },
    @{ Name = 'Requested'; Color = '#F9A825'; Icon = $hourglass
       de = @('Angefragt', 'Wartet auf Freigabe')
       en = @('Requested', 'Waiting for approval') },
    @{ Name = 'Approved'; Color = '#1E88E5'; Icon = $check
       de = @('Genehmigt', 'Kommt bald')
       en = @('Approved', 'Coming soon') },
    @{ Name = 'Denied'; Color = '#C62828'; Icon = $cross
       de = @('Abgelehnt', 'Anfrage abgelehnt')
       en = @('Denied', 'Request denied') },
    @{ Name = 'Available'; Color = '#43A047'; Icon = $play
       de = @('In Emby verfügbar', 'In deiner Bibliothek')
       en = @('Available in Emby', 'In your library') }
)

# Tight text boxes: the default StringFormat adds padding around the text, which pushed the
# lines off center in the first version.
$typographic = [System.Drawing.StringFormat]::GenericTypographic

function Get-FittingFont([System.Drawing.Graphics]$g, [string]$text, [string]$family, [float]$size, [System.Drawing.FontStyle]$style, [float]$maxWidth) {
    while ($size -gt 12) {
        $font = New-Object System.Drawing.Font $family, $size, $style, ([System.Drawing.GraphicsUnit]::Pixel)
        if ($g.MeasureString($text, $font, [int]::MaxValue, $typographic).Width -le $maxWidth) { return $font }
        $font.Dispose()
        $size -= 1
    }
    return New-Object System.Drawing.Font $family, $size, $style, ([System.Drawing.GraphicsUnit]::Pixel)
}

foreach ($status in $statuses) {
    foreach ($lang in 'de', 'en') {
        $bmp = New-Object System.Drawing.Bitmap ($width * $scale), ($height * $scale), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.TextRenderingHint = 'AntiAliasGridFit'
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.ScaleTransform($scale, $scale)

        $accent = [System.Drawing.ColorTranslator]::FromHtml($status.Color)

        # Nearly opaque dark band with a colored top edge, so the text reads on bright posters too.
        $band = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(238, 14, 14, 18))
        $g.FillRectangle($band, 0, 0, $width, $height)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $accent), 0, 0, $width, $stripe)

        # Fonts sized to the space left of the icon; at least 7% margin left and right.
        $circle = 116
        $gap = 26
        $margin = 48
        $maxText = $width - 2 * $margin - $circle - $gap
        $lines = $status[$lang]
        $titleFont = Get-FittingFont $g $lines[0] 'Segoe UI' 54 ([System.Drawing.FontStyle]::Bold) $maxText
        $subFont = Get-FittingFont $g $lines[1] 'Segoe UI Semibold' 38 ([System.Drawing.FontStyle]::Regular) $maxText
        $titleSize = $g.MeasureString($lines[0], $titleFont, [int]::MaxValue, $typographic)
        $subSize = $g.MeasureString($lines[1], $subFont, [int]::MaxValue, $typographic)

        # Icon and text block as one group, centered horizontally and vertically below the stripe.
        $lineGap = 6
        $textWidth = [Math]::Max($titleSize.Width, $subSize.Width)
        $textHeight = $titleSize.Height + $lineGap + $subSize.Height
        $groupWidth = $circle + $gap + $textWidth
        $left = [Math]::Max($margin, ($width - $groupWidth) / 2)
        $middle = $stripe + ($height - $stripe) / 2

        $circleTop = $middle - $circle / 2
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $accent), [float]$left, [float]$circleTop, [float]$circle, [float]$circle)
        Add-Icon $g $status.Icon 72 ([float]($left + $circle / 2)) ([float]$middle) ([System.Drawing.Brushes]::White)

        $textLeft = [float]($left + $circle + $gap)
        $textTop = [float]($middle - $textHeight / 2)
        $subBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 255, 255, 255))
        $g.DrawString($lines[0], $titleFont, [System.Drawing.Brushes]::White, $textLeft, $textTop, $typographic)
        $g.DrawString($lines[1], $subFont, $subBrush, $textLeft, [float]($textTop + $titleSize.Height + $lineGap), $typographic)

        $file = Join-Path $outDir ("banner-{0}-{1}.png" -f $status.Name, $lang)
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
        Write-Output $file
    }
}

# Channel picture (16:9) - deliberately without the name "Ombi", users only see "Requests".
$channels = @(
    @{ Lang = 'de'; Title = 'Anfragen'; Subtitle = 'Filme & Serien, die noch fehlen' },
    @{ Lang = 'en'; Title = 'Requests'; Subtitle = 'Movies & series not in Emby yet' }
)

foreach ($channel in $channels) {
    $w = 960
    $h = 540
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'

    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 28, 32, 44)), ([System.Drawing.Color]::FromArgb(255, 82, 181, 75)), 35
    $g.FillRectangle($gradient, $rect)

    # Plus in a circle, like the menu icon.
    $circle = 210
    $cx = 90
    $cy = [int](($h - $circle) / 2)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))), $cx, $cy, $circle, $circle)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 36, 40, 54)), 24
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $mx = $cx + $circle / 2
    $my = $cy + $circle / 2
    $g.DrawLine($pen, $mx, $my - 55, $mx, $my + 55)
    $g.DrawLine($pen, $mx - 55, $my, $mx + 55, $my)

    $textX = $cx + $circle + 50
    $maxText = $w - $textX - 40
    $titleFont = Get-FittingFont $g $channel.Title 'Segoe UI Semibold' 104 ([System.Drawing.FontStyle]::Regular) $maxText
    $subFont = Get-FittingFont $g $channel.Subtitle 'Segoe UI' 38 ([System.Drawing.FontStyle]::Regular) $maxText
    $g.DrawString($channel.Title, $titleFont, [System.Drawing.Brushes]::White, $textX, 170)
    $g.DrawString($channel.Subtitle, $subFont, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 255, 255))), $textX + 6, 312)

    $file = Join-Path $outDir ("channel-{0}.png" -f $channel.Lang)
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Output $file
}

# Pictures of the channel folders, 2:3 like the posters next to them. Without them Emby shows the
# poster of a movie or series from inside the folder. Icons: local_fire_department, theaters, event,
# star, celebration, movie, trending_up, schedule, tv.
$fw = 400
$fh = 600
$folders = @(
    @{ Key = 'popular-movies'; Accent = '#43A047'; Icon = [char]0xEF55; de = 'Beliebte Filme'; en = 'Popular movies' },
    @{ Key = 'nowplaying-movies'; Accent = '#43A047'; Icon = [char]0xE8DA; de = 'Aktuell im Kino'; en = 'Now in cinemas' },
    @{ Key = 'upcoming-movies'; Accent = '#43A047'; Icon = [char]0xE878; de = 'Demnächst erscheinende Filme'; en = 'Upcoming movies' },
    @{ Key = 'toprated-movies'; Accent = '#43A047'; Icon = [char]0xF09A; de = 'Bestbewertete Filme'; en = 'Top rated movies' },
    @{ Key = 'seasonal-movies'; Accent = '#43A047'; Icon = [char]0xEA65; de = 'Saisonale Filme'; en = 'Seasonal movies' },
    @{ Key = 'all-movies'; Accent = '#43A047'; Icon = [char]0xE404; de = 'Alle Filme'; en = 'All movies' },
    @{ Key = 'trending-tv'; Accent = '#1E88E5'; Icon = [char]0xE8E5; de = 'Angesagte Serien'; en = 'Trending series' },
    @{ Key = 'popular-tv'; Accent = '#1E88E5'; Icon = [char]0xEF55; de = 'Beliebte Serien'; en = 'Popular series' },
    @{ Key = 'anticipated-tv'; Accent = '#1E88E5'; Icon = [char]0xEFD6; de = 'Erwartete Serien'; en = 'Anticipated series' },
    @{ Key = 'toprated-tv'; Accent = '#1E88E5'; Icon = [char]0xF09A; de = 'Bestbewertete Serien'; en = 'Top rated series' },
    @{ Key = 'all-tv'; Accent = '#1E88E5'; Icon = [char]0xE63B; de = 'Alle Serien'; en = 'All series' }
)

function New-FolderCanvas([System.Drawing.Color]$accent) {
    $bmp = New-Object System.Drawing.Bitmap $fw, $fh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $rect = New-Object System.Drawing.Rectangle 0, 0, $fw, $fh
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 28, 32, 44)), $accent, 75
    $g.FillRectangle($gradient, $rect)
    return @($bmp, $g)
}

$centered = New-Object System.Drawing.StringFormat
$centered.Alignment = 'Center'
$centered.LineAlignment = 'Center'

foreach ($folder in $folders) {
    foreach ($lang in 'de', 'en') {
        $accent = [System.Drawing.ColorTranslator]::FromHtml($folder.Accent)
        $bmp, $g = New-FolderCanvas $accent

        # Icon in a white circle, like the channel picture.
        $circle = 150
        $cx = ($fw - $circle) / 2
        $cy = 120
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))), [float]$cx, [float]$cy, [float]$circle, [float]$circle)
        Add-Icon $g ([string]$folder.Icon) 100 ([float]($cx + $circle / 2)) ([float]($cy + $circle / 2)) (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 36, 40, 54)))

        # Title, wrapped at spaces and as large as fits; a long word ("Bestbewertete") must fit on one line.
        $text = $folder[$lang]
        $box = New-Object System.Drawing.RectangleF 30, 320, ($fw - 60), 220
        $size = 52
        do {
            $font = New-Object System.Drawing.Font 'Segoe UI Semibold', $size, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
            $measured = $g.MeasureString($text, $font, [int]$box.Width)
            $widestWord = ($text -split ' ' | ForEach-Object { $g.MeasureString($_, $font).Width } | Measure-Object -Maximum).Maximum
            $size -= 2
        } while (($measured.Height -gt $box.Height -or $measured.Width -gt $box.Width -or $widestWord -gt $box.Width) -and $size -gt 20)
        $g.DrawString($text, $font, [System.Drawing.Brushes]::White, $box, $centered)

        $file = Join-Path $outDir ("folder-{0}-{1}.png" -f $folder.Key, $lang)
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
        Write-Output $file
    }
}

# A-Z folders of "All movies" / "All series"; "#" for digits, symbols and other scripts.
$letters = @([char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZ' | ForEach-Object { [string]$_ }) + '#'
foreach ($letter in $letters) {
    $bmp, $g = New-FolderCanvas ([System.Drawing.Color]::FromArgb(255, 70, 78, 104))
    $font = New-Object System.Drawing.Font 'Segoe UI Semibold', 260, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString($letter, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $fw, $fh), $centered)
    $name = if ($letter -eq '#') { 'other' } else { $letter }
    $file = Join-Path $outDir ("letter-{0}.png" -f $name)
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Output $file
}
