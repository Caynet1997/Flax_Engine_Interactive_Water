#include "./Flax/Common.hlsl"

#define GROUP_SIZE 8
#define MAX_FORCES 256

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
    float NormalStrength;     // 高度场法线强度
    // 泡沫
    float FoamGeneration;     // 泡沫生成率
    float FoamDecay;          // 泡沫衰减率
    float BoundaryMode;       // 0=Solid 1=Open 2=Wrap
    float Time;               // 动画时间
    // 交互
    float ForceCount;         // 本帧力条目数量
    // 全局风力 (风向由 shader 内噪声场逐位置计算, 无需 CPU 传递)
    float WindStrength;       // 风力强度 (加速度)
    float WindGustAmount;     // 阵风调制幅度 (0=稳定风)
    float WindNoiseScale;     // 阵风空间频率 (每 texel)
    float WindGustSpeed;      // 阵风漂移速度
    float WindWaveHeight;     // 阵风气压→高度耦合 (波浪起伏强度)
    float WindFoamAmount;     // 风驱泡沫 (白浪) 生成率
META_CB_END

// =========================================================
// 资源绑定
// =========================================================
// 状态纹理 (r=高度h, g=速度u, b=速度v, a=泡沫foam)
Texture2D<float4> StateSrc : register(t0);
// 力条目缓冲 (CPU 每帧上传)
struct ForceEntry
{
    float2 Center;     // 纹理空间中心
    float Radius;      // 纹理空间半径
    float Strength;    // 强度
    float2 Direction;  // 方向 (Directional/Vortex/Attractor 使用)
    float HeightAmt;   // 高度修改量
    float FoamAmt;     // 泡沫量
    float Type;        // 0=Radial 1=Directional 2=Vortex 3=Attractor 4=Height 5=Foam
};
StructuredBuffer<ForceEntry> Forces : register(t1);
// 输出: 新状态 (UAV u0) 与 法线 (UAV u1)
RWTexture2D<float4> StateDst : register(u0);
RWTexture2D<float4> NormalField : register(u1);

// 共享内存 (含 1 像素光晕)
groupshared float4 g_Cache[GROUP_SIZE + 2][GROUP_SIZE + 2];

// =========================================================
// 梯度噪声 (用于风力阵风的空间调制)
// =========================================================
float2 WindNoiseHash(float2 p)
{
    p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
    return -1.0 + 2.0 * frac(sin(p) * 43758.5453123);
}

float WindNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(dot(WindNoiseHash(i + float2(0.0, 0.0)), f - float2(0.0, 0.0)),
                     dot(WindNoiseHash(i + float2(1.0, 0.0)), f - float2(1.0, 0.0)), u.x),
                lerp(dot(WindNoiseHash(i + float2(0.0, 1.0)), f - float2(0.0, 1.0)),
                     dot(WindNoiseHash(i + float2(1.0, 1.0)), f - float2(1.0, 1.0)), u.x), u.y);
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
// 力场计算: 遍历所有力条目, 累加对当前像素的影响
// =========================================================
void ApplyForces(uint2 pos, inout float h, inout float u, inout float v, inout float foam)
{
    int count = (int)ForceCount;
    for (int i = 0; i < count; i++)
    {
        ForceEntry f = Forces[i];
        float2 delta = (float2)pos - f.Center;
        float dist = length(delta);
        if (dist > f.Radius)
            continue;

        // 平滑衰减
        float t = dist / max(f.Radius, 0.001);
        float falloff = 1.0 - t * t;
        falloff *= falloff;
        if (falloff < 0.001)
            continue;

        int type = (int)f.Type;
        if (type == 0) // Radial: 径向推挤 + 下压
        {
            float2 dir = delta / max(dist, 0.001);
            float s = falloff * f.Strength;
            u += dir.x * s;
            v += dir.y * s;
            h -= s * 0.5;
            foam += s * 0.1;
        }
        else if (type == 1) // Directional: 方向力
        {
            float s = falloff * f.Strength;
            u += f.Direction.x * s;
            v += f.Direction.y * s;
        }
        else if (type == 2) // Vortex: 切线方向
        {
            float2 tangent = float2(-delta.y, delta.x) / max(dist, 0.001);
            float s = falloff * f.Strength;
            u += tangent.x * s;
            v += tangent.y * s;
        }
        else if (type == 3) // Attractor: 吸引/排斥
        {
            float2 dir = -delta / max(dist, 0.001);
            float s = falloff * f.Strength;
            u += dir.x * s;
            v += dir.y * s;
        }
        else if (type == 4) // HeightModifier: 高度修改
        {
            h += falloff * f.HeightAmt;
        }
        else if (type == 5) // FoamSource: 泡沫注入
        {
            foam += falloff * f.FoamAmt;
        }
    }
}

// =========================================================
// Pass 1: 浅水方程 (SWE) 流体模拟 + 力场交互
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

    // ---- 4.5 全局风力: 逐位置动态风向场 + 多尺度噪声 + 阵风包络 + 直接高度耦合 + 风驱泡沫 ----
    // 风向由低频噪声场逐位置计算 (随空间/时间平滑演变), 均匀风力散度为零不产生波浪。
    float windFoamGen = 0.0; // 风驱泡沫生成率 (先记录, 待泡沫平流后再累加, 避免被平流覆盖)
    if (WindStrength > 0.0001)
    {
        float drift = Time * WindGustSpeed;

        // 逐位置风向场: 低频噪声驱动方向角 (空间平滑 + 时间演变), 形成移动的风向域
        float2 dirCoord = (float2)pos * (WindNoiseScale * 0.5) + float2(drift * 0.15, drift * 0.11);
        float dirAngle = WindNoise(dirCoord) * 3.14159265; // [-pi, pi]
        float2 windDir = float2(cos(dirAngle), sin(dirAngle));
        float2 perpDir = float2(-windDir.y, windDir.x);

        // (1) 全局阵风包络: 多频正弦叠加, 整体风力随时间自然强弱起伏
        float envelope = 0.65 + 0.35 * (0.55 * sin(Time * 0.6)
                                      + 0.30 * sin(Time * 1.5 + 1.7)
                                      + 0.15 * sin(Time * 3.1 + 4.2));
        float effStrength = WindStrength * envelope;

        // (2) 风向扩散: 局部风向 ±30° 的斜向波列 (真实风浪的方向扩散)
        float cosA = 0.866; // cos(30°)
        float sinA = 0.5;   // sin(30°)
        float2 spreadDir1 = float2(windDir.x * cosA - windDir.y * sinA,  windDir.x * sinA + windDir.y * cosA);
        float2 spreadDir2 = float2(windDir.x * cosA + windDir.y * sinA, -windDir.x * sinA + windDir.y * cosA);

        // 三频移动噪声 (大涌浪 / 中浪 / 小涟漪), 随局部风向漂移形成传播
        float2 c1 = (float2)pos * WindNoiseScale + windDir * drift;
        float2 c2 = (float2)pos * (WindNoiseScale * 2.3) + windDir * (drift * 1.7) + perpDir * (drift * 0.35);
        float2 c3 = (float2)pos * (WindNoiseScale * 5.1) + windDir * (drift * 2.4);
        float gust = WindNoise(c1) + 0.5 * WindNoise(c2) + 0.25 * WindNoise(c3);

        // 斜向波噪声 (风向扩散)
        float spread1 = WindNoise((float2)pos * (WindNoiseScale * 1.6) + spreadDir1 * (drift * 1.3));
        float spread2 = WindNoise((float2)pos * (WindNoiseScale * 1.9) + spreadDir2 * (drift * 1.5));

        // 风应力: 主方向 (阵风调制) + 横向湍流 + 斜向波列
        float lateral = WindNoise(c2 + 7.31);
        float2 stress = windDir    * (effStrength * (1.0 + WindGustAmount * gust))
                      + perpDir    * (effStrength * WindGustAmount * 0.4 * lateral)
                      + spreadDir1 * (effStrength * 0.3 * spread1)
                      + spreadDir2 * (effStrength * 0.3 * spread2);
        u += DeltaTime * stress.x;
        v += DeltaTime * stress.y;

        // 阵风气压直接变形水面 (最直观的波浪来源, 随后由重力恢复形成传播)
        float totalGust = gust + 0.4 * spread1 + 0.4 * spread2;
        h += DeltaTime * effStrength * totalGust * WindWaveHeight;

        // (3) 风驱泡沫: 强阵风处的波峰生成白浪 (先记录, 平流后再加)
        windFoamGen = max(0.0, totalGust) * effStrength * WindFoamAmount;
    }

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
    float divNew = divergence - DeltaTime * Gravity * lap_h;
    h += DeltaTime * (-Depth * divNew);

    // ---- 7. 泡沫: 平流 + 生成 + 衰减 ----
    // 平流: 在回溯位置采样上一帧泡沫, 使泡沫随水流漂移
    float2 foamBackPos = (float2)pos + 0.5 - float2(u, v) * DeltaTime;
    foam = SampleStateBilinear(foamBackPos, texSize).a;
    // 生成: 波峰陡峭度 (-lap_h) + 水流汇聚 (-divergence) + 风驱白浪
    float convergence = max(0.0, -divergence);
    float steepness = max(0.0, -lap_h);
    foam += DeltaTime * (FoamGeneration * (steepness + convergence) + windFoamGen);
    // 衰减
    foam *= max(0.0, 1.0 - FoamDecay * DeltaTime);

    // ---- 8. 力场交互: 遍历力条目施加冲量 ----
    ApplyForces(pos, h, u, v, foam);

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
// Pass 2: 高度场法线 (纯 Sobel 梯度, 无细节噪声/环境浪)
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

    // 边界像素: 默认法线
    if (pos.x == 0 || pos.x >= texSize.x - 1 || pos.y == 0 || pos.y >= texSize.y - 1)
    {
        NormalField[pos] = float4(0.5, 0.5, 1.0, 1.0);
        return;
    }

    // 高度场 Sobel 梯度 (3x3 加权)
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
    float3 normal = normalize(float3(-dhdx, -dhdy, 1.0));

    NormalField[pos] = float4(normal * 0.5 + 0.5, 1.0);
}
