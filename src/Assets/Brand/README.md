# Hen Hotspot brand assets

The H monogram uses two rounded paths that meet at the center to suggest a shared connection. Its open center keeps the silhouette recognizable at Windows icon sizes.

## Colors and typography

| Use | Value |
| --- | --- |
| Brand background | Graphite: `#101A23` |
| Brand accent | Mint: `#57D6AD` |
| Workspace background | Canvas: `#F6F7F5` |
| Native typography | Segoe UI Variable, with Segoe UI as fallback |

The generated image contains small variations in mint. The functional UI accent uses the exact `#57D6AD` token.

## Files

- `hen-mark-source.png`: original ImageGen output with transparency.
- `hen-mark.png`: 512 px PNG used by the WPF app.
- `hen-mark-{size}.png`: exports at 16, 24, 32, 48, 64, 128, 256, and 512 px.
- `hen-hotspot.ico`: multi-resolution Windows icon containing 32-bit PNG images.
- `BrandResources.xaml`: shared brand resources, including `HenMarkImage`.
- `hen-mark.svg` and `hen-wordmark.svg`: SVG containers for the generated raster mark. The embedded mark is **not a vector path**. The wordmark uses live text.
- `hen-brand-preview.png`: brand preview and size reference.
- `generation-prompt.txt`: the generation specification and source reference.

## WPF integration

Include the PNG and ICO assets as project resources, set `Assets/Brand/hen-hotspot.ico` as the application icon, and merge `Assets/Brand/BrandResources.xaml` into the window resources. Use `HenMarkImage` as the source for the brand image.

The mark includes transparent padding. A 48 px image box produces a visible silhouette of approximately 35 px. It can be placed directly on the dark sidebar without an additional enclosing tile.

## Origin and verification

The mark was created with the built-in ImageGen tool. Size and format exports preserve the selected source silhouette.

The brand preview was visually inspected at 24, 32, 48, 64, and 128 px. The Windows icon also includes a 16 px representation.
