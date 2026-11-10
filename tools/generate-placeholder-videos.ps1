# Renders the placeholder videos of the movies in the "Requests" channel (src/OmbiRequests/Videos):
#   placeholder-<status>-<lang>.mp4   10 s, 1920x1080, H.264 High 4.0 + silent AAC stereo
# Each video shows the status banner of the poster (Images/banner-<status>-<lang>.png, so run
# generate-images.ps1 first after changing the banners) and one line of explanation.
# Movies in the channel have no file; without a media source the Apple TV app fails on their details
# page, see src/OmbiRequests/Channel/PlaceholderVideos.cs. Length and format must match the media
# source the plugin declares there.
# Requires Windows (System.Drawing) and ffmpeg (on the PATH or -Ffmpeg <path>).
# Run: powershell -File tools\generate-placeholder-videos.ps1

param([string]$Ffmpeg = 'ffmpeg')

Add-Type -AssemblyName System.Drawing

$imageDir = Join-Path $PSScriptRoot '..\src\OmbiRequests\Images'
$outDir = Join-Path $PSScriptRoot '..\src\OmbiRequests\Videos'
New-Item -ItemType Directory -Force $outDir | Out-Null
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('ombirequests-videos-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tempDir | Out-Null

$width = 1920
$height = 1080
$seconds = 10

# Same states as the banners. The line below the banner says what the state means for this movie.
$texts = @{
    None = @{
        de = 'Dieser Film ist noch nicht in Emby. Setz das Herz (Favorit), um ihn anzufragen.'
        en = 'This movie is not in Emby yet. Mark it as a favorite (heart) to request it.' }
    PartiallyAvailable = @{
        de = 'Dieser Film ist nur teilweise in Emby. Setz das Herz, um den Rest anzufragen.'
        en = 'This movie is only partially in Emby. Mark it as a favorite to request the rest.' }
    Requested = @{
        de = 'Dieser Film ist angefragt und wartet auf die Freigabe.'
        en = 'This movie has been requested and is waiting for approval.' }
    Approved = @{
        de = 'Die Anfrage ist genehmigt. Der Film kommt bald in Emby.'
        en = 'The request has been approved. The movie will be in Emby soon.' }
    Denied = @{
        de = 'Die Anfrage für diesen Film wurde abgelehnt.'
        en = 'The request for this movie was denied.' }
    Available = @{
        de = 'Dieser Film ist schon in Emby. Du findest ihn in deiner Bibliothek.'
        en = 'This movie is already in Emby. You will find it in your library.' }
}

$typographic = [System.Drawing.StringFormat]::GenericTypographic

function Get-FittingFont([System.Drawing.Graphics]$g, [string]$text, [string]$family, [float]$size, [float]$maxWidth) {
    while ($size -gt 12) {
        $font = New-Object System.Drawing.Font $family, $size, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        if ($g.MeasureString($text, $font, [int]::MaxValue, $typographic).Width -le $maxWidth) { return $font }
        $font.Dispose()
        $size -= 1
    }
    return New-Object System.Drawing.Font $family, $size, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
}

try {
    foreach ($status in $texts.Keys) {
        foreach ($lang in 'de', 'en') {
            $bannerFile = Join-Path $imageDir ("banner-{0}-{1}.png" -f $status, $lang)
            $banner = [System.Drawing.Image]::FromFile((Resolve-Path $bannerFile))

            $bmp = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.SmoothingMode = 'AntiAlias'
            $g.InterpolationMode = 'HighQualityBicubic'
            $g.TextRenderingHint = 'AntiAliasGridFit'
            # The banner's own background, so it merges into the picture.
            $g.Clear([System.Drawing.Color]::FromArgb(255, 14, 14, 18))

            # Banner (rendered at 1360x400) centered a little above the middle, the text line below it.
            $bannerWidth = 1360
            $bannerHeight = [int]($bannerWidth * $banner.Height / $banner.Width)
            $bannerTop = [int](($height - $bannerHeight) / 2 - 70)
            $g.DrawImage($banner, [int](($width - $bannerWidth) / 2), $bannerTop, $bannerWidth, $bannerHeight)

            $text = $texts[$status][$lang]
            $font = Get-FittingFont $g $text 'Segoe UI' 46 ($width - 240)
            $size = $g.MeasureString($text, $font, [int]::MaxValue, $typographic)
            $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 205, 205, 212))
            $g.DrawString($text, $font, $brush, [float](($width - $size.Width) / 2), [float]($bannerTop + $bannerHeight + 70), $typographic)

            $frame = Join-Path $tempDir ("{0}-{1}.png" -f $status, $lang)
            $bmp.Save($frame, [System.Drawing.Imaging.ImageFormat]::Png)
            $font.Dispose(); $brush.Dispose(); $g.Dispose(); $bmp.Dispose(); $banner.Dispose()

            # A still picture: one keyframe, the other frames are almost empty (each keyframe costs
            # about 25 KB). 25 fps, because some players handle very low frame rates badly. Silent
            # audio: some players do not like videos without an audio track.
            $file = Join-Path $outDir ("placeholder-{0}-{1}.mp4" -f $status, $lang)
            & $Ffmpeg -hide_banner -loglevel error -y `
                -loop 1 -framerate 25 -i $frame `
                -f lavfi -i 'anullsrc=channel_layout=stereo:sample_rate=48000' `
                -t $seconds -map 0:v -map 1:a `
                -c:v libx264 -preset veryslow -tune stillimage -crf 20 -profile:v high -level:v 4.0 -pix_fmt yuv420p -g 250 `
                -c:a aac -b:a 32k -ac 2 -ar 48000 `
                -map_metadata -1 -fflags +bitexact -flags:v +bitexact -flags:a +bitexact `
                -movflags +faststart $file
            if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $file" }
            Write-Output $file
        }
    }
}
finally {
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
