# Changelog

## [1.0.0] - 2026-06-13

- Initial UPM package release as `com.rotteneagle.urp-ui-blur`
- Screen Space Camera UI blur with per-panel Kawase blur
- Scene View preview support
- Runtime material creation (no material assets required)

## [1.1.0] - 2026-08-05

### Added
- Added support for `Image.Type = Sliced` in blur panels.
- Added transparent panel culling for alpha = 0.

### Changed
- Improved blur mask rendering for sliced sprites.
- Optimized blur rendering by skipping fully transparent panels.
## [2.0.0] - 2026-09-29

Breaking rewrite. The blur is now a UI material sampling a blur pyramid, similar to HDRP refraction.

### Added
- `RottenEagle/UI/Blur Panel` material (`Runtime/Materials/UiBlurPanel.mat`), compatible with `UI/Default`:
  sprite shape, sliced sprites, stencil `Mask`, `RectMask2D`, `CanvasGroup`, tint via `Image.color`.
- `_BlurStrength` material property: blur radius per material instance, continuous, resolution independent.
- Blur sorting layers: the pyramid is rebuilt before them, so panels blur the UI under them.
- `GameObject/UI/Blur Group` and `GameObject/UI/Blur Panel` menu items.
- Inspector buttons to fix the Transparent Layer Mask and to create the `UI Blur` sorting layer.

### Changed
- Per panel Kawase ping-pong replaced by one downsample pyramid (one raster pass per level) per blur sorting layer,
  independent of the panel count.
- UI of Screen Space Camera canvases is drawn by the feature after post processing.
- Blur textures use `B10G11R11_UFloatPack32` when supported.

### Removed
- `UiBlurPanel` and `UiBlurPanelRegistry` components, `hideFromCapture`, the panel composite pass,
  `Custom/UiBlurKawase` and `Custom/UiBlurPanelBlit` shaders.

### Fixed
- Blur parameters were set on the shared material inside the render function, so every iteration used the last value.
- Per frame GC allocations (panel list, corner arrays, texture names).
