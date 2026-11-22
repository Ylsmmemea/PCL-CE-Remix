# Liquid Glass 参考实现对比分析

对四个参考项目的 Liquid Glass 渲染实现做逐项拆解，并与本项目当前的
`LiquidGlass.hlsl` / `LiquidGlassEffect.cs` / `LiquidGlassAnimator.cs` / `BlurBorder.cs` 对照。

分析结论全部来自源码，不做推测。

---

## 参考项目清单

| 项目 | 技术栈 | 可读性 | 说明 |
|---|---|---|---|
| `dragosniamtu/WPF-Liquid-Glass-Effect` | WPF + PS 3.0 | 仅编译产物 | 与本文档技术栈**完全一致**。无 HLSL 源码，本文档通过 `D3DDisassemble` 反汇编 `.ps` 得到算法 |
| `b3s3da/LiqiudGlassHLSL` | D3D11 + HLSL | 完整源码 | `OverShifted/LiquidGlass` GLSL 的 HLSL 移植。`LiquidGlassPS.hlsl` + `BlurPS.hlsl` |
| `ybouane/liquidglass` | WebGL 1 | 完整源码 | 功能最完整。`src/shaders.ts` 含 SDF / 倒角 / 菲涅尔 / Blinn-Phong 全套 |
| `apkipa/WUILiquidGlassDemo` | WinUI2/3 + C++ | 源码 + 文档 | 主要是管线设计文档，未提供玻璃着色器本体 |

**共同结论**：三者中提供源码/可反汇编的两个（LiqiudGlassHLSL、ybouane）加上 WPF 样例，
**折射模型完全一致** —— 都基于「圆角矩形 SDF + 圆形倒角高度场 + 径向采样偏移」。
这构成了 Liquid Glass 与「模糊 + 半透明白底」的真正分界线。

---

## 一、逐项对比

### A. Background capture（背景捕获）

**本项目**
`BackgroundPresenter` 在 `OnRender` 时反射遍历父级视觉树（`_drawingContent` / `_content` /
`_offset` 私有字段），把兄弟节点的绘制内容画进一个 `DrawingVisual`，再把 `Effect` 挂在它上面。
逐元素、实时、无需截屏。

**参考实现**
- LiqiudGlassHLSL：把交换链/窗口内容渲染进 `BackgroundTexture`，另有 `BlurredTexture`
- ybouane：用 `html-to-image` 把兄弟 DOM 元素序列化成 canvas，再上传为纹理
- WPF 样例：`ScreenCaptureHelper` 用 GDI 截取整个桌面，按窗口矩形裁剪（截屏时需短暂隐藏窗口以免拍到自己）

**哪个更合理**
**本项目的方案更优。** 参考实现要么依赖截屏（WPF 样例的窗口隐藏 hack 很脆弱），
要么依赖 DOM 序列化。`BackgroundPresenter` 是活的、逐元素的、不受窗口遮挡影响。

**可移植性** 无需改动。

---

### B. Coordinate system（坐标系统）

**本项目**
`uv ∈ [0,1]`，元素局部、**角落为原点**。`texel = 1/TextureSize`。

**参考实现**
三者都在**以中心为原点的像素/归一化空间**中工作：
- LiqiudGlassHLSL：`p = (uv - 0.5) * 2.0` → `[-1, 1]`
- ybouane：`v_localPx = a_pos * total` → 相对面板中心的像素偏移
- WPF 样例：`v0 * TextureSize - GlassCenter` → 像素空间中心坐标

**哪个更合理**
参考实现。倒角/透镜模型需要「从中心指向外的径向方向」以及「到边缘的距离」，
中心原点系天然表达这两者；角落原点系每次都要先转换。

**可移植性** ✅ 完全可移植，`TextureSize` 已经传给着色器，一次减法即可。

---

### C. Blur（模糊）

**本项目**
玻璃着色器内联 9 抽样高斯，**共 27 次纹理采样**（9 抽样 × RGB 三通道色散）。
半径 `radius = BlurAmount * lerp(0.2, 1.0, mask)` —— **中心清晰、边缘最模糊**。

**参考实现**
- LiqiudGlassHLSL：**独立 pass**，13 抽样可分离高斯（H + V 两趟），`blurDownscaleFactor = 0.5`
- ybouane：**独立 pass**，9 抽样可分离高斯，`BLUR_ITERATIONS = 6` 趟，降采样。
  玻璃着色器同时采样 `u_bgTex`（清晰）与 `u_blurTex`（模糊），
  按 `mix(sharp, blur, 1.0 - edge * 0.15)` 混合 —— **中心用模糊样本，边缘混向清晰样本**
- WPF 样例：内联 taps，半径 `(1 - (1-t1)*0.5) * BlurIntensity` —— **中心强、边缘弱**

**哪个更合理**
**参考实现，而且我们方向反了。** 两个独立参考实现一致地采用「中心模糊、边缘锐利」：
苹果的玻璃是**磨砂主体 + 通透折射边缘**。我们在折射最强的边缘同时施加最强的模糊，
把折射出来的内容又糊掉了 —— 这是「看起来像普通毛玻璃」最直接的原因。

**可移植性**
- 模糊梯度方向反转 ✅ 一行改动
- 独立 blur pass / 降采样 / 多趟迭代 ❌ **不可移植**。WPF `ShaderEffect` 无法串联
  render target，一次 `Effect` 只有一次 pass。这是本项目 27 次采样开销的根源。

---

### D. Refraction（折射）

**本项目**
```
disp = normal * (R * bend) * texel
```
其中 `normal` 是**硬阶跃的 ±X / ±Y 四方向**，由「最近的是哪条边」决定：

```hlsl
float2 normal = (toEdgePx.x < toEdgePx.y)
    ? float2(sign(0.5 - uv.x), 0.0)
    : float2(0.0, sign(0.5 - uv.y));
```

沿一条边 `normal` 是常量，在四条对角线处**不连续跳变**。

**参考实现**
- LiqiudGlassHLSL：`sampleP = p * pow(refractionFunc(dist), u_fPower)` —— **径向缩放**，
  根本不做位移，而是把整个位置向量按比例拉向中心
- ybouane：由**真实表面法线**驱动
  ```glsl
  vec2 hGrad = vec2(hR - hL, hU - hD) / (2.0 * e);   // 高度场数值微分
  vec3 N = normalize(vec3(-hGrad, 1.0));
  float refrPow = 1.0 - 1.0 / ior;                    // ior = 1.5，真实 Snell 系数
  refrPx = (exitRefr + entryRefr + throughRefr) * u_refract * 30.0;
  ```
  再加径向项 `centerDir * u_refract * 4.0 * depth`
- WPF 样例：`samplePos = pixelPos - normalize(centered) * bevel * 60` —— **径向**，
  由倒角高度驱动

**哪个更合理**
参考实现。我们的 `normal` 是「平板面片法线」，物理上相当于一块**没有曲面、只有四条斜边**的玻璃；
参考实现用的是**连续曲面法线**。后果：
1. 沿一条边折射方向恒定 → 内容不会被「绕」向角落
2. 对角线处方向突变 → 角落出现方向断裂
3. 高光无法沿边缘变化（见 I）

**可移植性** ✅ 完全可移植。圆角矩形 SDF 的梯度可由 4 次额外 SDF 求值得到（约 24 条 ALU），
或用解析式。PS 3.0 预算足够。

---

### E. Edge distance / SDF（边缘距离场）

**本项目**
```hlsl
float2 toEdgePx = min(uv, 1.0 - uv) / texel;
float  distPx   = min(toEdgePx.x, toEdgePx.y);
```
**纯矩形**距离场，**不感知圆角**。

**参考实现**
- LiqiudGlassHLSL：`sdSuperellipse(p, n, r)` 超椭圆（squircle）SDF，`n = 2.0`
- ybouane：圆角矩形 SDF
  ```glsl
  float rrSDF(vec2 p, vec2 b, float r) {
      vec2 q = abs(p) - b + vec2(r);
      return min(max(q.x, q.y), 0.0) + length(max(q, vec2(0.0))) - r;
  }
  ```
- WPF 样例：反汇编确认同样使用圆角矩形 SDF（corner radius 硬编码 16）

**哪个更合理**
参考实现。UI 元素本身是圆角的，用矩形距离场会导致：
- 折射带在四角无法贴合真实边缘
- `distPx` 沿对角线取到的是「到最近边的距离」，与实际到圆角轮廓的距离不符

**可移植性** ✅ 圆角矩形 SDF 约 6 条 ALU，成本极低。

---

### F. Lensing（透镜衰减曲线）—— **最关键的一项**

**本项目**
```hlsl
float t = saturate(distPx / thickness);
float mask = 1.0 - (t * t * (3.0 - 2.0 * t));   // smoothstep
float bend = mask * mask;
```
即 `bend = (1 - smoothstep(0, T, d))²`，一条对称的 S 形软曲线。

**参考实现**
全部基于**圆形剖面**：
- ybouane：
  ```glsl
  float bevelHeight(float d, float zR) {
      if (d <= 0.0) return 0.0;
      if (d >= zR) return zR;
      return sqrt(d * (2.0 * zR - d));      // 圆方程：z = sqrt(d(2R-d))
  }
  ```
- WPF 样例（反汇编还原）：
  ```
  t1 = saturate(depth / minSize * 3.3333)     ; 倒角区 = 外侧约 30% 面板
  bevel = 1 - sqrt(1 - (1 - t1)^2)            ; 同一圆剖面
  ```
- LiqiudGlassHLSL：指数曲线 `1 - b * (c*e)^(-d*D - a)`，
  实测参数 `a=0.45, b=2.3, c=3.5, d=3.3, fPower=1.7`

**为什么这决定了观感**

两条曲线的**梯度峰值位置**完全不同：

| 到边缘距离 | 本项目 `bend` | 圆形倒角 `bevel` |
|---|---|---|
| 0（边缘） | 1.00（梯度 **0**） | 1.00（梯度 **∞**） |
| 20% 带宽 | 0.80 | 0.40 |
| 50% 带宽 | 0.25（梯度**最大**） | 0.13 |
| 100% 带宽 | 0.00 | 0.00 |

- **本项目**：导数在边缘为 0，在带宽中点最大 → 位移是一条**宽而软的鼓包**，
  整个折射带都在缓慢变形 → 观感是「柔和的扭曲/水波」
- **参考实现**：导数在边缘趋于无穷，向内迅速衰减 → 位移是**极窄而强烈的边缘压缩**，
  折叠被压缩到最外侧几个像素内 → 观感是「干净的玻璃透镜边缘」

**这是「像毛玻璃」与「像液态玻璃」的分水岭。**
两者在 20% 带宽处的绝对位移可以相等，但梯度分布决定了它是「软」还是「锐」。

**可移植性** ✅ `sqrt` / `pow` 都是 PS 3.0 内建函数。

---

### G. Chromatic aberration（色散）

**本项目**
`col.r/g/b` 分别在 `uv + disp*1.03 / disp*1.00 / disp*0.97` 处采样。
随位移量成比例，中心位移为 0 时色散自然消失。

**参考实现**
- ybouane：由**表面法线**驱动，且随 `edge` 因子缩放
  ```glsl
  float caS = u_chroma * 18.0 * (edge * 0.7 + 0.3) * 2.0;
  vec2 caD = N.xy * caS * pxToUV;
  ```
  默认 `chromAberration = 0.05`
- LiqiudGlassHLSL：无
- WPF 样例：无

**哪个更合理**
思路接近，我们的「随位移衰减」与它们的「随边缘/法线衰减」目标一致。
但用 `N.xy` 能保证色散方向与表面倾斜方向一致，边缘处的横向色边更干净。

**可移植性** ✅ 已经有 `disp`，改成用法线梯度即可。

---

### H. Fresnel（菲涅尔）

**本项目** —— **完全没有。**

**参考实现**
- ybouane：
  ```glsl
  float fres = pow(1.0 - abs(N.z), 4.0) * u_fresnel;   // 默认 u_fresnel = 1.00
  float envRefl = (N.y * 0.5 + 0.5) * fres * 0.08;
  fin = mix(fin, vec3(1.0), fres * 0.2);
  ```
- 其余两个参考无

**哪个更合理**
参考实现。菲涅尔是「材质感」的核心 —— 它让掠射角（边缘）反射增强，
是玻璃区别于半透明色块的关键物理特征。没有它，边缘只是「被加亮」而不是「在反射」。

**可移植性** ✅ 需要真实法线（依赖 D/F 的重构），本身只是 `pow`。

---

### I. Specular（高光）

**本项目**
```hlsl
float2 outward = -normal;                       // 四方向常量
float nDotL = dot(outward, normalize(float2(-0.55, -0.835)));
float spec = pow(saturate(nDotL * 0.5 + 0.5), 3.0);
col += pow(mask, 4.0) * Glare * GlareGain * spec;
```
因为 `normal` 沿每条边是常量，`nDotL` 沿整条边也是常量 →
**每条边均匀发亮，呈「描边」状**，无法产生沿边缘游走的高光。

**参考实现**
- ybouane：4 光源 Blinn-Phong，使用真实法线
  ```glsl
  vec3 H1 = normalize(L1 + V);
  float sp1 = pow(max(dot(N, H1), 0.0), 90.0);
  float sp4 = pow(max(dot(N, H4), 0.0), 120.0) * 0.6;
  ```
  幂次 90 / 50 / 6 / 120 —— 高而窄

**哪个更合理**
参考实现。真实法线随位置连续变化，高光才能沿边缘形成游走的亮弧，
并在圆角处自然收束。**这是「精致感」的主要来源之一。**

**可移植性** ✅ 依赖真实法线。Blinn-Phong 本身只是几条 `dot` + `pow`。

---

### J. Edge highlight（边缘高光/内描边）

**本项目**
单一加法项 `pow(mask, 4.0) * Glare * GlareGain * spec`。

**参考实现**
ybouane 分三层独立叠加：
```glsl
float innerStroke = smoothstep(-2.5, -1.5, sdf) * (1.0 - smoothstep(-1.0, 0.0, sdf));
float topBias = 0.5 + 0.5 * (-v_localPx.y / half_.y);      // 顶部更亮
innerStroke *= (0.4 + 0.6 * topBias);
float rim = edge * u_edgeHL * 0.22;
float innerGlow = smoothstep(5.0, 0.0, -sdf) * u_edgeHL * 0.15;
```
1. 最外圈 1.5px 的**内描边**（带顶部偏置，模拟天光）
2. 稍宽的 **rim**
3. 更宽的 **inner glow**

**哪个更合理**
参考实现。三层不同尺度的叠加产生了「厚度」层次。
我们只有一层，且强度被 `GlareGain = 0.32` 压低。

**可移植性** ✅ 纯数学，成本很低。

---

### K. Noise / distortion（噪声/扰动）

**本项目**
双层 value noise，由 `Time` 逐帧驱动，位移沿法线方向。
默认 `LiquidNoiseStrength = 30`（映射后约 6px），**持续流动**。

**参考实现**
- ybouane：`u_distort` —— **默认 `0.00`**。逐像素 hash 抖动，**无时间项**
- LiqiudGlassHLSL：`u_noise` —— **默认 `0.000`**。同样无时间项
- WPF 样例：无

**哪个更合理**
参考实现。**两个独立参考实现都把噪声默认关闭，且完全没有时间动画。**
我们的持续流动噪声是自行引入的「水波/果冻」来源；更严重的是，
它叠加在折射位移上，而边缘处梯度已经接近单调性上限，噪声会直接触发折叠。

**可移植性** ✅ 保留参数、把默认值改为 0 即可。

---

### L. Tint（色调）

**本项目**
`Tint` DP 存在但 `Tint.a = 0`（未启用），实际色调由 WPF 层的
`GlassBackground`（alpha 0.14 的浅色刷）叠在着色器输出**之上**实现。

**参考实现**
- ybouane：
  ```glsl
  col = mix(col, col * vec3(0.92, 0.95, 1.05), u_tint);   // 乘法染色，默认 0
  col *= 1.0 + 0.06 * depth;                              // 厚度感
  ```

**哪个更合理**
参考实现。乘法染色保留背景的明暗结构，是「给玻璃染色」；
`lerp` 向纯色靠拢是「盖一层色块」—— 后者正是「彩色塑料感」的来源。

**可移植性** ✅ 只是把 `lerp` 换成 `*`。

---

### M. Alpha compositing（透明度合成）

**本项目**
着色器 `return float4(col, Opacity)`，`Opacity` 恒为 1。之后由 WPF 层的
`GlassBorder.base.OnRender` 把 `GlassBackground`（alpha 0.14）画在**输出之上**，
`MyCard` 则画在玻璃**之下**（`AnimatedGrid` 背景）。

**参考实现**
ybouane：
```glsl
gl_FragColor = vec4(fin, mask * u_alpha);
```
`alpha = ` 抗锯齿后的 SDF 面板遮罩，且所有高光/菲涅尔都是**先加进 RGB**、
再由遮罩统一决定覆盖范围。这是标准的预乘合成，边缘自带 1.5px AA。

**哪个更合理**
参考实现。我们有两个问题：
1. **叠在着色器之上的 14% 浅色层**直接把折射内容的对比度压低 14%，
   边缘本来最需要通透的地方被蒙了一层
2. 硬编码 `alpha = 1` 丢失了 SDF 抗锯齿能力

**可移植性** ✅ alpha 输出到遮罩是可行的；`GlassBorder` 的覆盖层需要产品决策。

---

### N. Hover state（悬停）

**本项目** `MyCard` 动画改变文字/图标/阴影颜色，玻璃本身不变。

**参考实现** ybouane 在配置里提供 `button` / `floating` 标志，由 JS 侧处理位移和缩放，着色器不参与。

**哪个更合理** 等价，非着色器职责。

---

### O. Animation（动画）

**本项目** `LiquidGlassAnimator` 用 `DispatcherTimer` 逐帧写入 `Time`，驱动噪声流动。

**参考实现**
- ybouane：**着色器完全静态，无 `Time` uniform**
- LiqiudGlassHLSL：**完全静态**
- WPF 样例：静态

**哪个更合理**
参考实现。三者无一有时间动画。Liquid Glass 的「液态」指的是**光学上的透镜质感**，
不是字面意义的流动。我们的流动噪声是观感走样的主要来源之一。

**可移植性** ✅ 保留 `LiquidGlassAnimator` 基础设施（它是好东西），
把默认噪声强度设为 0 即可默认静止。

---

## 二、为什么我们的效果像毛玻璃 / 果冻 / 水波

按影响程度排序，全部有代码依据：

### 1. 模糊梯度方向反了（影响最大）

`radius = BlurAmount * lerp(0.2, 1.0, mask)` —— 边缘模糊最强。
而 `LiqiudGlassHLSL` 与 WPF 样例都一致采用**边缘更锐利**（ybouane 明确注释
"the rim blends toward the sharp sample so refraction edges stay crisp"）。

**后果**：折射最强的位置恰好也是模糊最强处，折射出的内容被立刻糊掉。
背景在边缘的「弯折」被完全抹平，只剩下一圈柔和的明暗过渡 —— 这正是普通毛玻璃的特征。

### 2. 透镜曲线是软 S 形，梯度峰值在带宽中点

`bend = (1 - smoothstep(0, T, d))²` 的导数在边缘为 0、在中点最大。
参考实现的圆剖面 `sqrt(d(2R-d))` / `1-sqrt(1-(1-t)²)` 导数在边缘趋于无穷。

**后果**：我们的位移是覆盖整个折射带的宽软鼓包 → 观感是「整块在缓慢变形」= 水波/果冻；
参考实现是压缩在最外侧数像素内的强压缩 → 观感是「干净的透镜边缘」。

### 3. 法线是硬阶跃的四方向常量

`normal` 由「最近边」决定，沿边恒定、对角线突变。

**后果**：
- 内容不会被绕向角落（缺少环形弯折）
- 高光呈均匀描边而非沿边缘游走
- 无法计算菲涅尔

### 4. 持续流动噪声

两个参考实现噪声默认 **0.0** 且无时间项。我们的默认 6px 流动噪声
既引入字面意义上的「流动」，又在边缘叠加到已接近单调性上限的位移上，放大折叠重影。

### 5. 叠加在着色器之上的 14% 浅色层

`GlassBorder` 把 `GlassBackground` 画在着色器输出之上，直接降低折射内容对比度 14%。
`MyCard` 的等价层画在玻璃之下（`AnimatedGrid` 背景），因此**卡片反而比导航栏更通透** ——
这与「左侧菜单/导航栏看起来更糊」的观察一致。

### 6. 矩形距离场而非圆角 SDF

折射带无法贴合 UI 元素实际的圆角，角落处距离计算失真。

---

## 三、PS 3.0 可移植性总结

| 项 | 可移植 | 说明 |
|---|---|---|
| B 中心原点坐标 | ✅ | 一次减法 |
| C 模糊方向反转 | ✅ | 改 `lerp` 系数 |
| C 独立 blur pass | ❌ | WPF `ShaderEffect` 无法串联 render target |
| D 真实法线驱动折射 | ✅ | 4 次额外 SDF 求值，约 24 ALU |
| E 圆角矩形 SDF | ✅ | 约 6 ALU |
| F 圆形倒角剖面 | ✅ | `sqrt` 是 PS 3.0 内建 |
| G 色散由法线驱动 | ✅ | 已有 `disp` |
| H 菲涅尔 | ✅ | 依赖真实法线 |
| I Blinn-Phong 高光 | ✅ | 几条 `dot` + `pow` |
| J 三层边缘高光 | ✅ | 纯数学 |
| K 关闭流动噪声 | ✅ | 改默认值 |
| L 乘法染色 | ✅ | `lerp` → `*` |
| M 遮罩 alpha | ⚠️ | 着色器侧可行；WPF 覆盖层需产品决策 |

**PS 3.0 硬约束**
- 指令上限 512 条。当前着色器 **290 行汇编 / 27 次 `texld`**
- `texld` 是最贵的资源。参考实现玻璃着色器只需 1~6 次采样，因为模糊在独立 pass；
  我们因无法分 pass，把 9 抽样 × 3 通道色散全部内联 = 27 次
- 增加 SDF 法线（+4 次 SDF 求值）+ 菲涅尔 + Blinn-Phong 后，
  指令数预计 +60~80 条，仍在预算内；但**若要同时降低采样数需重新设计色散**

---

## 四、重构方向（待确认，暂不实施）

按「投入产出比 × 观感影响」排序，最小改动集：

1. **模糊梯度反转**（C）—— 一处系数，影响最大
2. **透镜曲线换为圆剖面**（F）—— 一处公式，决定「锐 vs 软」
3. **圆角矩形 SDF + 真实法线**（D/E/H/I/G）—— 一次性解决折射方向、角落、
   高光形态、菲涅尔、色散方向五个问题
4. **噪声默认关闭**（K）—— 一处默认值
5. **色调改乘法**（L）
6. `GlassBorder` 覆盖层（M）—— 需产品决策：是否保留这 14% 浅色

不改动：`BackgroundPresenter`（优于参考实现）、`LiquidGlassAnimator`（基础设施保留）、
寄存器布局（C0–C9 与 C7 的 `Point` 约束）、`BlurBorder` 的整体架构。
