// LiquidGlass.hlsl - 液态玻璃像素着色器
//
// 把完整的效果链写进单个 PS 3.0 着色器（WPF 的 ShaderEffect 不支持效果图组合，
// 因此模糊 → 置换 → 遮罩 → 高光 必须在一次 pass 内完成）：
//
//   采样背景 → 高斯模糊打底
//            → 沿边缘法线置换采样坐标（折射）
//            → 边缘遮罩淡出（中心清晰、边缘扭曲）
//            → 边缘高光与轻微色散 → 色调/透明度
//
// 编译：见同目录 ShaderCompiler/（P/Invoke d3dcompiler_47.dll，产出 LiquidGlass.ps）
//
// 四条必须遵守的约定：
//
// 1. 位移方向必须朝向面板中心。若朝外，采样坐标会越过元素边界并触发钳制，
//    位移被抵消为零，折射看起来「完全没做」。
//
// 2. 所有 tex2D 坐标都必须钳制在纹素中心范围内。WPF 传入的输入纹理在元素边界之外
//    是透明黑，九抽样模糊的外侧抽样一旦越界，边缘就会整圈变暗形成黑边。
//    由 SampleClamped 统一保证，调用方无需自行夹紧。
//
// 3. 长度量以「像素」为单位并按面板尺寸缩放。面板尺寸差异极大
//    （导航栏约 48px 高、卡片可达 600px 高），固定像素值在细长面板上会过冲。
//
// 4. 折射位移量必须小于折射带宽（通常取 0.6 倍）。折射会让靠近边缘的采样点向
//    中心移动，位移超过带宽后该映射不再单调，边缘会出现镜像重影 —— 观感上就是
//    「水波 / 果冻」。判据见 PixelShaderFunction 中 maxShift 处的推导。
//
// 5. 输出 alpha 必须跟随输入 alpha。输入纹理里未被绘制的区域是透明的，
//    若一律输出不透明，这些区域会变成纯黑 —— 表现为卡片边缘出现黑灰色块，
//    滚动时尤其明显（采集裁剪与祖先重绘在单帧内未必严丝合缝）。

sampler2D InputTexture : register(S0);

float  BlurRadius    : register(C0); // 模糊半径（像素）
float  Refraction    : register(C1); // 折射强度：边缘处沿法线的最大位移（像素）
float  RefThickness  : register(C2); // 边缘影响厚度：从面板边缘向内延伸的距离（像素）
float  Glare         : register(C3); // 边缘高光强度
// 注意：宿主用 Point 类型传递尺寸，它只占一个常量寄存器（填在 .x/.y），
// 因此这里必须声明为 float2 而不是两个独立的 float 寄存器，否则后续参数会整体错位。
float2 TextureSize   : register(C4); // .x = 宽, .y = 高（像素）
float4 Tint          : register(C5); // 色调叠加（rgb 为颜色，a 为混合量）
float  Opacity       : register(C6); // 整体不透明度
float  CornerRadius  : register(C7); // 面板圆角半径（像素），用于圆角矩形 SDF

// 尺寸缩放的参考短边。短边小于它时按比例缩小效果尺度，避免细长面板过冲。
static const float ScaleReference = 220.0;
static const float ScaleMin = 0.30;

// 折射带宽的绝对基准（像素）。带宽 = 本值 + 短边 × 10% × 滑块。取绝对值而非纯比例，
// 是为了让透镜带在各种尺寸上都维持在「玻璃边缘」的尺度上（约 15~35px），
// 而不是随面板放大成整块弯折。细长面板也靠它兜底：48px 高的导航栏不会只剩 8px 带宽。
static const float BandFloorPx = 14.0;

// 高光增益。Glare 取 1.0 时若直接叠加会把浅色背景推到纯白，
// 因此压低整体增益，让滑块全量程都落在可用范围。
static const float GlareGain = 0.32;

// ---------------------------------------------------------------------------
// 采样
// ---------------------------------------------------------------------------

/// <summary>
///     钳制到纹素中心后取样。<br/>
///     元素边界之外是透明黑，直接采样会让边缘整圈变暗；钳制到最靠近边界的纹素中心，
///     等价于 D3D 的 Clamp 寻址，可彻底消除黑边。
/// </summary>
float4 SampleClamped(float2 uv, float2 texel)
{
    float2 halfTexel = texel * 0.5;
    float2 p = clamp(uv, halfTexel, 1.0 - halfTexel);

    // 用 tex2Dlod 显式指定 LOD，而不是 tex2D。
    //
    // ps_3_0 下编译器会给 tex2D 的纹理坐标自动加 _pp（部分精度）提示，而 WPF 把效果
    // 渲染在一个由两个三角形拼成的四边形上 —— 两个三角形在共享边上的降精度插值会有
    // 微小出入。出入本身小于一个纹素，但采样位置在共享边两侧因此错开，元素正中就会
    // 出现一条 1 像素的竖线（普通毛玻璃的 16px 模糊盖得住，液态玻璃只有 2~3px）。
    // tex2Dlod 不做隐式求导、坐标走全精度，这条缝随之消失。
    return tex2Dlod(InputTexture, float4(p, 0.0, 0.0));
}

/// <summary>磁盘均匀采样近似高斯模糊（16 抽样，见下方常量表）。</summary>
float4 SampleBlurred(float2 uv, float2 texel, float radius)
{
    // 早期用「同心圆环 + 45° 等分」的规则点阵（1 圈 8 点，后来加到 2 圈）。
    // 那种排布本身构成一个正方形点阵，透过它看背景就是**一片菱形格子**，
    // 加点数只能让格子变密，去不掉格子本身 —— 规则性才是问题。
    //
    // 改成黄金角螺旋：径向按 sqrt 分布（面积均匀），方位角每步转 137.5°。
    // 这两个条件合起来使采样点既不共线也不成环，核函数是 Epanechnikov 抛物线，
    // 是同类里最平滑的一档，观感就是均匀磨砂而不是图案。
    //
    // 16 个点由 (i + 0.5) / 16 离线算好，权重为 1 - t（和为 8，末尾统一乘 0.125）。
    float2 s = texel * max(radius, 0.0);

    float4 c = SampleClamped(uv + float2(0.1443, 0.0000) * s, texel) * 0.9792;
    c += SampleClamped(uv + float2(-0.1843, 0.1689) * s, texel) * 0.9375;
    c += SampleClamped(uv + float2(0.0282, -0.3215) * s, texel) * 0.8958;
    c += SampleClamped(uv + float2(0.2324, 0.3031) * s, texel) * 0.8542;
    c += SampleClamped(uv + float2(-0.4264, -0.0754) * s, texel) * 0.8125;
    c += SampleClamped(uv + float2(0.4039, -0.2569) * s, texel) * 0.7708;
    c += SampleClamped(uv + float2(-0.1351, 0.5026) * s, texel) * 0.7292;
    c += SampleClamped(uv + float2(-0.2577, -0.4961) * s, texel) * 0.6875;
    c += SampleClamped(uv + float2(0.5590, 0.2041) * s, texel) * 0.6458;
    c += SampleClamped(uv + float2(-0.5816, 0.2401) * s, texel) * 0.6042;
    c += SampleClamped(uv + float2(0.2803, -0.5991) * s, texel) * 0.5625;
    c += SampleClamped(uv + float2(0.2072, 0.6605) * s, texel) * 0.5208;
    c += SampleClamped(uv + float2(-0.6244, -0.3619) * s, texel) * 0.4792;
    c += SampleClamped(uv + float2(0.7325, -0.1610) * s, texel) * 0.4375;
    c += SampleClamped(uv + float2(-0.4470, 0.6359) * s, texel) * 0.3958;
    c += SampleClamped(uv + float2(-0.1033, -0.7970) * s, texel) * 0.3542;
    c += SampleClamped(uv + float2(0.6340, 0.5343) * s, texel) * 0.3125;
    c += SampleClamped(uv + float2(-0.8532, 0.0353) * s, texel) * 0.2708;
    c += SampleClamped(uv + float2(0.6223, -0.6193) * s, texel) * 0.2292;
    c += SampleClamped(uv + float2(-0.0416, 0.9004) * s, texel) * 0.1875;
    c += SampleClamped(uv + float2(-0.5922, -0.7096) * s, texel) * 0.1458;
    c += SampleClamped(uv + float2(0.9380, 0.1262) * s, texel) * 0.1042;
    c += SampleClamped(uv + float2(-0.7948, 0.5530) * s, texel) * 0.0625;
    c += SampleClamped(uv + float2(0.2172, -0.9654) * s, texel) * 0.0208;

    return c * 0.083333;
}

// ---------------------------------------------------------------------------
// 形状
// ---------------------------------------------------------------------------

/// <summary>
///     圆角矩形 SDF，返回「向内深度」：面板边缘处为 0，向内为正，外部为负。<br/>
///     相比纯矩形距离场，它让折射带贴合控件真实的圆角轮廓，
///     并且其梯度是一个**连续方向场** —— 沿圆角自然旋转，四角不会出现方向突变。
/// </summary>
float RoundedDepth(float2 p, float2 halfSize, float radius)
{
    float2 q = abs(p) - halfSize + radius;
    return -(min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius);
}

/// <summary>
///     圆形倒角剖面：边缘（depth=0）为 1，向内 depth=thickness 处为 0。<br/>
///     等价于圆方程 z = sqrt(d(2R-d)) 的归一化形式。梯度在边缘趋于无穷、向内迅速衰减，
///     因此压缩集中在最外侧几个像素内 —— 读作「透镜边缘」，而不是「把图片拉长」。
/// </summary>
float BevelHeight(float depth, float thickness)
{
    float t = saturate(depth / thickness);
    return 1.0 - sqrt(max(0.0, 2.0 * t - t * t));
}

// ---------------------------------------------------------------------------
// 主入口
// ---------------------------------------------------------------------------

float4 PixelShaderFunction(float2 uv : TEXCOORD) : COLOR
{
    float2 texel = 1.0 / max(TextureSize, float2(1.0, 1.0));
    float  minDim = min(TextureSize.x, TextureSize.y);

    // 面板尺寸补偿：细长面板按比例收窄效果尺度，避免边缘扭曲吞掉整个元素
    float sizeScale = max(ScaleMin, min(1.0, minDim / ScaleReference));

    // --- 圆角矩形 SDF -----------------------------------------------------
    // 用圆角矩形距离场而非矩形距离：折射带必须贴合控件真实的圆角轮廓，
    // 而且它的梯度天然给出一个连续的表面法线方向场（见下）。
    float2 halfSize = TextureSize * 0.5;
    float2 p        = (uv - 0.5) * TextureSize;               // 像素坐标，原点在面板中心
    float  cornerR  = min(max(CornerRadius, 0.0), min(halfSize.x, halfSize.y));
    float  distPx   = RoundedDepth(p, halfSize, cornerR);     // 边缘 0，向内为正

    // 折射带宽按面板短边取比例，而不是固定像素。
    // 固定 50px 在 400px 的卡片上只覆盖 12%，透镜效应无从体现 ——
    // 参考实现的透镜范围占面板相当大的比例（其 Width/Height 默认为 0.6）。
    //
    // 注意不要再乘 sizeScale：带宽已经正比于 minDim，再叠一层「小面板缩小」
    // 等于对矮面板二次惩罚 —— 实测 684x77 的卡片位移只剩 2.1px，完全看不出效果，
    // 表现为「只有部分卡片有折射」。
    //
    // 但纯比例还有第二个问题：它把大面板整块折一遍。侧边栏 300px 在滑块 50 时
    // 得到 52.5px 带宽、23px 位移（短边的 8%），再加径向项合计约 13% ——
    // 实测观感是「壁纸被拉长了一块」而不是玻璃边缘的透镜。
    //
    // 因此改成 绝对基准 + 很小的尺寸项：尺寸项满量程也只占短边 10%，
    // 弯曲集中在最外圈二十几像素内 —— 这才是「边缘透镜」而不是「整块弯折」。
    // 绝对基准同时修好细长面板：导航栏 864x48 从 8.4px 提到 16px，
    // 否则 3.7px 位移在低对比背景上完全读不出玻璃。
    // 上限取 0.42 而不是 0.5：上下两条带必须留出中间的平整区，不能相接。
    float bandMax = minDim * 0.42;
    float band = BandFloorPx + minDim * 0.10 * saturate(RefThickness / 100.0);
    float thickness = clamp(band, 1.0, bandMax);

    // mask：边缘处为 1、向内超过 thickness 像素后为 0（供模糊梯度与高光使用）
    float tm   = saturate(distPx / thickness);
    float mask = 1.0 - (tm * tm * (3.0 - 2.0 * tm)); // 手工 smoothstep，避免内建函数在边界输入下的未定义行为

    // --- 倒角高度场与表面法线 ---------------------------------------------
    // 在 p 及四个邻点求距离，一次拿到两样东西：
    //   · SDF 梯度  → 指向面板内部的单位向量（折射位移方向）
    //   · 高度场梯度 → 表面斜率，构造三维法线供 Fresnel / Specular 使用
    // 两者都是连续场：沿直边恒定、沿圆角平滑旋转，不会出现早期四方向硬阶跃法线的方向突跳。
    float  e  = 1.0;
    float  dR = RoundedDepth(p + float2(e, 0.0), halfSize, cornerR);
    float  dL = RoundedDepth(p - float2(e, 0.0), halfSize, cornerR);
    float  dU = RoundedDepth(p + float2(0.0, e), halfSize, cornerR);
    float  dD = RoundedDepth(p - float2(0.0, e), halfSize, cornerR);

    float bevel = BevelHeight(distPx, thickness);

    // --- 内向方向：连续混合，避开 SDF 的角平分线折痕 ----------------------
    // 直接对圆角矩形 SDF 求梯度会踩到一个坑：其 max(q.x, q.y) 沿角平分线存在
    // 梯度不连续 —— 数值本身连续，但**方向**会从「垂直于左右边」突变为
    // 「垂直于上下边」。而方向正是折射与高光的驱动项，于是对角线上会亮出一条
    // 明显的直线（实测卡片上该折痕可长达 90px）。
    //
    // 这里改用「按接近程度平滑混合」得到连续的内向方向，再沿它取高度场的方向导数。
    float2 toEdge = halfSize - abs(p);          // 到左右边 / 上下边的距离
    float2 nX = float2(-sign(p.x), 0.0);        // 左右边的内向法线
    float2 nY = float2(0.0, -sign(p.y));        // 上下边的内向法线

    // 在角平分线附近以 thickness 为宽度渐变，方向平滑旋转而非突跳。
    //
    // 权重必须用 toEdge.x - toEdge.y：它为正表示「离左右边更近」，此时该用左右法线 nX；
    // 为负表示离上下边更近，该用 nY。写成 toEdge.y - toEdge.x 会把两者弄反 ——
    // 于是靠近上下边的位置全都用了左右法线 nX = (-sign(p.x), 0)，而它在 p.x = 0 处正负翻转，
    // 采样位置瞬间跳变一个多像素，玻璃正中就出现一条竖线（元素正中，正是 sign 的翻转处）。
    // 这个翻转只靠加大模糊盖不住，因为跳的是采样位置而不是内容。
    float  wBlend = smoothstep(-thickness * 0.5, thickness * 0.5, toEdge.x - toEdge.y);
    float2 inward = normalize(lerp(nX, nY, wBlend) + float2(0.00001, 0.00001));

    // 沿 inward 的方向导数（每像素的高度变化），乘 z 半径换算成真实斜率。
    // z 半径必须按像素给出：BevelHeight 归一化在 0~1 却横跨 thickness 像素，
    // 直接当高度用斜率只有 ~0.1，法线几乎垂直，菲涅尔与高光会全部失效。
    float  zRadius = min(thickness * 0.4, 24.0);
    float  slope = ((BevelHeight(dR, thickness) - BevelHeight(dL, thickness)) * inward.x
                  + (BevelHeight(dU, thickness) - BevelHeight(dD, thickness)) * inward.y) * (0.5 / e);
    float2 hGrad = inward * (zRadius * slope);
    float3 N = normalize(float3(-hGrad, 1.0));

    // --- 折射位移（像素）--------------------------------------------------
    // 由两部分叠加：
    //
    // 1. 边缘法线分量 —— 把靠边的采样点推向内部，形成透镜边缘的压缩。
    //    强度按带宽比例给出并用满滑块行程；位移峰值受采样映射单调性限制
    //    （f(d) = d + R·bevel(d)，bevel 在边缘梯度无界，超出会让映射反向产生镜像重影）。
    //
    // 2. 径向分量 —— 把内容整体向面板中心轻微收拢。
    //    只有第 1 项时，压缩完全垂直于各条边、切向为 1:1，各向异性极强，
    //    视觉上读作「把图片拉长」。参考实现（ybouane 的 centerDir 项、WPF 样例的
    //    normalize(centered)）都带这个径向项，它让内容绕着玻璃连续弯折而非单向拉伸。
    // 取 0.62 而非 0.55：带宽收窄后需要更高的位移/带宽比才能维持透镜强度，
    // 上限是 0.667（单调性，即边缘出现镜像重影的临界点），留一点余量。
    float strength = saturate(Refraction / 150.0);
    float effRefraction = thickness * 0.62 * strength;

    // 径向项同样乘 mask 收敛在折射带内 —— 否则它会作用到整块面板，
    // 让中心区域也发生位移，破坏「中心保持平整」这一基本要求。
    float2 dispPx = inward * (effRefraction * bevel)
                  + (-p / max(halfSize, float2(1.0, 1.0))) * (thickness * 0.25 * strength) * mask;

    float2 disp   = dispPx * texel;
    float2 uvBase = uv + disp;

    // --- 采样 -------------------------------------------------------------
    // 中心模糊、边缘锐利 —— 梯度方向与早期实现相反。
    // 两个独立参考实现（LiqiudGlassHLSL、WPF 样例）一致采用「磨砂主体 + 通透折射边缘」：
    // 折射最强的位置必须保持清晰，否则弯折出来的内容会被随即糊掉，退化成普通毛玻璃。
    // 中心系数取 0.6 而非 1.0，避免中心一下子变成高倍模糊。
    // 下界 3px：采集纹理在元素正中会残留一列 1 像素的痕迹（普通毛玻璃的 16px 模糊
    // 能盖住，液态玻璃只有 2~3px 时它会显露为一条竖线）。3px 足以把它摊平到不可见。
    float radius = BlurRadius * lerp(0.6, 0.15, mask);

    // 只做一次采样，不再按通道做色散。
    //
    // 早期 R/G/B 各做一次完整模糊，三个采样中心分别取 disp 的 1.03 / 1.00 / 0.97 倍，
    // 本意是「厚玻璃边缘的色散」。但三者只差 3% 的位移量（不到 1 像素），效果微乎其微，
    // 代价却是三倍模糊开销；更糟的是它会产生一条可见的竖线 ——
    // 位移量随位置变化时，R 与 B/G 的采样位置会先后跨过纹素边界，两条通道就此错开一个像素。
    // 错开发生在位移量跨界的那个位置，正好落在元素中部，于是玻璃正中出现一条 1 像素的竖线
    // （普通毛玻璃的 16px 模糊盖得住，液态玻璃盖不住）。
    //
    // 色散本就是很弱的锦上添花，而这条竖线是实打实的缺陷，因此去掉。
    float4 sample = SampleBlurred(uvBase, texel, radius);
    float3 col = sample.rgb;

    // 输入纹理里未被绘制的区域是透明的。若一律输出不透明结果，这些区域会变成纯黑 ——
    // 表现为卡片边缘（尤其是位于窗口边缘、正在滚动的卡片）出现黑灰色的块。
    // 采集的裁剪与祖先重绘在滚动时未必与本次渲染严丝合缝，就可能留下这样的空洞。
    // 让输出 alpha 跟随输入 alpha：采样不到内容的地方保持透明，
    // 由该元素下方原本的绘制内容透出来，而不是盖上一块黑。
    float coverage = saturate(sample.a);

    // --- 边缘高光 ---------------------------------------------------------
    // 光源方向与幂次参考 ybouane 的实现：两个窄高光 + 一个宽泛的环境反射。
    // 因为 N 随位置连续变化，高光会沿边缘游走、在圆角自然收束，
    // 而不是像早期的四方向常量法线那样把每条边整条均匀点亮（读作「描边」）。
    // 光源方向：本着色器的坐标里 +y 朝下（p = (uv-0.5)*TextureSize，uv.y 向下增长），
    // 因此「左上方」是 (-x, -y)。直接照搬参考实现的 (0.4, 0.7, 1.0) 会变成右下方，
    // 左上边缘的法线与它方向相悖，dot 只有 0.58，无论怎么调都不可能亮。
    float3 V = float3(0.0, 0.0, 1.0);
    float3 L1 = normalize(float3(-0.7, -0.5, 0.7));
    float3 H1 = normalize(L1 + V);
    float3 L2 = normalize(float3(0.6, 0.8, 0.5));
    float3 H2 = normalize(L2 + V);

    // 幂次必须与法线的实际倾角范围匹配。倒角法线最多倾斜约 45°，
    // dot(N,H) 的上限约 0.91 —— pow(0.91, 90) ≈ 1e-4，高光在数学上永远不亮。
    // 参考实现用 90 是因为它的高度场跨度大得多（40px 高度跨 40px），不可照搬。
    float  sp  = pow(saturate(dot(N, H1)), 12.0)
               + pow(saturate(dot(N, H2)), 6.0) * 0.30;

    // --- 菲涅尔 -----------------------------------------------------------
    // 掠射角（边缘）反射增强，让玻璃读作「有反射的表面」而不是「被扭曲的图片」。
    // 这是材质识别的关键线索之一。
    float fres = pow(1.0 - abs(N.z), 2.5);

    // --- 边缘高光与内描边 -------------------------------------------------
    // 分三层：最外圈的细描边（带顶部偏置，模拟天光）、稍宽的 rim、以及最外几像素的收边。
    // 内描边：参考实现是 1.5px 的窄线且权重极低（约 0.03），只作为材质的一点暗示。
    // 早期这里用了 1.5px 硬线乘上远高于参考的权重，观感是一条「画上去的边框」——
    // 这正是「生硬」的来源。这里改成约 4px 的柔和渐变带并压低权重。
    float  sdf = -distPx;                                    // 内负外正
    float  innerStroke = smoothstep(-5.0, -1.5, sdf) * (1.0 - smoothstep(-1.5, 0.5, sdf));
    float  topBias = saturate(0.5 + 0.5 * (-p.y / max(halfSize.y, 1.0)));
    innerStroke *= 0.4 + 0.6 * topBias;
    float  rim = mask * mask;

    // 高光项集中在边缘：rim 与 fres 都用高次幂收敛，
    // 否则整体叠加会把玻璃抬成均匀白雾（Acrylic 观感）。
    col += Glare * GlareGain * (sp * 1.6 + pow(rim, 3.0) * 0.40 + innerStroke * 0.18);
    col = lerp(col, float3(1.0, 1.0, 1.0), fres * Glare * 0.30);

    // --- 色调与透明度 -----------------------------------------------------
    col = lerp(col, Tint.rgb, Tint.a);

    return float4(col, Opacity * coverage);
}
