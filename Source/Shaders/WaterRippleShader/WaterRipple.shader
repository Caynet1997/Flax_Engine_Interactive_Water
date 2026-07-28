#include "./Flax/Common.hlsl"

#define GROUP_SIZE 8
#define MAX_TOUCHES 16

// =========================================================
// 常量缓冲: 浅水方程 (SWE) 模拟参数
// =========================================================
META_CB_BEGIN(0, SimData)
    // 流体物理
    float Gravity;            // 重力加速度 g
    float Depth;              // 平均水深 H
    float Viscosity;          // 粘度 (速度拉普拉斯扩散)
    float Drag;               // 线性阻尼
    float AdvectionStrength;  // 速度平流强度 (0=关闭)
    float DeltaTime;          // 固定时间步长
    float TexelSize;          // 1/textureSize
    float TouchCount;         // 多触点数量
    // 法线
    float NormalStrength;     // 高度场法线强度
    float DetailScale1;       // 细节噪声层1 频率
    float DetailStrength1;    // 细节噪声层1 强度
    float DetailSpeed1;       // 细节噪声层1 流速
    float DetailScale2;       // 细节噪声层2 频率
    float DetailStrength2;    // 细节噪声层2 强度
    float DetailSpeed2;       // 细节噪声层2 流速
    float Time;               // 动画时间
    // 泡沫
    float FoamGeneration;     // 泡沫生成率
    float FoamDecay;          // 泡沫衰减率
    float BoundaryMode;       // 0=Solid 1=Open 2=Wrap
    float RainStrength;       // 雨滴强度 (0=关闭)
    // 单触点回退 (当 TouchCount==0 时使用)
    float2 TouchPosition;
    float TouchRadius;
    float TouchStrength;
    // Gerstner 环境浪 / 坐标变换
    float WaveCount;
    float MeshSize;
    float WaterOriginX;
    float WaterOriginZ;
META_CB_END

// =========================================================
// 资源绑定
// =========================================================
// 状态纹理 (r=高度h, g=速度u, b=速度v, a=泡沫foam)
Texture2D<float4> StateSrc : register(t0);
// 多触点缓冲: float4(x, y, strength, radius)
StructuredBuffer<float4> TouchPoints : register(t1);
// 预计算的可平铺细节法线纹理 (启动时生成一次)
Texture2D<float4> DetailNormalTex : register(t2);
// Gerstner 环境浪参数缓冲
struct GerstnerWave
{
    float2 Direction;   // 归一化传播方向 (XZ)
    float Wavelength;
    float Amplitude;
    float Speed;
    float Steepness;
    float Phase;
    float Pad;
};
StructuredBuffer<GerstnerWave> Waves : register(t3);
// 输出: 新状态 (UAV u0) 与 多尺度法线 (UAV u1)
RWTexture2D<float4> StateDst : register(u0);
RWTexture2D<float4> NormalField : register(u1);
// 细节法线生成输出 (UAV u2, 仅 CS_GenerateDetailNormal 使用)
RWTexture2D<float4> DetailNormalOut : register(u2);

// 共享内存 (含 1 像素光晕)
groupshared float4 g_Cache[GROUP_SIZE + 2][GROUP_SIZE + 2];

// =========================================================
// 程序化噪声 (用于细节法线)
// =========================================================
float2 NoiseHash(float2 p)
{
    p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
    return -1.0 + 2.0 * frac(sin(p) * 43758.5453123);
}

float GradientNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);

    return lerp(lerp(dot(NoiseHash(i + float2(0.0, 0.0)), f - float2(0.0, 0.0)),
                     dot(NoiseHash(i + float2(1.0, 0.0)), f - float2(1.0, 0.0)), u.x),
                lerp(dot(NoiseHash(i + float2(0.0, 1.0)), f - float2(0.0, 1.0)),
                     dot(NoiseHash(i + float2(1.0, 1.0)), f - float2(1.0, 1.0)), u.x), u.y);
}

float FBM(float2 p)
{
    float value = 0.0;
    float amplitude = 0.5;
    for (int i = 0; i < 3; i++)
    {
        value += amplitude * GradientNoise(p);
        p *= 2.0;
        amplitude *= 0.5;
    }
    return value;
}

// 标量哈希 (用于雨滴随机分布)
float Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// =========================================================
// 可平铺噪声 (用于预计算细节法线纹理)
// =========================================================
#define DETAIL_BASE_PERIOD 8.0 // 细节纹理原生平铺周期 (8 个噪声单元)

float2 TileHash(float2 i, float period)
{
    i = ((i % period) + period) % period; // 晶格坐标环绕 → 噪声可平铺
    return NoiseHash(i);
}

float TileableGradientNoise(float2 p, float period)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(dot(TileHash(i + float2(0.0, 0.0), period), f - float2(0.0, 0.0)),
                     dot(TileHash(i + float2(1.0, 0.0), period), f - float2(1.0, 0.0)), u.x),
                lerp(dot(TileHash(i + float2(0.0, 1.0), period), f - float2(0.0, 1.0)),
                     dot(TileHash(i + float2(1.0, 1.0), period), f - float2(1.0, 1.0)), u.x), u.y);
}

float TileableFBM(float2 p, float basePeriod)
{
    float value = 0.0;
    float amplitude = 0.5;
    float period = basePeriod;
    for (int i = 0; i < 4; i++)
    {
        value += amplitude * TileableGradientNoise(p, period);
        p *= 2.0;
        period *= 2.0;
        amplitude *= 0.5;
    }
    return value;
}

// 采样预计算细节法线 (双线性 + 环绕平铺, 替代每帧 FBM)
float3 SampleDetailNormal(float2 uvTiled, float strength)
{
    uint2 dts;
    DetailNormalTex.GetDimensions(dts.x, dts.y);
    int2 sz = int2(dts);

    float2 coord = frac(uvTiled) * float2(dts) - 0.5;
    float2 f = frac(coord);
    int2 i = int2(floor(coord));

    float3 n00 = DetailNormalTex[uint2(((i + int2(0, 0)) % sz + sz) % sz)].xyz;
    float3 n10 = DetailNormalTex[uint2(((i + int2(1, 0)) % sz + sz) % sz)].xyz;
    float3 n01 = DetailNormalTex[uint2(((i + int2(0, 1)) % sz + sz) % sz)].xyz;
    float3 n11 = DetailNormalTex[uint2(((i + int2(1, 1)) % sz + sz) % sz)].xyz;

    float3 n = lerp(lerp(n00, n10, f.x), lerp(n01, n11, f.x), f.y);
    n = n * 2.0 - 1.0; // 解码 [0,1] → [-1,1]
    n.xy *= strength;
    return normalize(n);
}

// 程序化细节法线 (噪声梯度, 随时间流动)
float3 ProceduralDetailNormal(float2 uv, float scale, float flowSpeed, float strength)
{
    float eps = 0.02;
    float2 coord = uv * scale + float2(Time, Time * 0.63) * flowSpeed;

    float h  = FBM(coord);
    float hx = FBM(coord + float2(eps, 0.0));
    float hy = FBM(coord + float2(0.0, eps));

    float dhdx = (hx - h) / eps;
    float dhdy = (hy - h) / eps;

    return normalize(float3(-dhdx * strength, -dhdy * strength, 1.0));
}

// RNM 法线混合 (Reoriented Normal Mapping)
float3 BlendRNM(float3 baseNormal, float3 detailNormal)
{
    float3 t = baseNormal + float3(0.0, 0.0, 1.0);
    float3 u = detailNormal * float3(-1.0, -1.0, 1.0);
    return normalize((t / t.z) * dot(t, u) - u);
}

// Gerstner 环境浪法线 (与 CPU GetWaterHeight 同公式)
// 输出切线空间法线 (Z 为表面法线), 与基础法线约定一致:
//   纹理 x ↔ 世界 X, 纹理 y ↔ 世界 -Z
float3 ComputeGerstnerNormal(float2 worldXZ, float time)
{
    float dhdx = 0.0; // 世界 X 方向高度梯度
    float dhdz = 0.0; // 世界 Z 方向高度梯度
    int count = (int)WaveCount;
    for (int i = 0; i < count; i++)
    {
        GerstnerWave w = Waves[i];
        if (w.Amplitude <= 0.0001 || w.Wavelength <= 0.0001)
            continue;
        float k = 6.2831853 / w.Wavelength;
        float phi = k * dot(w.Direction, worldXZ) + w.Phase - w.Speed * time;
        float c = cos(phi);
        // d(A*sin(phi))/dx = A*k*dir.x*cos(phi)
        dhdx += w.Amplitude * k * w.Direction.x * c;
        dhdz += w.Amplitude * k * w.Direction.y * c;
    }
    // 世界梯度 → 每 texel 梯度 (乘以 世界单位/texel), 再转切线空间法线
    // gradScale = MeshSize * TexelSize = MeshSize / texSize
    float gradScale = MeshSize * TexelSize;
    // normal.x = -dh/d(texel x) = -dhdx * gradScale
    // normal.y = -dh/d(texel y) = -dhdz * (-gradScale) = dhdz * gradScale (因 texel y ↔ 世界 -Z)
    return normalize(float3(-dhdx * gradScale, dhdz * gradScale, 1.0));
}

// =========================================================
// 边界感知采样
// =========================================================
int2 ApplyBoundary(int2 p, uint2 texSize)
{
    if (BoundaryMode < 1.5) // Solid / Open: 钳制到边缘 (Neumann)
    {
        return clamp(p, int2(0, 0), int2(texSize) - int2(1, 1));
    }
    // Wrap: 环绕
    return ((p % (int2)texSize) + (int2)texSize) % (int2)texSize;
}

float4 SampleState(int2 p, uint2 texSize)
{
    return StateSrc[uint2(ApplyBoundary(p, texSize))];
}

// 双线性采样 (用于半拉格朗日平流)
float4 SampleStateBilinear(float2 uv, uint2 texSize)
{
    float2 coord = uv - 0.5;
    float2 f = frac(coord);
    int2 i = int2(floor(coord));

    float4 s00 = SampleState(i + int2(0, 0), texSize);
    float4 s10 = SampleState(i + int2(1, 0), texSize);
    float4 s01 = SampleState(i + int2(0, 1), texSize);
    float4 s11 = SampleState(i + int2(1, 1), texSize);

    return lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y);
}

// =========================================================
// Pass 1: 浅水方程 (SWE) 流体模拟
// =========================================================
META_CS(true, FEATURE_LEVEL_SM5)
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void CS_Simulate(
    uint3 groupID : SV_GroupID,
    uint3 groupThreadID : SV_GroupThreadID,
    uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 pos = dispatchThreadID.xy;
    uint2 localID = groupThreadID.xy;
    uint2 texSize;
    StateSrc.GetDimensions(texSize.x, texSize.y);

    // ---- 1. 共享内存加载 (中心 + 光晕, 并行) ----
    uint2 cachePos = localID + 1;
    uint2 groupStart = groupID * GROUP_SIZE;

    if (pos.x < texSize.x && pos.y < texSize.y)
        g_Cache[cachePos.x][cachePos.y] = StateSrc[pos];
    else
        g_Cache[cachePos.x][cachePos.y] = float4(0, 0, 0, 0);

    uint linearID = localID.y * GROUP_SIZE + localID.x;
    if (linearID < 10) // 顶边
    {
        uint x = linearID;
        g_Cache[x][0] = SampleState(int2(groupStart) + int2(x - 1, -1), texSize);
    }
    else if (linearID < 20) // 底边
    {
        uint x = linearID - 10;
        g_Cache[x][GROUP_SIZE + 1] = SampleState(int2(groupStart) + int2(x - 1, GROUP_SIZE), texSize);
    }
    else if (linearID < 28) // 左边
    {
        uint y = linearID - 20;
        g_Cache[0][y + 1] = SampleState(int2(groupStart) + int2(-1, y), texSize);
    }
    else if (linearID < 36) // 右边
    {
        uint y = linearID - 28;
        g_Cache[GROUP_SIZE + 1][y + 1] = SampleState(int2(groupStart) + int2(GROUP_SIZE, y), texSize);
    }

    GroupMemoryBarrierWithGroupSync();

    if (pos.x >= texSize.x || pos.y >= texSize.y)
        return;

    // ---- 2. 读取邻居 ----
    float4 c  = g_Cache[cachePos.x][cachePos.y];
    float h = c.r, u = c.g, v = c.b, foam = c.a;

    float h_l = g_Cache[cachePos.x - 1][cachePos.y].r;
    float h_r = g_Cache[cachePos.x + 1][cachePos.y].r;
    float h_d = g_Cache[cachePos.x][cachePos.y - 1].r;
    float h_u = g_Cache[cachePos.x][cachePos.y + 1].r;

    float u_l = g_Cache[cachePos.x - 1][cachePos.y].g;
    float u_r = g_Cache[cachePos.x + 1][cachePos.y].g;
    float u_d = g_Cache[cachePos.x][cachePos.y - 1].g;
    float u_u = g_Cache[cachePos.x][cachePos.y + 1].g;

    float v_l = g_Cache[cachePos.x - 1][cachePos.y].b;
    float v_r = g_Cache[cachePos.x + 1][cachePos.y].b;
    float v_d = g_Cache[cachePos.x][cachePos.y - 1].b;
    float v_u = g_Cache[cachePos.x][cachePos.y + 1].b;

    // ---- 3. 梯度 / 散度 / 拉普拉斯 ----
    float dhdx = (h_r - h_l) * 0.5;
    float dhdy = (h_u - h_d) * 0.5;
    float dudx = (u_r - u_l) * 0.5;
    float dvdy = (v_u - v_d) * 0.5;
    float divergence = dudx + dvdy;
    float lap_u = u_l + u_r + u_u + u_d - 4.0 * u;
    float lap_v = v_l + v_r + v_u + v_d - 4.0 * v;
    float lap_h = h_l + h_r + h_u + h_d - 4.0 * h;

    // ---- 4. 速度更新 (动量方程: 重力 + 粘度) ----
    u += DeltaTime * (-Gravity * dhdx + Viscosity * lap_u);
    v += DeltaTime * (-Gravity * dhdy + Viscosity * lap_v);

    // 线性阻尼
    float dragFactor = max(0.0, 1.0 - Drag * DeltaTime);
    u *= dragFactor;
    v *= dragFactor;

    // ---- 5. 半拉格朗日速度平流 (可选) ----
    if (AdvectionStrength > 0.001)
    {
        float2 backPos = (float2)pos + 0.5 - float2(u, v) * DeltaTime * AdvectionStrength;
        float4 adv = SampleStateBilinear(backPos, texSize);
        u = lerp(u, adv.g, AdvectionStrength);
        v = lerp(v, adv.b, AdvectionStrength);
    }

    // ---- 6. 高度更新 (连续性方程: 散度驱动) ----
    // 关键: 必须使用更新后速度的散度 (辛欧拉/半隐式格式)。
    // 若直接用旧散度 (前向欧拉), 波动方程无条件不稳定, 能量指数增长成噪点。
    // 解析近似: div(v_new) = div(v_old) + dt*(-g*lap(h)) (忽略粘度小项)
    float divNew = divergence - DeltaTime * Gravity * lap_h;
    h += DeltaTime * (-Depth * divNew);

    // ---- 7. 泡沫: 平流 + 生成 + 衰减 ----
    // 平流: 在回溯位置采样上一帧泡沫, 使泡沫随水流漂移
    float2 foamBackPos = (float2)pos + 0.5 - float2(u, v) * DeltaTime;
    foam = SampleStateBilinear(foamBackPos, texSize).a;
    // 生成与衰减
    float convergence = max(0.0, -divergence);
    float steepness = max(0.0, -lap_h);
    float foamSource = FoamGeneration * (steepness + convergence);
    foam += DeltaTime * foamSource;
    foam *= max(0.0, 1.0 - FoamDecay * DeltaTime);

    // ---- 8. 交互: 多触点速度冲量 ----
    int touchCount = (int)TouchCount;
    for (int ti = 0; ti < touchCount; ti++)
    {
        float4 tp = TouchPoints[ti];
        float dist = distance((float2)pos, tp.xy);
        float falloff = 1.0 - saturate(dist / max(tp.w, 0.001));
        falloff *= falloff;
        if (falloff > 0.001)
        {
            float2 dir = ((float2)pos - tp.xy) / max(dist, 0.001);
            u += dir.x * tp.z * falloff;
            v += dir.y * tp.z * falloff;
            h -= tp.z * falloff * 0.5; // 下压水面
        }
    }
    // 单触点回退
    if (touchCount == 0 && TouchStrength > 0.0)
    {
        float dist = distance((float2)pos, TouchPosition);
        float falloff = 1.0 - saturate(dist / max(TouchRadius, 0.001));
        falloff *= falloff;
        if (falloff > 0.001)
        {
            float2 dir = ((float2)pos - TouchPosition) / max(dist, 0.001);
            u += dir.x * TouchStrength * falloff;
            v += dir.y * TouchStrength * falloff;
            h -= TouchStrength * falloff * 0.5;
        }
    }

    // ---- 8.5 下雨: 随机点状小冲量 (基于网格哈希, 不占用触点配额) ----
    if (RainStrength > 0.001)
    {
        float cellSize = 16.0;
        float2 cell = floor((float2)pos / cellSize);
        float slot = floor(Time * 15.0); // 每秒 30 个雨滴时隙
        float rnd = Hash12(cell * 1.618 + slot * 7.31);
        if (rnd < 0.06) // 约 12% 的网格在当前时隙产生雨滴
        {
            float2 offset = float2(Hash12(cell + 1.7), Hash12(cell + 9.3));
            float2 dropCenter = (cell + offset) * cellSize;
            float dist = distance((float2)pos, dropCenter);
            float dropRadius = 4.0;
            float falloff = 1.0 - saturate(dist / dropRadius);
            h -= RainStrength * falloff * falloff;
        }
    }

    // ---- 9. 边界条件 ----
    bool atLeft   = pos.x == 0;
    bool atRight  = pos.x == texSize.x - 1;
    bool atBottom = pos.y == 0;
    bool atTop    = pos.y == texSize.y - 1;

    if (BoundaryMode < 0.5) // Solid: 反射 (法向速度置零)
    {
        if (atLeft || atRight) u = 0.0;
        if (atBottom || atTop) v = 0.0;
    }
    else if (BoundaryMode < 1.5) // Open: 边缘阻尼带吸收
    {
        float edgeDist = min(min((float)pos.x, (float)(texSize.x - 1 - pos.x)),
                             min((float)pos.y, (float)(texSize.y - 1 - pos.y)));
        float dampZone = 8.0;
        if (edgeDist < dampZone)
        {
            float damp = saturate(edgeDist / dampZone);
            h *= damp;
            u *= damp;
            v *= damp;
        }
    }
    // Wrap: 由光晕采样处理

    // ---- 10. 稳定性钳制 + 写回 ----
    h = clamp(h, -10.0, 10.0);
    u = clamp(u, -10.0, 10.0);
    v = clamp(v, -10.0, 10.0);
    foam = clamp(foam, 0.0, 1.0);

    StateDst[pos] = float4(h, u, v, foam);
}

// =========================================================
// Pass 2: 多尺度法线合成
// =========================================================
META_CS(true, FEATURE_LEVEL_SM5)
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void CS_ComputeNormals(
    uint3 groupID : SV_GroupID,
    uint3 groupThreadID : SV_GroupThreadID,
    uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 pos = dispatchThreadID.xy;
    uint2 texSize;
    StateSrc.GetDimensions(texSize.x, texSize.y);

    if (pos.x >= texSize.x || pos.y >= texSize.y)
        return;

    float2 uv = (float2)pos / (float2)texSize;

    // 边界像素: 默认法线
    if (pos.x == 0 || pos.x >= texSize.x - 1 || pos.y == 0 || pos.y >= texSize.y - 1)
    {
        NormalField[pos] = float4(0.5, 0.5, 1.0, 1.0);
        return;
    }

    // ---- 1. 基础法线: 高度场 Sobel 梯度 ----
    float h_l = StateSrc[pos - uint2(1, 0)].r
              + 0.25 * StateSrc[uint2(pos.x - 1, pos.y - 1)].r
              + 0.25 * StateSrc[uint2(pos.x - 1, pos.y + 1)].r;
    float h_r = StateSrc[pos + uint2(1, 0)].r
              + 0.25 * StateSrc[uint2(pos.x + 1, pos.y - 1)].r
              + 0.25 * StateSrc[uint2(pos.x + 1, pos.y + 1)].r;
    float h_d = StateSrc[pos - uint2(0, 1)].r
              + 0.25 * StateSrc[uint2(pos.x - 1, pos.y - 1)].r
              + 0.25 * StateSrc[uint2(pos.x + 1, pos.y - 1)].r;
    float h_u = StateSrc[pos + uint2(0, 1)].r
              + 0.25 * StateSrc[uint2(pos.x - 1, pos.y + 1)].r
              + 0.25 * StateSrc[uint2(pos.x + 1, pos.y + 1)].r;

    float dhdx = (h_r - h_l) * 0.5 * NormalStrength;
    float dhdy = (h_u - h_d) * 0.5 * NormalStrength;
    float3 baseNormal = normalize(float3(-dhdx, -dhdy, 1.0));

    // ---- 2. 细节法线: 采样预计算可平铺噪声纹理 (时间滚动, 替代每帧 FBM) ----
    float invPeriod = 1.0 / DETAIL_BASE_PERIOD;
    float2 scroll1 = float2(Time, Time * 0.63) * DetailSpeed1 * invPeriod;
    float2 scroll2 = float2(-Time * 0.8, Time * 0.45) * DetailSpeed2 * invPeriod;
    float3 detail1 = SampleDetailNormal(uv * DetailScale1 * invPeriod + scroll1, DetailStrength1);
    float3 detail2 = SampleDetailNormal(uv * DetailScale2 * invPeriod + scroll2, DetailStrength2);

    // ---- 3. Gerstner 环境浪法线 (texel → 世界坐标) ----
    float2 worldToUV = uv * MeshSize;
    float worldX = worldToUV.x - MeshSize * 0.5 + WaterOriginX;
    float worldZ = -worldToUV.y + MeshSize * 0.5 + WaterOriginZ;
    float3 gerstnerNormal = ComputeGerstnerNormal(float2(worldX, worldZ), Time);

    // ---- 4. RNM 混合 (大尺度环境浪 → 交互涟漪 → 细节) ----
    float3 normal = BlendRNM(gerstnerNormal, baseNormal);
    normal = BlendRNM(normal, detail1);
    normal = BlendRNM(normal, detail2);

    NormalField[pos] = float4(normal * 0.5 + 0.5, 1.0);
}

// =========================================================
// Pass 3: 生成可平铺细节法线纹理 (启动时运行一次)
// =========================================================
META_CS(true, FEATURE_LEVEL_SM5)
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void CS_GenerateDetailNormal(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 pos = dispatchThreadID.xy;
    uint2 texSize;
    DetailNormalOut.GetDimensions(texSize.x, texSize.y);

    if (pos.x >= texSize.x || pos.y >= texSize.y)
        return;

    // uv 跨越 [0, DETAIL_BASE_PERIOD), 噪声周期环绕 → 纹理可无缝平铺
    float2 uv = (float2)pos / (float2)texSize * DETAIL_BASE_PERIOD;
    float eps = 0.05;

    float h  = TileableFBM(uv, DETAIL_BASE_PERIOD);
    float hx = TileableFBM(uv + float2(eps, 0.0), DETAIL_BASE_PERIOD);
    float hy = TileableFBM(uv + float2(0.0, eps), DETAIL_BASE_PERIOD);

    float3 normal = normalize(float3(-(hx - h) / eps, -(hy - h) / eps, 1.0));
    DetailNormalOut[pos] = float4(normal * 0.5 + 0.5, 1.0);
}
