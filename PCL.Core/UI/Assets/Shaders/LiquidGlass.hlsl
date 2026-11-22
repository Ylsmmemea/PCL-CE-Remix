// LiquidGlass.hlsl - 液态玻璃
//
// 整条效果链得塞进一个 PS 3.0 着色器里 WPF 的 ShaderEffect 不让拼效果图
// 模糊 置换 遮罩 高光 只能一次 pass 做完
//
// 编译走同目录的 ShaderCompiler 它 P/Invoke d3dcompiler_47.dll 产出 LiquidGlass.ps
//
// 改之前先看这几条 都是踩过的
//
// 位移方向必须朝面板中心 朝外的话采样坐标越过元素边界就被钳制 位移归零 折射看起来像没做
// 所有 tex2D 坐标都得钳到纹素中心 输入纹理在元素外面是透明黑 外侧抽样越界会让边缘整圈变暗
// 长度量一律按像素并随面板尺寸缩放 导航栏 48px 高 卡片能到 600px 固定值在细长面板上会过冲
// 位移量必须小于折射带宽 一般取 0.6 倍 超了采样映射不再单调 边缘出现镜像重影
// 输出 alpha 要跟随输入 alpha 不跟随的话未绘制区域会变成纯黑 滚动时卡片边缘出现灰黑色块

sampler2D InputTexture : register(S0);

float  BlurRadius    : register(C0); // 模糊半径 像素
float  Refraction    : register(C1); // 折射强度 边缘处沿法线的最大位移 像素
float  RefThickness  : register(C2); // 边缘影响厚度 从面板边缘向内延伸的距离 像素
float  Glare         : register(C3); // 边缘高光强度
// 宿主用 Point 传尺寸 它只占一个常量寄存器 填在 .x 和 .y
// 所以这里必须是 float2 不能拆成两个 float 否则后面所有参数整体错位
float2 TextureSize   : register(C4); // .x 宽 .y 高 像素
float4 Tint          : register(C5); // 色调叠加 rgb 是颜色 a 是混合量
float  Opacity       : register(C6); // 整体不透明度
float  CornerRadius  : register(C7); // 面板圆角半径 像素 给圆角矩形 SDF 用
float  ScrimAlpha    : register(C8); // 磨砂灰层的不透明度 0 表示完全不生效

// 中性灰 只拉平背景的明暗差 不往任何一个色偏上带
static const float3 ScrimColor = float3(0.5, 0.5, 0.5);

// 尺寸缩放的参考短边 短边比它小就按比例缩效果尺度 免得细长面板过冲
static const float ScaleReference = 220.0;
static const float ScaleMin = 0.30;

// 折射带宽的绝对基准 像素
// 带宽 = 基准 + 短边 * 10% * 滑块
// 取绝对值而不是纯比例 是为了让透镜带在各种尺寸上都维持在玻璃边缘那个尺度 约 15 到 35px
// 纯比例的话大面板会被整块弯折 另外细长面板也靠它兜底 48px 高的导航栏不至于只剩 8px 带宽
static const float BandFloorPx = 14.0;

// 高光增益 Glare 是 1.0 时直接叠加会把浅色背景推到纯白
// 压一下总增益 让滑块全量程都落在可用范围
static const float GlareGain = 0.32;

// ---------------------------------------------------------------------------
// 采样
// ---------------------------------------------------------------------------

// 钳到纹素中心再取样
// 元素外面是透明黑 直接采会让边缘整圈发暗 钳到最靠边的纹素中心等价于 D3D 的 Clamp 寻址
float4 SampleClamped(float2 uv, float2 texel)
{
    float2 halfTexel = texel * 0.5;
    float2 p = clamp(uv, halfTexel, 1.0 - halfTexel);

    // 用 tex2Dlod 显式给 LOD 不用 tex2D
    // ps_3_0 会给 tex2D 的坐标自动加 _pp 精度提示 而 WPF 把效果画在两个三角形拼的四边形上
    // 共享边两侧的降精度插值有微小出入 采样位置就差开 元素正中会出现一条 1 像素竖线
    // 普通毛玻璃 16px 模糊盖得住 液态玻璃只有 2~3px 盖不住 tex2Dlod 不做隐式求导 缝就没了
    return tex2Dlod(InputTexture, float4(p, 0.0, 0.0));
}

// 盘式均匀采样近似高斯模糊 16 抽样
// 早期用同心圆环加 45 度等分 那种排布本身就是个正方形点阵 看过去是一片菱形格子
// 加点数只能让格子变密 去不掉格子 规则性才是问题
// 换成黄金角螺旋 径向按 sqrt 分布 方位角每步转 137.5 度 点既不共线也不成环
// 核函数取 Epanechnikov 抛物线 同类里最平滑的一档 观感才是均匀磨砂而不是图案
// 16 个点按 (i + 0.5) / 16 离线算好 权重 1 - t 和为 8 末尾统一乘 0.125
float4 SampleBlurred(float2 uv, float2 texel, float radius)
{
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

// 圆角矩形 SDF 返回向内深度 边缘处 0 向内为正 外面为负
// 用它而不是矩形距离场 是为了折射带能贴合控件真实的圆角轮廓
float RoundedDepth(float2 p, float2 halfSize, float radius)
{
    float2 q = abs(p) - halfSize + radius;
    return -(min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius);
}

// 圆形倒角剖面 边缘为 1 向内 depth 到 thickness 处衰减为 0
// 梯度在边缘趋于无穷 越往里衰减越快 压缩集中在最外几个像素 读起来才像透镜边缘
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

    // 面板尺寸补偿 细长面板按比例收窄效果尺度 免得边缘扭曲吞掉整个元素
    float sizeScale = max(ScaleMin, min(1.0, minDim / ScaleReference));

    // --- 圆角矩形 SDF -----------------------------------------------------
    // 距离场比矩形距离多给两样东西 折射带能贴合真实圆角 梯度天然是个连续的方向场
    float2 halfSize = TextureSize * 0.5;
    float2 p        = (uv - 0.5) * TextureSize;               // 像素坐标 原点在面板中心
    float  cornerR  = min(max(CornerRadius, 0.0), min(halfSize.x, halfSize.y));
    float  distPx   = RoundedDepth(p, halfSize, cornerR);     // 边缘 0 向内为正

    // 折射带宽 绝对基准加一点尺寸项
    // 纯按短边取比例会把大面板整块折一遍 侧边栏 300px 在滑块 50 时带宽 52.5px 位移 23px
    // 观感是壁纸被拉长了一块 不是玻璃边缘的透镜
    // 尺寸项满量程也只占短边 10% 弯曲集中在最外二十几像素 这才是边缘透镜
    // 绝对基准顺便救细长面板 导航栏 864x48 从 8.4px 提到 16px 不然 3.7px 位移在低对比背景上读不出来
    // 另外这里别再乘 sizeScale 带宽已经正比于 minDim 再叠一层等于对矮面板二次惩罚
    // 实测 684x77 的卡片位移只剩 2.1px 表现为只有部分卡片有折射
    // 上限取 0.42 不是 0.5 上下两条带得留出中间的平整区 不能相接
    float bandMax = minDim * 0.42;
    float band = BandFloorPx + minDim * 0.10 * saturate(RefThickness / 100.0);
    float thickness = clamp(band, 1.0, bandMax);

    // mask 边缘处 1 向内超过 thickness 像素后 0 给模糊梯度和高光用
    float tm   = saturate(distPx / thickness);
    float mask = 1.0 - (tm * tm * (3.0 - 2.0 * tm)); // 手写 smoothstep 内建函数在边界输入下行为未定义

    // --- 倒角高度场与表面法线 ---------------------------------------------
    // 在 p 和四个邻点求距离 一次拿到两样 一个是内向单位向量给折射位移 一个是表面斜率给菲涅尔和高光
    // 两者都连续 沿直边恒定沿圆角平滑旋转 早期四方向硬阶跃法线的方向突跳就没了
    float  e  = 1.0;
    float  dR = RoundedDepth(p + float2(e, 0.0), halfSize, cornerR);
    float  dL = RoundedDepth(p - float2(e, 0.0), halfSize, cornerR);
    float  dU = RoundedDepth(p + float2(0.0, e), halfSize, cornerR);
    float  dD = RoundedDepth(p - float2(0.0, e), halfSize, cornerR);

    float bevel = BevelHeight(distPx, thickness);

    // --- 内向方向 连续混合 避开 SDF 的角平分线折痕 ------------------------
    // 直接对圆角矩形 SDF 求梯度会踩坑 它的 max(q.x, q.y) 沿角平分线梯度不连续
    // 数值是连续的 但方向会从垂直于左右边突变成垂直于上下边 而方向正是折射和高光的驱动项
    // 对角线上就会亮出一条直线 实测卡片上这条折痕能到 90px
    // 所以改成按接近程度平滑混合出内向方向 再沿它取高度场的方向导数
    float2 toEdge = halfSize - abs(p);          // 到左右边和上下边的距离
    float2 nX = float2(-sign(p.x), 0.0);        // 左右边的内向法线
    float2 nY = float2(0.0, -sign(p.y));        // 上下边的内向法线

    // 在角平分线附近以 thickness 为宽度渐变 方向平滑旋转而不是突跳
    // 权重必须写 toEdge.x - toEdge.y 为正说明离左右边更近 该用 nX
    // 写成 toEdge.y - toEdge.x 就反了 靠近上下边的位置全用上 nX 而它在 p.x = 0 处正负翻转
    // 采样位置瞬间跳一个多像素 玻璃正中就是一条竖线 这个加大模糊也盖不住 跳的是采样位置不是内容
    float  wBlend = smoothstep(-thickness * 0.5, thickness * 0.5, toEdge.x - toEdge.y);
    float2 inward = normalize(lerp(nX, nY, wBlend) + float2(0.00001, 0.00001));

    // 沿 inward 的方向导数 也就是每像素的高度变化 再乘 z 半径换成真实斜率
    // z 半径必须按像素给 BevelHeight 归一化在 0 到 1 却横跨 thickness 个像素
    // 直接当高度用斜率只有 0.1 法线几乎垂直 菲涅尔和高光会全部失效
    float  zRadius = min(thickness * 0.4, 24.0);
    float  slope = ((BevelHeight(dR, thickness) - BevelHeight(dL, thickness)) * inward.x
                  + (BevelHeight(dU, thickness) - BevelHeight(dD, thickness)) * inward.y) * (0.5 / e);
    float2 hGrad = inward * (zRadius * slope);
    float3 N = normalize(float3(-hGrad, 1.0));

    // --- 折射位移 ---------------------------------------------------------
    // 两项叠加
    // 一是边缘法线分量 把靠边的采样点推向内部 形成透镜边缘的压缩
    //   峰值受采样映射单调性限制 f(d) = d + R * bevel(d) 里 bevel 在边缘梯度无界 超了映射反向就是镜像重影
    // 二是径向分量 把内容整体往面板中心轻微收拢
    //   只有第一项的话压缩完全垂直于各条边 切向 1:1 各向异性极强 看上去像把图片拉长
    //   参考实现都带这一项 它让内容绕着玻璃连续弯折而不是单向拉伸
    // 系数 0.62 而不是 0.55 带宽收窄后得靠更大的位移带宽比才能维持透镜强度
    // 上限 0.667 也就是边缘出现镜像重影的临界点 留一点余量
    float strength = saturate(Refraction / 150.0);
    float effRefraction = thickness * 0.62 * strength;

    // 径向项也乘 mask 收敛在折射带里 不然它会作用到整块面板 中心也位移 就不平整了
    float2 dispPx = inward * (effRefraction * bevel)
                  + (-p / max(halfSize, float2(1.0, 1.0))) * (thickness * 0.25 * strength) * mask;

    float2 disp   = dispPx * texel;
    float2 uvBase = uv + disp;

    // --- 采样 -------------------------------------------------------------
    // 中心模糊边缘锐利 跟早期实现相反 两个参考实现都是磨砂主体加通透的折射边缘
    // 折射最强的地方必须保持清晰 不然弯折出来的内容立刻被糊掉 退化成普通毛玻璃
    // 中心系数 0.6 而不是 1.0 免得中心一下子变成高倍模糊
    // 下界 3px 采集纹理在元素正中会残留一列 1 像素的痕迹 普通毛玻璃 16px 模糊盖得住 液态玻璃只有 2~3px 盖不住
    float radius = BlurRadius * lerp(0.6, 0.15, mask);

    // 只采一次 不再按通道做色散
    // 早期 RGB 各做一次完整模糊 采样中心分别取 disp 的 1.03 1.00 0.97 倍 本意是厚玻璃边缘的色散
    // 但只差 3% 位移量不到 1 像素 效果微乎其微 代价是三倍模糊开销
    // 更糟的是位移量随位置变化 R 和 B/G 会先后跨过纹素边界 错开一个像素 正中又是一条竖线
    // 色散本就是很弱的锦上添花 竖线是实打实的缺陷 去掉
    float4 sample = SampleBlurred(uvBase, texel, radius);
    float3 col = sample.rgb;

    // 未绘制的区域在输入纹理里是透明的 一律输出不透明的话这些地方会变成纯黑
    // 表现为卡片边缘出现灰黑色块 尤其是贴窗口边正在滚动的那种
    // 采集裁剪和祖先重绘在滚动时未必严丝合缝 就会留下这种空洞
    // 输出 alpha 跟随输入 采不到内容就保持透明 让下面原本画的内容透出来
    float coverage = saturate(sample.a);

    // --- 磨砂灰层 ---------------------------------------------------------
    // 磨砂拉到高位时背景再怎么糊也还是花的 压不住上面的文字
    // 在折射结果之上 边缘高光之下垫一层半透明灰 把背景的明暗差拉平
    // 高光还在最上层 所以读出来还是一块玻璃 不是一块灰板
    // 只改 col 不改 coverage 圆角裁剪和采集空洞的透明照旧由 coverage 决定
    // 不然灰层会溢出到圆角之外 把卡片边缘补成实心方块
    col = lerp(col, ScrimColor, ScrimAlpha);

    // --- 边缘高光 ---------------------------------------------------------
    // 光源方向与幂次照 ybouane 的实现 两个窄高光加一个宽泛的环境反射
    // N 随位置连续变化 高光会沿边缘游走 在圆角处自然收束
    // 早期四方向常量法线是把每条边整条均匀点亮 读作描边
    // 光源方向要注意 本着色器的坐标里 +y 朝下 所以左上方是 -x -y
    // 直接照抄参考实现的 0.4 0.7 1.0 会变成右下方 左上边缘的法线跟它相反 dot 只有 0.58 怎么调都不亮
    float3 V = float3(0.0, 0.0, 1.0);
    float3 L1 = normalize(float3(-0.7, -0.5, 0.7));
    float3 H1 = normalize(L1 + V);
    float3 L2 = normalize(float3(0.6, 0.8, 0.5));
    float3 H2 = normalize(L2 + V);

    // 幂次必须跟法线的实际倾角范围匹配 倒角法线最多倾斜约 45 度 dot(N, H) 上限约 0.91
    // pow(0.91, 90) 约等于 1e-4 高光在数学上永远不亮
    // 参考实现能用 90 是它的高度场跨度大得多 40px 高跨 40px 不能照搬
    float  sp  = pow(saturate(dot(N, H1)), 12.0)
               + pow(saturate(dot(N, H2)), 6.0) * 0.30;

    // --- 菲涅尔 -----------------------------------------------------------
    // 掠射角也就是边缘处反射增强 让玻璃读作有反射的表面而不是被扭曲的图片 这是材质识别的关键线索
    float fres = pow(1.0 - abs(N.z), 2.5);

    // --- 边缘高光与内描边 -------------------------------------------------
    // 分三层 最外圈的细描边带动顶部偏置模拟天光 稍宽的 rim 还有最外几像素的收边
    // 参考实现的内描边是 1.5px 窄线且权重极低 大约 0.03 只作为材质的一点暗示
    // 早期这里是 1.5px 硬线乘上远高于参考的权重 观感就是画上去的边框 生硬就是这么来的
    // 改成约 4px 的柔和渐变带并压低权重
    float  sdf = -distPx;                                    // 内负外正
    float  innerStroke = smoothstep(-5.0, -1.5, sdf) * (1.0 - smoothstep(-1.5, 0.5, sdf));
    float  topBias = saturate(0.5 + 0.5 * (-p.y / max(halfSize.y, 1.0)));
    innerStroke *= 0.4 + 0.6 * topBias;
    float  rim = mask * mask;

    // 高光项都收敛在边缘 rim 和 fres 都用高次幂
    // 不然整体叠起来会把玻璃抬成均匀白雾 就是 Acrylic 那个观感
    col += Glare * GlareGain * (sp * 1.6 + pow(rim, 3.0) * 0.40 + innerStroke * 0.18);
    col = lerp(col, float3(1.0, 1.0, 1.0), fres * Glare * 0.30);

    // --- 色调与透明度 -----------------------------------------------------
    col = lerp(col, Tint.rgb, Tint.a);

    return float4(col, Opacity * coverage);
}
