# Normalmap Generator — Usage Guide

A Unity Editor extension that generates beveled normal maps from mask images (black-and-white or grayscale).

---

## Requirements

| Item | Requirement |
|---|---|
| Unity Version | 2022.3.22f1 or later |
| Graphics API | DirectX 11 / Metal / Vulkan |

A ComputeShader is used for processing, so a GPU that supports one of the APIs above is required.

---

## Opening the Window

In the Unity menu bar, select **`dennokoworks > Normalmap Generator`**.

---

## Basic Workflow

1. Drag and drop a mask texture from the Project window onto the **Mask Texture** field (or use the picker button).
2. Adjust parameters as needed — the preview updates automatically.
3. Click **"Generate Normal Map"**.
4. The output PNG is saved to the **`output/`** folder in the same directory as the input asset.

---

## Input

- Supported formats: PNG / JPEG
- Input images are treated as grayscale (luminance value is used for color images)
- Any texture asset in the Project window can be used

---

## Parameters

### Input Mode

| Mode | Description |
|---|---|
| **Threshold** | Optimized for pure black-and-white masks. Binarizes the input at the threshold and generates bevel from the resulting boundary. |
| **GrayWeight** | Handles anti-aliased edges or grayscale gradients. Uses the gray value as the upper limit of the height map. |

### Threshold

- Range: 0.0 – 1.0
- Pixels **brighter** than this value are treated as foreground (white); darker pixels as background (black).
- The comparison is made against the value you see in an image editor (display / sRGB space). `0.5` corresponds to the 128/255 gray you would pick with a color picker.

> **Changed behaviour:** earlier versions compared against the linearized value, so `0.5` actually behaved like sRGB 188/255. Projects carried over from those versions may need the threshold lowered to reproduce the previous result.

### Invert Mask

When enabled, the mask is inverted before processing. Use this when you want to apply the normal effect to the dark areas of the mask.

### Disable Bevel

When enabled, the distance transform step is skipped and the raw binarized mask is used directly as the height map. This produces sharp, near-vertical edges rather than a beveled slope.

### Bevel Radius (px)

- Range: 1 – 200 (slider range: 1 – 100)
- Specifies the width of the bevel in pixels.
- Larger values produce a gentler, wider slope.
- Values beyond the slider range can be entered directly in the number field on the left.

### Profile

Selects the cross-sectional shape of the bevel.

| Profile | Characteristic |
|---|---|
| **Linear** | Uniform slope — the simplest shape. |
| **Logarithmic** | Steep near the edge, gradually flattening toward the interior. |
| **Exponential** | Gentle near the edge, becoming steeper toward the interior. |
| **Smoothstep** | S-curve that flattens out at both ends. The other profiles change slope abruptly where the bevel meets the flat top, which shows up as a thin crease line in the normal map; Smoothstep removes it. |

### Strength

- Range: 0.1 – 50
- Controls the intensity (steepness) of the normal deflection.
- Higher values produce more pronounced surface detail.

### Dither

Adds a ±1/255 dither before the normal map is quantized to 8 bits per channel. Leave it on unless you need bit-exact flat regions: without it, gentle bevel slopes show visible stepping (banding) in the output PNG.

### Normal Type

| Type | Y Channel Direction | Typical Use |
|---|---|---|
| **DirectX** | Y+ (bright G = upward) | Unity / DirectX-based tools |
| **OpenGL** | Y− (dark G = upward) | Blender / OpenGL-based tools |

---

## Output Settings

### Overwrite if file exists

When enabled, an existing file with the same name will be overwritten. When disabled, generation is skipped if the output file already exists.

---

## Output Location

```
Same directory as the input file/
  output/
    {original filename}_normal.png
```

Example: if the input is `Assets/Textures/mask.png`,  
the output will be saved as `Assets/Textures/output/mask_normal.png`.

The Unity AssetDatabase is refreshed automatically after saving.

---

## Preview

- The input image and the generated normal map are displayed side by side in the window.
- The preview updates automatically after a parameter change: **0.1 s** for parameters that only affect the last stages (Strength, Profile, Normal Type, Input Mode, and shrinking the Bevel Radius) and **0.3 s** for ones that rebuild the distance field (Threshold, Invert Mask, changing the texture, growing the Bevel Radius).
- The preview resolution is **min(source resolution, Max)**, where **Max** is the dropdown in the preview toolbar (1024 / 2048 / 4096, default 2048).
  - A source at or below Max is processed at its native resolution, so the preview is **identical to the saved output**.
  - A larger source is box-filtered down to Max, and Bevel Radius / Strength are scaled to match.
- The resolution actually in use is shown below the preview panes.
- Preview resolution does **not** depend on the window size — resizing the window is free and does not trigger a recomputation.

### Inspecting the preview

| Action | Result |
|---|---|
| Mouse wheel over a preview pane | Zoom (1× – 32×) |
| Drag while zoomed in | Pan |
| Double-click | Reset to fit |

Both panes share the same zoom and pan, so the input and the normal map stay aligned.

---

## Language

Click **EN / JA** in the top-right corner of the window to switch the UI language. The preference is saved across Editor sessions.

---

## Troubleshooting

| Symptom | Solution |
|---|---|
| "ComputeShader not found" error | Verify that `NormalMapGenerator.compute` is located in `Assets/Editor/Normalmap_generator/`. |
| Generate button is disabled | Make sure a Mask Texture has been assigned. |
| Bevel appears along the image edges | The image should have black (background) pixels at its borders. White pixels that touch the image boundary will not receive bevel. |
| Preview differs from the generated output | Only possible when the source is larger than the **Max** setting. Raise Max (or lower the source resolution) to make the two match exactly. |
| Preview looks stale after re-importing the texture | Press **Update** in the preview toolbar; it forces a full rebuild rather than reusing the cached stages. |
| Preview is slow on a large texture | Lower **Max**. Most parameters only re-run the final stages, but Threshold / Invert / a larger Bevel Radius rebuild the distance field, which is the expensive part. |
| Banding or stepping on gentle slopes | Make sure **Dither** is enabled. |
| A thin crease line where the bevel meets the flat area | Use the **Smoothstep** profile — Linear / Logarithmic / Exponential change slope abruptly at the top of the bevel. |
