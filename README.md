# URP UI Blur

Material based background blur for **Screen Space Camera** UI in URP (Render Graph), in the spirit of HDRP
refraction: the renderer feature builds a blur pyramid of the frame, any UI graphic with the blur material samples it.

- Blur is a regular UI material (`RottenEagle/UI/Blur Panel`). Sprite alpha defines the shape; `Sliced` sprites,
  stencil `Mask`, `RectMask2D`, `CanvasGroup` and tint via `Image.color` work as usual.
- **Blur strength is a material property** (`_BlurStrength`, 0–1). Use material instances for different strengths.
- Panels blur the scene and the UI under them, UI drawn after a panel stays sharp on top of it.
  Panels do not blur each other.
- No components. Cost does not depend on the number of panels: one raster pass per pyramid level (5 at 1080p).

## Requirements

- Unity 6.0+
- Universal RP 17+ (Render Graph)

## Installation

1. **Window → Package Manager → + → Add package from git URL…**
2. Paste `https://github.com/RottenEagle1337/urp-ui-blur.git` (optionally `#tag` or `#branch`).

## Setup

1. **Renderer.** Add **Ui Blur Feature** to the URP Renderer Data. In the feature inspector:
   - press **Remove UI layers from Transparent Layer Mask** (the feature draws UI itself, after post processing);
   - press **Use 'UI Blur' sorting layer** if panels must blur UI under them (see below).
2. **Canvas.** `Render Mode = Screen Space - Camera`, `Render Camera` = the camera using this renderer, layer `UI`.
3. **Panels.** **GameObject → UI → Blur Panel**, or set the material of any `Image` to
   `Packages/com.rotteneagle.urp-ui-blur/Runtime/Materials/UiBlurPanel.mat` (or an instance of it).

### Blurring UI under the panels

Unity renders a canvas as one unit, so the point where the frame is captured has to be a canvas boundary.
Put the UI that holds blur panels into a **blur group**: **GameObject → UI → Blur Group** creates a child object
with a nested `Canvas` (override sorting, sorting layer `UI Blur`), `GraphicRaycaster` and `CanvasGroup`.

```
Canvas (Screen Space - Camera)
├─ Background          ← HUD, world labels, … : blurred under the panels
└─ Blur Group          ← nested Canvas on sorting layer "UI Blur"
   ├─ Panel (blur material, strength 0.6)
   │  └─ Text          ← sharp, on top of the blur
   └─ Panel (blur material instance, strength 1.0)
```

Without blur sorting layers in the feature, the pyramid is built once before all UI: panels blur only the scene.

## Ui Blur Feature

| Setting | Default | Role |
|---|---|---|
| Blur Sorting Layers | empty | The pyramid is rebuilt before each of these sorting layers |
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
Draw UI [below 'UI Blur']             merged with the post processing pass when possible
Pyramid: Down ½ → ¼ → ⅛ → 1/16 → 1/32 _UIBlurLevel1..7
Draw UI ['UI Blur' and above]         panels sample the pyramid
```

- Kawase downsample (5 taps) per level. The panel shader picks the two levels around its strength and samples
  them with cubic B-spline filtering (4 bilinear taps each), so the radius changes continuously.
- Raster passes only, fixed texture sizes (stable Render Graph pool), no per-frame GC allocations.
- The panel shader samples the pyramid with `SV_Position`, no platform specific UV flips.
- Scene View, Preview and Reflection cameras draw UI without blur.

## Limitations

- Only Screen Space Camera canvases. Screen Space Overlay UI is drawn by URP after the camera (it is fine for
  a HUD that must stay on top and unblurred).
- UI drawn by the feature ignores scene depth (always on top of the scene).
- The pyramid is built every frame, also when no panel is visible.
