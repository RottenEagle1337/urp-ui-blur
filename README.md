# URP UI Blur

Material based background blur for **Screen Space Camera** UI in URP (Render Graph), in the spirit of HDRP
refraction: the renderer feature builds a blur pyramid of the frame, any UI graphic with the blur material samples it.

- Blur is a regular UI material (`RottenEagle/UI/Blur Panel`). Sprite alpha defines the shape; `Sliced` sprites,
  stencil `Mask`, `RectMask2D`, `CanvasGroup` and tint via `Image.color` work as usual.
- **Blur strength is a material property** (`_BlurStrength`, 0–1). Use material instances for different strengths.
- **Works with the normal hierarchy of one canvas:** a panel blurs the scene and the UI drawn before it,
  UI drawn after it (its children, following siblings) stays sharp on top. Panels do not blur each other.
- No components, no sorting layers. Cost does not depend on the number of panels.

## Requirements

- Unity 6.0+
- Universal RP 17+ (Render Graph)

## Installation

1. **Window → Package Manager → + → Add package from git URL…**
2. Paste `https://github.com/RottenEagle1337/urp-ui-blur.git` (optionally `#tag` or `#branch`).

## Setup

1. **Renderer.** Add **Ui Blur Feature** to the URP Renderer Data and press
   **Remove UI layers from Transparent Layer Mask** in its inspector (the feature draws UI itself, after post processing).
2. **Canvas.** `Render Mode = Screen Space - Camera`, `Render Camera` = the camera using this renderer, layer `UI`.
3. **Panels.** **GameObject → UI → Blur Panel**, or set the material of any `Image` to
   `Packages/com.rotteneagle.urp-ui-blur/Runtime/Materials/UiBlurPanel.mat` (or an instance of it).

```
Canvas (Screen Space - Camera)
├─ HUD                         ← blurred under the panels below
└─ Menu (CanvasGroup)
   ├─ Panel (blur material, strength 0.6)
   │  └─ Text                  ← sharp, on top of the blur
   └─ Panel (blur material instance, strength 1.0)
```

## Ui Blur Feature

| Setting | Default | Role |
|---|---|---|
| Max Blur Levels | 5 | Pyramid levels at the reference height, `_BlurStrength = 1` samples the last one (radius ≈ 2^levels px) |
| Reference Height | 1080 | The blur radius scales with the camera target height |
| Ui Layer Mask | `UI` | Unity layers of the UI drawn by the feature |
| Support Stencil Masks | on | Binds the camera depth-stencil while drawing UI (needed for `Mask`) |
| Injection Point | After Rendering Post Processing | UI is not affected by bloom, tonemapping, etc. |

## Blur Panel material

| Property | Role |
|---|---|
| `_BlurStrength` | 0–1, blur radius ≈ `2^(strength * Max Blur Levels)` px at the reference height |
| `_Color` | Tint, multiplied with `Image.color` |

Animate the strength with `material.SetFloat("_BlurStrength", value)` on a material instance.
Different materials break UI batching, keep the number of distinct strengths small.

## How it works

```
Opaques → Skybox → Transparents (without UI layer) → Post processing
Down ½        camera color → level 1
Capture UI    all UI again into level 1 (half resolution)   one native render pass with Down ½
Down ¼ … 1/32 levels 2..5
Draw UI       all UI on the camera color, panels sample the pyramid
```

- **Capture UI** draws the UI with its own depth-stencil buffer and the `_UI_BLUR_CAPTURE` keyword. Blur panels then
  write no color but a nearest depth mark in their shape, so UI drawn after a panel fails the depth test there.
  The pyramid holds only what is under each panel.
- Kawase downsample (5 taps) per level. The panel shader picks the two levels around its strength and samples
  them with cubic B-spline filtering (4 bilinear taps each), so the radius changes continuously.
- 6 render passes at 1080p (pyramid levels + draw), raster passes only, fixed texture sizes, no per-frame GC.
- The panel shader samples the pyramid with `SV_Position`, no platform specific UV flips.
- Scene View, Preview and Reflection cameras draw UI without blur.

## Limitations

- Only Screen Space Camera canvases. Screen Space Overlay UI is drawn by URP after the camera (fine for a HUD that
  must stay on top and unblurred).
- The depth mark covers the panel shape only: UI drawn after a panel right next to it can leak into the blur near the
  panel edge (up to the blur radius).
- UI is drawn twice (the capture at half resolution), UI draw calls on the CPU double.
- UI drawn by the feature ignores scene depth (always on top of the scene).
- Custom UI shaders must keep `ZTest [unity_GUIZTestMode]` (or `LEqual`) to be rejected under panels in the capture.
