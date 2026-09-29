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

Breaking rewrite. The blur is now a UI material and works in layers.

### Added
- `RottenEagle/UI/Blur Panel` material (`Runtime/Materials/UiBlurPanel.mat`), compatible with `UI/Default`:
  sprite shape, sliced sprites, stencil `Mask`, `RectMask2D`, `CanvasGroup`, tint via `Image.color`.
- Blur layers based on sorting layers: a panel blurs the scene and all UI of lower blur layers.
- Per layer settings (`levels`, `offset`), resolution independent radius (`referenceHeight`).
- `UiBlur.SetLayerStrength` runtime API with continuous radius scaling.
- Scissor to visible panel bounds and skipping of layers without visible panels.
- Inspector warning and fix button when the `UI` layer is still in the Transparent Layer Mask.

### Changed
- Dual Kawase blur (5 / 8 taps) instead of per panel Kawase ping-pong. `2 * levels - 1` raster passes per layer,
  independent of the panel count.
- UI of Screen Space Camera canvases is drawn by the feature after post processing.
- `UiBlurPanel` only reports the panel rect and sorting layer, it no longer hides or draws the `Image`.
- Blur textures use `B10G11R11_UFloatPack32` when supported.

### Removed
- `hideFromCapture`, the panel composite pass, `Custom/UiBlurKawase` and `Custom/UiBlurPanelBlit` shaders.

### Fixed
- Blur parameters were set on the shared material inside the render function, so every iteration used the last value.
- Per frame GC allocations (panel list, corner arrays, texture names).
