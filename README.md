# URP UI Blur

Layered, material based background blur for **Screen Space Camera** UI in URP (Render Graph).

- Blur is a regular UI material (`RottenEagle/UI/Blur Panel`) on any `Image`: sprite alpha defines the shape,
  `Sliced` sprites, stencil `Mask`, `RectMask2D`, `CanvasGroup` and tint via `Image.color` work as usual.
- Blur layers are **sorting layers**. A panel on blur layer N blurs the scene and all UI of the layers below N.
  Panels of the same blur layer do not blur each other.
- Cost depends on the number of blur layers, not on the number of panels: `2 * levels - 1` passes per layer.

## Requirements

- Unity 6.0+
- Universal RP 17+ (Render Graph)

## Installation

1. **Window → Package Manager → + → Add package from git URL…**
2. Paste `https://github.com/RottenEagle1337/urp-ui-blur.git` (optionally `#tag` or `#branch`).

## Setup

1. **Sorting layers.** Create one sorting layer per blur layer, e.g. `UI_Base`, `UI_Blur1`, `UI_Blur2`, `UI_Top`.
2. **Renderer.** On the URP Renderer Data:
   - add **Ui Blur Feature**;
   - add one entry to **Blur Layers** per blur sorting layer (`UI_Blur1`, `UI_Blur2`);
   - remove the `UI` layer from **Transparent Layer Mask** (the feature inspector offers a button).
     The feature draws UI itself, after post processing, split by blur layers.
3. **Canvases.** `Render Mode = Screen Space - Camera`, `Render Camera` = the camera using this renderer,
   GameObject layer `UI`, `Sorting Layer` = the layer the canvas belongs to.
4. **Panels.** On an `Image`:
   - set **Material** to `Packages/com.rotteneagle.urp-ui-blur/Runtime/Materials/UiBlurPanel.mat`;
   - add **Ui Blur Panel** (reports the panel rect, lets the feature skip empty layers and limit the blur area).

Text and images after the panel in the same canvas are drawn on top of the blur and stay sharp.

## Ui Blur Feature

| Setting | Default | Role |
|---|---|---|
| Ui Layer Mask | `UI` | Unity layers of the UI drawn by the feature |
| Injection Point | After Rendering Post Processing | UI is not affected by bloom, tonemapping, etc. |
| Blur Layers | — | Sorting layer, `Levels` (1–6) and `Offset` per blur layer |
| Reference Height | 1080 | Blur radius scales with the camera target height |
| Skip Layers Without Panels | on | No blur work for a layer without a visible `UiBlurPanel` |
| Limit To Panel Bounds | on | Scissor the blur to the bounds of the visible panels (+ kernel reach) |
| Support Stencil Masks | on | Binds the camera depth-stencil while drawing UI (needed for `Mask`) |

Blur radius ≈ `2^levels * (offset + 0.5)` pixels at the reference height. Prefer more levels over large offsets.

## Runtime API

```csharp
UiBlur.SetLayerStrength("UI_Blur1", 0.5f); // 0..1, scales the blur radius continuously
UiBlur.GetLayerStrength("UI_Blur1");
UiBlur.ResetLayerStrengths();
```

Lower strength uses fewer passes. Values live in memory, the renderer asset is not modified.

## How it works

```
Opaques → Skybox → Transparents (without UI layer) → Post processing
Draw UI [sorting layers below first blur layer]        merged with the post processing pass when possible
Blur layer 1: Down ½ → Down ¼ → … → Up ½              → _UIBlurTexture
Draw UI [blur layer 1 .. blur layer 2)
Blur layer 2: …                                        → _UIBlurTexture
Draw UI [blur layer 2 .. ∞)
```

- Dual Kawase: downsample 5 taps, upsample 8 taps, result at half resolution.
- All passes are raster passes, textures have fixed sizes (stable Render Graph pool), no per-frame GC allocations.
- The panel shader samples `_UIBlurTexture` with `SV_Position`, no platform specific UV flips.
- Scene View, Preview and Reflection cameras draw UI without blur.

## Limitations

- Only Screen Space Camera canvases. Screen Space Overlay UI is drawn by URP after the camera and cannot be split.
- UI drawn by the feature ignores scene depth (always on top of the scene).
- A panel on a sorting layer below the first blur layer has no blur source (black).
