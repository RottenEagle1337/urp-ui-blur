# URP UI Blur

Kawase blur for **Screen Space Camera** UI panels using URP Render Graph.

## Requirements

- Unity 6+
- Universal RP 17+

## Installation from Package Manager UI

1. **Window → Package Manager**
2. **+ → Add package from git URL…**
3. Paste `https://github.com/RottenEagle1337/urp-ui-blur.git` (optionally add `#tag` or `#branch`)
4. Click **Add**

## Quick setup

1. **Install the package**
2. **Enable the renderer feature** — open your URP Renderer Data asset → **Add Renderer Feature** → **Ui Blur Feature**.
3. **Configure the canvas** — set Render Mode to **Screen Space - Camera** and assign the same camera that renders your 3D scene.
4. **Add a blur panel** — on a UI `Image` that should show blurred content behind it:
   - Add **Ui Blur Panel**
   - Keep the default UI material (do not assign a custom blur material)
   - Use a white sprite and white color `(255, 255, 255, 255)` for a full rectangular panel
   - Shape the panel with the `Image` sprite alpha (rounded corners, icons, etc.)
5. **Tune quality** on the renderer feature — start with `Downsample: 2`, `Iterations: 4`, `Offset: 2`. Lower downsample/iterations on weaker GPUs.

Shader slots on **Ui Blur Feature** can stay empty; materials are created at runtime via `Shader.Find`.

### Checklist

| Step | What to verify |
|------|----------------|
| Package | `com.rotteneagle.urp-ui-blur` appears in Package Manager |
| Renderer | **Ui Blur Feature** is present on the active URP Renderer Data |
| Camera | Canvas `worldCamera` matches the game camera |
| Panel | `Ui Blur Panel` is on the `Image`, enabled, and visible in the hierarchy |
| Mask | `Image.color.a` defines panel opacity; sprite alpha defines shape |

## Components

### `UiBlurFeature` (Renderer Feature)

`ScriptableRendererFeature` added to URP Renderer Data. Owns two render passes and creates blur materials at runtime.

| Setting | Range | Default | Role |
|---------|-------|---------|------|
| Iterations | 1–6 | 4 | Number of Kawase blur passes per panel |
| Downsample | 1–4 | 2 | Resolution divisor for capture and atlas |
| Offset | 0.5–6 | 2 | Blur spread; increases each iteration |
| Blur Shader | — | auto | Optional override for `Custom/UiBlurKawase` |
| Panel Blit Shader | — | auto | Optional override for `Custom/UiBlurPanelBlit` |

Skips Preview and Reflection cameras. Requires at least one active `UiBlurPanel` linked to the rendering camera.

### `UiBlurPanel` (MonoBehaviour)

Lives on the same GameObject as a UI `Image`. Registers the panel with the blur system and supplies screen-space bounds each frame.

| Field | Description |
|-------|-------------|
| Hide From Capture | When enabled, culls the `CanvasRenderer` so the panel is not drawn by the normal UI pass before blur is composited (recommended: on) |

The `Image` provides the **mask** (sprite alpha + color). The component does not replace the `Image` — blur is drawn by the renderer feature, then masked to the panel shape.

### `UiBlurPanelRegistry` (internal)

Static registry of enabled `UiBlurPanel` instances. The renderer feature reads this list each frame to know which panels to blur. No manual setup required.

### Shaders

| Shader | Used by |
|--------|---------|
| `Custom/UiBlurKawase` | Ping-pong Kawase blur passes |
| `Custom/UiBlurPanelBlit` | Final composite into the camera color target |

Global texture `_GlobalUiBlurTexture` holds the downsampled blur atlas after the blur pass.

## How it works

Only **Screen Space - Camera** canvases are supported. The canvas `worldCamera` must match the camera being rendered (Scene View is supported in the Editor).

```
[3D scene + post-processing]
        │
        ▼
┌─────────────────────────────────────┐
│  Pass 1: UI Blur                    │  AfterRenderingPostProcessing
│  • For each UiBlurPanel:            │
│    - Crop camera color to panel rect│
│    - Kawase blur (ping-pong RTs)    │
│    - Copy result into blur atlas    │
│  • Publish atlas as                 │
│    _GlobalUiBlurTexture             │
└─────────────────────────────────────┘
        │
        ▼
┌─────────────────────────────────────┐
│  Pass 2: UI Blur Panels             │  AfterRenderingPostProcessing + 1
│  • For each panel:                  │
│    - Draw fullscreen quad in panel  │
│      screen rect                    │
│    - Sample atlas UV for that panel │
│    - Multiply by Image sprite mask  │
│    - Alpha blend into camera color  │
└─────────────────────────────────────┘
        │
        ▼
[Rest of frame / UI if any]
```

**Per-panel capture** — each panel gets ping-pong RTs sized to its own rectangle (not a single shared max buffer), so different panel sizes stay sharp and undistorted.

**Atlas** — blurred regions are packed into one downsampled atlas matching the camera resolution ÷ `Downsample`. Each panel stores its atlas UVs for the composite pass.

**Hide from capture** — with `hideFromCapture` enabled, the panel `Image` is culled during normal UI rendering so you do not see an unblurred copy underneath; only the blurred composite from pass 2 is visible.

**Performance tips** — blur cost scales with panel pixel area × `Iterations` ÷ `Downsample²`. Use fewer/larger downsample on mobile; limit the number and size of simultaneous blur panels.
