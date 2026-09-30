# Fixture-specific image validation. The Customer Desk has a light, detailed client
# area; valid PNG bytes or nonblank title bars cannot prove its UI was captured.
Add-Type -AssemblyName System.Drawing

function Get-FixtureScreenshotContent {
    param([Parameter(Mandatory)][string]$Path)
    $bitmap = [System.Drawing.Bitmap]::new([System.IO.Path]::GetFullPath($Path))
    try {
        if ($bitmap.Width -lt 300 -or $bitmap.Height -lt 250) {
            return [pscustomobject]@{ passed=$false; width=$bitmap.Width; height=$bitmap.Height; reason='Image too small for the Customer Desk fixture.' }
        }
        # Exclude all chrome and borders, including the nonblank OS title bar.
        $left = [int]($bitmap.Width * 0.1)
        $right = [int]($bitmap.Width * 0.9)
        $top = [int]($bitmap.Height * 0.15)
        $bottom = [int]($bitmap.Height * 0.9)
        $colors = [System.Collections.Generic.HashSet[int]]::new()
        $samples = 0
        $visible = 0
        for ($y = $top; $y -lt $bottom; $y += 9) {
            for ($x = $left; $x -lt $right; $x += 9) {
                $color = $bitmap.GetPixel($x, $y)
                $samples++
                if ([Math]::Max($color.R, [Math]::Max($color.G, $color.B)) -gt 32) { $visible++ }
                [void]$colors.Add($color.ToArgb())
            }
        }
        $visibleFraction = $visible / [double]$samples
        $passed = $visibleFraction -gt 0.5 -and $colors.Count -ge 8
        return [pscustomobject]@{
            passed=$passed
            width=$bitmap.Width
            height=$bitmap.Height
            sampledClientPixels=$samples
            visibleClientFraction=[Math]::Round($visibleFraction, 4)
            distinctClientColors=$colors.Count
            reason=if ($passed) { 'Detailed light fixture client pixels present; human semantic review remains separate.' } else { 'Client pixels are blank, almost black, or lack expected fixture detail.' }
        }
    }
    finally { $bitmap.Dispose() }
}
