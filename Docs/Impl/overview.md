コードを全て確認しました。以下に移植用の要件定義書と技術仕様書を作成します。

---

# Unity Editor拡張 ノーマルマップジェネレーター 移植要件定義書

## 1. 概要

マスク画像（白黒またはグレースケール）からベベル付きノーマルマップを生成するUnity Editor拡張ツール。元のPython実装（`dennoko/Normalmap_Generator_v3`）をベースに、アルゴリズムの改善を加えてC# + ComputeShaderで再実装する。

---

## 2. 機能要件

### 2.1 入力

- 対応フォーマット：PNG / JPEG
- 入力方法：Projectウィンドウからのオブジェクトフィールドへのアサイン
- 入力画像はグレースケールとして扱う（カラー画像の場合はRチャンネルのみ使用）

### 2.2 入力モード（新規設計）

元実装の `detect_edges` ステップを廃止し、以下の2モードに置き換える。

#### モード1: 閾値処理モード（Threshold Mode）

白黒マスクに最適。入力画像を閾値で二値化し、その境界から距離変換でベベルを生成する。

```
入力 → グレースケール変換 → 閾値二値化(binary) → 距離変換 → プロファイル適用
     → 高さマップ合成 min(binary, intensity) → Sobel → 法線マップ
```

#### モード2: グレー値重みモード（Gray Weight Mode）

アンチエイリアスやグレースケールのグラデーションを持つ入力に対応。境界検出には閾値二値化を使い、高さマップ合成時にグレー値を上限として使用する。

```
入力 → グレースケール変換 → 閾値二値化(binary) → 距離変換 → プロファイル適用
     → 高さマップ合成 min(gray, intensity) → Sobel → 法線マップ
```

2モードの違いは高さマップ合成時の `base` のみ：
- Threshold Mode: `base = binary`（0.0 または 1.0）
- Gray Weight Mode: `base = gray`（0.0〜1.0の連続値）

### 2.3 パラメータ一覧

| パラメータ名 | 型 | デフォルト | 範囲 | 説明 |
|---|---|---|---|---|
| `InputMode` | enum | Threshold | - | 入力処理モード |
| `Threshold` | float | 0.5 | 0.0〜1.0 | 二値化の閾値 |
| `BevelRadius` | int | 15 | 1〜200 | ベベルの半径（px） |
| `Strength` | float | 1.0 | 0.01〜10.0 | 法線の強度 |
| `ProfileType` | enum | Linear | - | ベベルの斜面形状 |
| `NormalMapType` | enum | DirectX | - | DirectX(Y+) / OpenGL(Y-) |
| `InvertMask` | bool | false | - | マスクの白黒を反転 |
| `DisableBevel` | bool | false | - | ベベル生成を無効化 |
| `SaveIntermediates` | bool | false | - | 中間テクスチャをアセットとして保存 |
| `OverwriteExisting` | bool | true | - | 同名ファイルの上書き |

### 2.4 出力

- ノーマルマップ PNG（入力アセットと同じディレクトリの `output/` フォルダ）
- `SaveIntermediates` が有効な場合、`processing/` フォルダに以下を保存：
  - `{name}_binary.png`（二値化マスク）
  - `{name}_bevel.png`（距離変換・プロファイル適用後）
  - `{name}_height.png`（高さマップ）
- 保存後は `AssetDatabase.Refresh()` を呼び出す

### 2.5 プレビュー

- `EditorWindow` 内にリアルタイムプレビューを表示
- パラメータ変更後600msのデバウンスを経て自動更新
- プレビュー処理は512×512にリサイズして実行（軽量化）
- 入力画像プレビューと出力ノーマルマッププレビューを並列表示

---

## 3. 非機能要件

- Unity 2021.3 LTS 以降を対象とする
- ComputeShaderを使用するため、対応グラフィックスAPIが必要（DirectX11 / Metal / Vulkan）
- 処理はメインスレッドをブロックしないよう `async/await` または `EditorApplication.update` で非同期実行
- 入力画像の解像度に制限は設けないが、プレビューは512×512に縮小して処理する

---

## 4. 技術仕様

### 4.1 Enumの定義

```csharp
public enum InputMode    { Threshold, GrayWeight }
public enum ProfileType  { Linear, Logarithmic, Exponential }
public enum NormalMapType { DirectX, OpenGL }
``` [1](#3-0) 

### 4.2 処理パイプライン詳細

#### Step 1: グレースケール変換 + 閾値二値化

入力テクスチャの各ピクセルについて：

```hlsl
// ComputeShader: Binarize カーネル
float gray = dot(inputColor.rgb, float3(0.299, 0.587, 0.114));
binaryTex[id] = gray > threshold ? 1.0 : 0.0;
grayTex[id]   = gray;  // Gray Weight Mode用に保持
```

`InvertMask` が有効な場合は `gray = 1.0 - gray`、`binary = 1.0 - binary` として反転する。

#### Step 2: 距離変換（JFA: Jump Flooding Algorithm）

`detect_edges` を廃止し、`binary` から直接距離変換を行う。ComputeShaderでJFAを実装する。 [2](#3-1) 

**JFA実装手順：**

1. **シード初期化（`JFA_Init` カーネル）**
   - `binary[i] == 1.0` かつ8近傍に `binary[j] == 0.0` が存在するピクセルを境界シードとして設定
   - シードバッファ（`RG32Float`）に自身のUV座標を書き込む
   - 非シードピクセルは `(-1, -1)` で初期化

2. **JFAパス（`JFA_Step` カーネル）**
   - ステップサイズ `k = max(W, H) / 2` から開始し、`k = k / 2` で `log2(max(W,H))` 回繰り返す
   - 各パスで8方向（`±k` ピクセル）の近傍シードを参照し、L2距離が小さい方に更新

3. **距離計算（`JFA_Distance` カーネル）**
   - 最終シードバッファから各ピクセルの最近境界までのL2距離を計算
   - `dist = clamp(distance(uv, seedUV) * textureSize, 0, bevelRadius)`

**`DisableBevel` が `true` の場合はStep 2〜3をスキップする。**

#### Step 3: プロファイル適用

正規化距離 `nd = dist / bevelRadius`（0.0〜1.0）に対して：

```hlsl
// ComputeShader: ApplyProfile カーネル
float nd = clamp(dist / bevelRadius, 0.0, 1.0);
float intensity;

if (profileType == 0)       // Linear
    intensity = nd;
else if (profileType == 1)  // Logarithmic
    intensity = log(1.0 + 9.0 * nd) / log(10.0);
else                        // Exponential
    intensity = (exp(nd * 2.5) - 1.0) / (exp(2.5) - 1.0);
```

その後、3×3ガウシアンブラーで平滑化する。 [3](#3-2) 

#### Step 4: 高さマップ合成

```hlsl
// ComputeShader: CompositeHeightMap カーネル
float base;
if (inputMode == 0)  // Threshold Mode
    base = binaryTex[id];
else                 // Gray Weight Mode
    base = grayTex[id];

float heightMap;
if (disableBevel)
    heightMap = base;
else
    heightMap = min(base, intensity);  // ベベルがbaseを超えないよう制限
``` [4](#3-3) 

#### Step 5: 法線マップ生成

高さマップに3×3 Sobelフィルタを適用して勾配を計算する。

**Sobelカーネル：**
```
Sobel X:  [-1, 0, +1]    Sobel Y:  [-1, -2, -1]
          [-2, 0, +2]              [ 0,  0,  0]
          [-1, 0, +1]              [+1, +2, +1]
```

**スケーリング（元実装からの修正）：**

元実装は `/ 255.0` でスケールしており、`strength=1.0` でも境界部分の法線が水平（sz=0）になる問題があった。float テクスチャ（0〜1）を前提とした場合、3×3 Sobelの理論的最大値は `8.0` なので：

```hlsl
// ComputeShader: GenerateNormalMap カーネル
// 境界処理: BORDER_REPLICATE（境界ピクセルを複製）
float sx = sobelX(heightMap, id) * strength / 8.0;
float sy = sobelY(heightMap, id) * strength / 8.0;
float sz = sqrt(1.0 - clamp(sx*sx + sy*sy, 0.0, 1.0));

// RGBA チャンネルパック（R=X, G=Y, B=Z, A=1）
float r = clamp(0.5 - sx * 0.5, 0.0, 1.0);  // X
float b = clamp(0.5 + sz * 0.5, 0.0, 1.0);  // Z

// DirectX (Y+)
float g = clamp(0.5 - sy * 0.5, 0.0, 1.0);
// OpenGL (Y-)
// float g = clamp(0.5 + sy * 0.5, 0.0, 1.0);
```

元実装はOpenCVのBGR順で格納してから `COLOR_BGR2RGB` 変換していたが、Unity側はRGBAで直接構築するため変換不要。 [5](#3-4) 

---

### 4.3 ファイル構成

```
Assets/
  Editor/
    NormalMapGeneratorWindow.cs   // EditorWindow（UI・パラメータ管理）
    NormalMapProcessor.cs         // 処理ロジック（ComputeShader呼び出し）
  Shaders/
    NormalMapGenerator.compute    // ComputeShader（全カーネル）
```

### 4.4 ComputeShaderカーネル一覧

| カーネル名 | 処理内容 |
|---|---|
| `Binarize` | グレースケール変換 + 閾値二値化 |
| `JFA_Init` | JFAシード初期化（境界ピクセルの検出） |
| `JFA_Step` | JFAパス（ステップサイズをCBufferで渡す） |
| `JFA_Distance` | 最終L2距離計算 |
| `ApplyProfile` | プロファイル適用 + 3×3ガウシアンブラー |
| `CompositeHeightMap` | 高さマップ合成（モード分岐） |
| `GenerateNormalMap` | Sobel + 法線パック |

### 4.5 RenderTextureフォーマット

| 用途 | フォーマット | 備考 |
|---|---|---|
| グレースケール入力 | `RFloat` | 0.0〜1.0 |
| 二値化マスク | `RFloat` | 0.0 or 1.0 |
| JFAシードバッファ（×2） | `RGFloat` | シードのUV座標、ピンポンバッファ |
| 距離マップ | `RFloat` | 0.0〜bevelRadius |
| ベベル強度マップ | `RFloat` | 0.0〜1.0 |
| 高さマップ | `RFloat` | 0.0〜1.0 |
| 出力ノーマルマップ | `RGBA32` | R=X, G=Y, B=Z, A=255 |

### 4.6 境界処理

| 処理 | 境界処理方式 | 理由 |
|---|---|---|
| JFA距離変換 | 画像外側を背景（0）として扱う | 画像端にベベルが生じないようにする |
| Sobelフィルタ | `BORDER_REPLICATE`（境界ピクセルを複製） | 端部の法線が不自然にならないようにする | [6](#3-5) 

---

### 4.7 プレビュー処理フロー

```
入力テクスチャ
  → Graphics.Blit で 512×512 に縮小（LANCZOS相当: Bilinear）
  → NormalMapProcessor.Process() を実行
  → 結果を EditorWindow の GUILayout.Label に表示
```

元実装のプレビューは512×512にリサイズして処理している。 [7](#3-6) 

---

### 4.8 元実装からの変更点まとめ

| 項目 | 元実装 | 移植版 |
|---|---|---|
| エッジ検出 | `detect_edges`（Sobel×4カーネル） | 廃止。閾値二値化に置き換え |
| 入力モード | なし（単一パイプライン） | Threshold / GrayWeight の2モード |
| 距離変換 | `cv2.distanceTransform` | JFA ComputeShader |
| `DisableBevel` 時の無駄な計算 | 常に全ステップ実行 | Step 2〜3をスキップ |
| `strength` スケーリング | `/ 255.0`（不適切） | `/ 8.0`（float テクスチャ前提） |
| チャンネル順 | BGR構築 → RGB変換 | RGBA直接構築（変換不要） |
| 非同期処理 | `threading.Thread` | `async/await` または `Task` |

### Citations

**File:** normalmap_generator/types.py (L4-12)
```python
class ProfileType(Enum):
    LINEAR = 1
    LOGARITHMIC = 2
    EXPONENTIAL = 3


class NormalMapType(Enum):
    DX = 1
    GL = 2
```

**File:** normalmap_generator/processor.py (L31-33)
```python
        edge_mask = (pad < 128).astype(np.uint8) * 255
        dist = cv2.distanceTransform(255 - edge_mask, cv2.DIST_L2, cv2.DIST_MASK_PRECISE)
        dist = np.minimum(dist, radius)
```

**File:** normalmap_generator/processor.py (L35-43)
```python
        if profile_type == ProfileType.LINEAR:
            intensity = nd * 255
        elif profile_type == ProfileType.LOGARITHMIC:
            intensity = 255 * (np.log(1 + 9 * nd) / np.log(10))
        elif profile_type == ProfileType.EXPONENTIAL:
            intensity = 255 * (np.exp(nd * 2.5) - 1) / (np.exp(2.5) - 1)
        else:
            intensity = nd * 255
        intensity = cv2.GaussianBlur(intensity, (3, 3), 0)
```

**File:** normalmap_generator/processor.py (L46-60)
```python
    def generate_normal_map(self, height_map: np.ndarray, strength=1.0, normal_map_type: NormalMapType = NormalMapType.DX) -> np.ndarray:
        padded = cv2.copyMakeBorder(height_map, 1, 1, 1, 1, cv2.BORDER_REPLICATE)
        sx = cv2.Sobel(padded, cv2.CV_32F, 1, 0, ksize=3)[1:-1, 1:-1]
        sy = cv2.Sobel(padded, cv2.CV_32F, 0, 1, ksize=3)[1:-1, 1:-1]
        sx = sx * strength / 255.0
        sy = sy * strength / 255.0
        sz = np.sqrt(1 - np.clip(sx**2 + sy**2, 0, 1))
        normal = np.zeros((height_map.shape[0], height_map.shape[1], 3), dtype=np.uint8)
        normal[:, :, 0] = np.clip(127.5 + sz * 127.5, 0, 255).astype(np.uint8)
        normal[:, :, 2] = np.clip(127.5 - sx * 127.5, 0, 255).astype(np.uint8)
        if normal_map_type == NormalMapType.DX:
            normal[:, :, 1] = np.clip(127.5 - sy * 127.5, 0, 255).astype(np.uint8)
        else:
            normal[:, :, 1] = np.clip(127.5 + sy * 127.5, 0, 255).astype(np.uint8)
        return normal
```

**File:** normalmap_generator/processor.py (L69-73)
```python
        if disable_blurring:
            height_map = 255 - mask_img if invert_mask else mask_img
        else:
            base_mask = 255 - mask_img if invert_mask else mask_img
            height_map = cv2.min(base_mask, blurred)
```

**File:** main.py (L178-180)
```python
            pil_image = Image.open(file_path).convert("L")
            pil_resized = pil_image.resize((512, 512), Image.LANCZOS)
            mask_img = np.array(pil_resized)
```
