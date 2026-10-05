#!/bin/bash
# Generates the bundled Viking sail textures (ImageMagick 7) into Package/ShipwrightsTouch-Sails.
# Run from anywhere; works in a temporary folder. Designs follow the historical evidence: the
# Gokstad sail remnants (white wool, sewn-on red stripes), the Gotland picture stones (chequered
# and lozenge sails), and plain wadmal; all sewn from strips of cloth, hence the seams.
# Output is 1024x1024, quantized to 256 colors and saved as RGB PNG to stay under 1 MB.
#
# The output is NOT byte-reproducible (ImageMagick's noise and quantization vary between runs).
# The committed PNGs in Package/ are canonical: a sail is identified by the hash of its file, so
# regenerating gives it a new identity and ships using the old one fall back to the vanilla sail.
# Only rerun to make a deliberately new version, and check the sizes stay under 1 MB.
set -e
S=1024
OUT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/Package/ShipwrightsTouch-Sails"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

# Woven wool: fine thread noise (horizontal + vertical streaks), grey around 50%, used as overlay.
weave() {
  # Thread texture as a multiply layer: mostly white, thread streaks down to ~82%.
  magick -size ${S}x${S} xc:gray50 -seed $1 -attenuate 0.7 +noise Gaussian -colorspace gray \
    \( +clone -motion-blur 0x3+0 \) \( -clone 0 -motion-blur 0x3+90 \) -delete 0 -compose blend -define compose:args=50 -composite \
    -auto-level +level 82%,100% -colorspace sRGB weave_$1.png
}
blotch() {
  # Uneven dye and weathering: soft patches down to ~88%.
  magick -size 128x128 xc:gray50 -seed $1 +noise Random -colorspace gray -blur 0x10 -auto-level -resize ${S}x${S} \
    +level 88%,100% -colorspace sRGB blotch_$1.png
}
finish() {
  local in=$1 out=$2 seed=$3
  weave $seed; blotch $seed
  # Sewn seams between cloth strips every 128 px: a dark line with a faint highlight below.
  magick -size ${S}x${S} xc:none -fill 'rgba(40,25,15,0.35)' \
    -draw "rectangle 0,127 $S,129" -draw "rectangle 0,255 $S,257" -draw "rectangle 0,383 $S,385" \
    -draw "rectangle 0,511 $S,513" -draw "rectangle 0,639 $S,641" -draw "rectangle 0,767 $S,769" -draw "rectangle 0,895 $S,897" \
    -fill 'rgba(255,245,225,0.18)' \
    -draw "rectangle 0,130 $S,130" -draw "rectangle 0,258 $S,258" -draw "rectangle 0,386 $S,386" \
    -draw "rectangle 0,514 $S,514" -draw "rectangle 0,642 $S,642" -draw "rectangle 0,770 $S,770" -draw "rectangle 0,898 $S,898" \
    seams.png
  magick $in weave_$seed.png -compose multiply -composite \
    blotch_$seed.png -compose multiply -composite \
    seams.png -compose over -composite \
    \( -size ${S}x${S} gradient:'rgba(0,0,0,0)-rgba(60,40,20,0.22)' \) -compose over -composite \
    -alpha off -depth 8 PNG24:$out
}

# 1. Gokstad: off-white wadmal with sewn-on red vertical stripes.
magick -size ${S}x${S} xc:'#E4D7BA' -fill '#8C2A21' \
  -draw 'rectangle 96,0 223,1024' -draw 'rectangle 448,0 575,1024' -draw 'rectangle 800,0 927,1024' base1.png
finish base1.png sail-gokstad-stripes.png 11

# 2. Gotland lozenge: natural wool with red diagonal reinforcing bands forming diamonds.
args=""; for i in -1024 -768 -512 -256 0 256 512 768 1024; do args="$args -draw 'line $i,0 $((i+1024)),1024' -draw 'line $((i+1024)),0 $i,1024'"; done
eval magick -size ${S}x${S} xc:'#DCCFB2' -stroke "'#7E2A1E'" -strokewidth 22 -fill none $args base2.png
finish base2.png sail-gotland-lozenge.png 22

# 3. Gotland chequer: red and white checks, 4x4.
magick -size 256x256 xc:'#E4D7BA' -fill '#8C2A21' -draw 'rectangle 0,0 127,127' -draw 'rectangle 128,128 255,255' \
  -write mpr:tile +delete -size ${S}x${S} tile:mpr:tile base3.png
finish base3.png sail-gotland-checks.png 33

# 4. Plain wadmal: undyed wool strips of slightly different shades.
magick -size ${S}x${S} xc:'#CFC3A8' -fill '#C6B99C' -draw 'rectangle 0,0 1024,127' -draw 'rectangle 0,384 1024,511' -draw 'rectangle 0,768 1024,895' \
  -fill '#D6CBB2' -draw 'rectangle 0,256 1024,383' -draw 'rectangle 0,640 1024,767' base4.png
finish base4.png sail-plain-wadmal.png 44

mkdir -p "$OUT"
save() {
  magick "$1" -dither FloydSteinberg -colors 256 PNG8:q.png
  magick q.png -strip -define png:compression-level=9 PNG24:"$OUT/$2.png"
}
save sail-gokstad-stripes.png "Gokstad Stripes"
save sail-gotland-lozenge.png "Gotland Lozenge"
save sail-gotland-checks.png "Gotland Checks"
save sail-plain-wadmal.png "Plain Wadmal"
ls -la "$OUT"
