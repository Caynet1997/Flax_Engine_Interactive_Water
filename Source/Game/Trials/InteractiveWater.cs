using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FlaxEngine;

namespace Game.Game.Trials;

/// <summary>
/// 模拟质量等级, 决定流体纹理分辨率
/// </summary>
public enum WaterQualityLevel
{
    Low = 512,
    Medium = 1024,
    High = 2048,
    Ultra = 4096,
}

/// <summary>
/// 边界条件模式
/// </summary>
public enum WaterBoundaryMode
{
    Solid = 0, // 固体墙: 波反射
    Open = 1,  // 开放: 边缘吸收
    Wrap = 2,  // 环绕: 周期边界
}

/// <summary>
/// 交互式 2D 流体水面: 基于浅水方程 (SWE) 的 GPU 模拟。
/// 高度场 h 与速度场 (u,v) 耦合, 支持波传播/反射、水流汇聚、力场交互、泡沫与高度场法线。
/// 交互通过 StructuredBuffer 力条目实现 (最多 256 个/帧), 支持径向冲量/方向力/漩涡/吸引排斥/高度修改/泡沫注入。
/// </summary>
public class InteractiveWater : PostProcessEffect
{
    // ---------------------------------------------------------
    // 常量
    // ---------------------------------------------------------
    private const int ThreadGroupSize = 8;
    private const float MaxAccumulatedTime = 0.1f; // 防止螺旋死亡
    private const int MaxForces = 256;             // 每帧最大力条目数
    private const int ForceEntrySize = 40;         // ForceEntry 结构体字节数
    private const int HeightCacheSize = 128;       // CPU 高度缓存分辨率
    private const int HeightReadbackInterval = 4;  // 每 N 帧回读一次高度

    // ---------------------------------------------------------
    // 流体物理参数 (编辑器可调)
    // ---------------------------------------------------------

    [Tooltip("Gravity acceleration (drives wave propagation)"), Limit(0.1f, 5000.0f, 0.1f)]
    public float Gravity = 9.8f;

    [Tooltip("Mean water depth H"), Limit(0.01f, 100.0f, 0.01f)]
    public float Depth = 1.0f;

    [Tooltip("Viscosity (velocity diffusion)"), Limit(0.0f, 5.0f, 0.01f)]
    public float Viscosity = 0.1f;

    [Tooltip("Linear velocity damping"), Limit(0.0f, 10.0f, 0.01f)]
    public float Drag = 0.3f;

    [Tooltip("Velocity advection strength (0 = off)"), Limit(0.0f, 2.0f, 0.01f)]
    public float AdvectionStrength = 0.0f;

    [Tooltip("Simulation updates per second"), Limit(15f, 120.0f, 1f)]
    public float SimulateSpeed = 60f;

    // ---------------------------------------------------------
    // 法线参数
    // ---------------------------------------------------------

    [Tooltip("Height-field normal strength"), Limit(0.0f, 20.0f, 0.01f)]
    public float NormalStrength = 1.0f;

    // ---------------------------------------------------------
    // 泡沫参数
    // ---------------------------------------------------------

    [Tooltip("Foam generation rate"), Limit(0.0f, 20.0f, 0.01f)]
    public float FoamGeneration = 1.0f;

    [Tooltip("Foam decay rate"), Limit(0.0f, 10.0f, 0.01f)]
    public float FoamDecay = 0.5f;

    [Tooltip("Seconds to keep simulating after last activity before idling"), Limit(0.0f, 30.0f, 0.5f)]
    public float IdleSettleTime = 8.0f;

    // ---------------------------------------------------------
    // 全局风力 (风向由 shader 内噪声场逐位置动态计算)
    // ---------------------------------------------------------

    [Tooltip("Wind strength (acceleration, 0 = off)"), Limit(0.0f, 50.0f, 0.1f)]
    public float WindStrength = 0.0f;

    [Tooltip("Gust modulation amount (0 = steady wind)"), Limit(0.0f, 2.0f, 0.01f)]
    public float WindGustAmount = 0.8f;

    [Tooltip("Gust spatial frequency (per texel)"), Limit(0.001f, 0.5f, 0.001f)]
    public float WindNoiseScale = 0.02f;

    [Tooltip("Gust drift speed"), Limit(0.0f, 20.0f, 0.1f)]
    public float WindGustSpeed = 3.0f;

    [Tooltip("Wind wave height coupling (gust pressure -> surface deformation)"), Limit(0.0f, 5.0f, 0.05f)]
    public float WindWaveHeight = 0.8f;

    [Tooltip("Wind-driven foam (whitecaps) generation rate"), Limit(0.0f, 5.0f, 0.05f)]
    public float WindFoamAmount = 0.5f;

    // ---------------------------------------------------------
    // 边界与网格
    // ---------------------------------------------------------

    [Tooltip("Boundary condition mode")]
    public WaterBoundaryMode Boundary = WaterBoundaryMode.Solid;

    [Tooltip("World-space size of the water mesh")]
    public float MeshSize = 500f;

    [Tooltip("Simulation texture resolution")]
    public WaterQualityLevel Quality = WaterQualityLevel.High;

    // ---------------------------------------------------------
    // 资源引用
    // ---------------------------------------------------------

    public Shader RippleShader;
    public MaterialInstance WaterMaterial;
    public string RippleTextureParam = "Ripple Texture";
    public string NormalTextureParam = "Normal Texture";

    // ---------------------------------------------------------
    // 内部状态
    // ---------------------------------------------------------

    // 力条目结构体 (与 shader ForceEntry 布局一致, 40 字节)
    // 注意: HLSL 中 float2 按 8 字节对齐, 结构体步长补齐到 40;
    //       C# 的 Float2 对齐为 4, 需显式补 Pad 使 sizeof == 40, 否则 GPU 按 40 步长读取会错位。
    [StructLayout(LayoutKind.Sequential)]
    private struct ForceEntry
    {
        public Float2 Center;     // 纹理空间中心 (offset 0)
        public float Radius;      // 纹理空间半径 (offset 8)
        public float Strength;    // 强度 (offset 12)
        public Float2 Direction;  // 方向 (offset 16)
        public float HeightAmt;   // 高度修改量 (offset 24)
        public float FoamAmt;     // 泡沫量 (offset 28)
        public float Type;        // 0=Radial 1=Directional 2=Vortex 3=Attractor 4=Height 5=Foam (offset 32)
        public float Pad;         // 对齐填充 (offset 36) → sizeof == 40
    }

    // 常量缓冲数据 (布局必须与 shader 的 SimData 完全一致)
    [StructLayout(LayoutKind.Sequential)]
    private struct SimData
    {
        public float Gravity;
        public float Depth;
        public float Viscosity;
        public float Drag;
        public float AdvectionStrength;
        public float DeltaTime;
        public float TexelSize;
        public float NormalStrength;
        public float FoamGeneration;
        public float FoamDecay;
        public float BoundaryMode;
        public float Time;
        public float ForceCount;
        public float WindStrength;
        public float WindGustAmount;
        public float WindNoiseScale;
        public float WindGustSpeed;
        public float WindWaveHeight;
        public float WindFoamAmount;
    }

    private bool _isComputeSupported;
    private GPUTexture _stateA;       // (h, u, v, foam)
    private GPUTexture _stateB;
    private GPUTexture _normalField;
    private GPUBuffer _forceBuffer;   // StructuredBuffer<ForceEntry>
    private bool _pingPongFlip;
    private float _accumulator;
    private int _textureSize;
    private bool _cflWarned;
    private double _lastActivityTime;

    // 力条目 CPU 累积列表 (每帧清空 → 各交互源写入 → 上传 GPU)
    private readonly List<ForceEntry> _forceEntries = new List<ForceEntry>(MaxForces);
    private readonly ForceEntry[] _forceUploadData = new ForceEntry[MaxForces];

    // 高度缓存 (CPU 端)
    private float[] _heightCache;
    private int _frameCounter;

    /// <summary>全局实例, 供交互组件访问</summary>
    public static InteractiveWater Instance { get; private set; }

    /// <summary>水面基准高度 (世界 Y, 不含波浪)</summary>
    public float WaterSurfaceY => Actor.Position.Y;

    // ---------------------------------------------------------
    // 属性
    // ---------------------------------------------------------

    public int TextureSize => _textureSize;

    // ---------------------------------------------------------
    // 生命周期
    // ---------------------------------------------------------

    public override void OnEnable()
    {
        // 布局安全校验: C# ForceEntry 大小必须与 shader 步长一致, 否则多力条目会错位
        int entrySize = Marshal.SizeOf<ForceEntry>();
        if (entrySize != ForceEntrySize)
            Debug.LogError($"[InteractiveWater] ForceEntry 布局不匹配: C#={entrySize} bytes, shader={ForceEntrySize} bytes, 交互将失效!");

        _textureSize = (int)Quality;
        _pingPongFlip = false;
        _accumulator = 0f;
        _cflWarned = false;
        _lastActivityTime = Time.GameTime;
        _frameCounter = 0;

        // 状态纹理: RGBA16F 承载 (h, u, v, foam), Ping-Pong 双缓冲
        _stateA = CreateStateTexture(_textureSize);
        _stateB = CreateStateTexture(_textureSize);

        // 法线输出纹理
        _normalField = new GPUTexture();
        GPUTextureDescription normalDesc = GPUTextureDescription.New2D(
            _textureSize,
            _textureSize,
            PixelFormat.R16G16B16A16_Float,
            GPUTextureFlags.UnorderedAccess | GPUTextureFlags.ShaderResource
        );
        _normalField.Init(ref normalDesc);

        // 力条目结构化缓冲 (Dynamic, 每帧 CPU 上传)
        _forceBuffer = new GPUBuffer();
        GPUBufferDescription forceDesc = GPUBufferDescription.Structured(MaxForces, ForceEntrySize, false);
        forceDesc.Usage = GPUResourceUsage.Dynamic;
        _forceBuffer.Init(ref forceDesc);

        // 高度缓存
        _heightCache = new float[HeightCacheSize * HeightCacheSize];

        // 绑定材质参数
        if (WaterMaterial)
        {
            WaterMaterial.SetParameterValue(RippleTextureParam, _stateA);
            WaterMaterial.SetParameterValue(NormalTextureParam, _normalField);
        }

        _isComputeSupported = GPUDevice.Instance.Limits.HasCompute;
        MainRenderTask.Instance.AddCustomPostFx(this);

        Instance = this;
        ValidateCFL();
    }

    public override void OnDisable()
    {
        if (WaterMaterial)
        {
            WaterMaterial.SetParameterValue(RippleTextureParam, null);
            WaterMaterial.SetParameterValue(NormalTextureParam, null);
        }

        MainRenderTask.Instance?.RemoveCustomPostFx(this);
        if (Instance == this)
            Instance = null;
        ReleaseBuffers();
    }

    public override bool CanRender()
    {
        return base.CanRender()
            && _isComputeSupported
            && RippleShader != null && RippleShader.IsLoaded
            && _stateA != null && _stateB != null && _normalField != null;
    }

    // ---------------------------------------------------------
    // 渲染 (固定步长 + Compute Dispatch)
    // ---------------------------------------------------------

    public override unsafe void Render(
        GPUContext context,
        ref RenderContext renderContext,
        GPUTexture input,
        GPUTexture output
    )
    {
        _frameCounter++;

        // 活跃度跟踪 (有力条目或风力时视为活跃)
        int forceCount = _forceEntries.Count;
        bool hasWind = WindStrength > 0.0001f;
        if (forceCount > 0 || hasWind)
            _lastActivityTime = FlaxEngine.Time.GameTime;
        bool isActive = (FlaxEngine.Time.GameTime - _lastActivityTime) < IdleSettleTime;

        // 上传力条目缓冲
        UploadForceBuffer(context, forceCount);

        // 固定时间步长累加器
        float frameTime = Mathf.Min(Time.DeltaTime, MaxAccumulatedTime);
        _accumulator += frameTime;

        float fixedStep = 1.0f / SimulateSpeed;

        if (isActive)
        {
            bool firstStep = true;
            while (_accumulator >= fixedStep)
            {
                // 力仅在首个子步施加 (通过 ForceCount 控制)
                DispatchSimulation(context, fixedStep, firstStep ? forceCount : 0);
                firstStep = false;
                _accumulator -= fixedStep;

                if (_accumulator > fixedStep * 3)
                {
                    _accumulator = 0;
                    break;
                }
            }
        }
        else
        {
            _accumulator = 0f;
        }

        // 法线 Pass 每帧运行
        DispatchNormals(context);

        // 更新材质引用的最新状态纹理
        GPUTexture readTexture = _pingPongFlip ? _stateB : _stateA;
        if (WaterMaterial)
            WaterMaterial.SetParameterValue(RippleTextureParam, readTexture);

        // 清空力条目 (为下一帧准备)
        _forceEntries.Clear();
    }

    // ---------------------------------------------------------
    // 公开交互 API
    // ---------------------------------------------------------

    /// <summary>
    /// 径向冲量 (爆炸/入水浪花): 从中心向外推挤水面并下压。
    /// </summary>
    public void AddRadialImpulse(Vector3 worldPos, float strength, float worldRadius)
    {
        if (strength <= 0.001f || _forceEntries.Count >= MaxForces) return;
        _forceEntries.Add(new ForceEntry
        {
            Center = WorldToTexel(worldPos),
            Radius = Mathf.Max(worldRadius / MeshSize * _textureSize, 1.0f),
            Strength = strength,
            Direction = Float2.Zero,
            HeightAmt = 0f,
            FoamAmt = 0f,
            Type = 0f,
        });
        MarkActivity();
    }

    /// <summary>
    /// 方向力 (水流/风推动): 在区域内施加统一方向的速度冲量。
    /// </summary>
    public void AddDirectionalForce(Vector3 worldPos, Float2 direction, float strength, float worldRadius)
    {
        if (strength <= 0.001f || _forceEntries.Count >= MaxForces) return;
        Float2 dir = direction.Length > 0.0001f ? direction.Normalized : new Float2(1.0f, 0.0f);
        _forceEntries.Add(new ForceEntry
        {
            Center = WorldToTexel(worldPos),
            Radius = Mathf.Max(worldRadius / MeshSize * _textureSize, 1.0f),
            Strength = strength,
            Direction = dir,
            HeightAmt = 0f,
            FoamAmt = 0f,
            Type = 1f,
        });
        MarkActivity();
    }

    /// <summary>
    /// 漩涡 (旋转力场): 在区域内施加切线方向力, 形成旋转水流。
    /// </summary>
    public void AddVortex(Vector3 worldPos, float angularStrength, float worldRadius)
    {
        if (Mathf.Abs(angularStrength) <= 0.001f || _forceEntries.Count >= MaxForces) return;
        _forceEntries.Add(new ForceEntry
        {
            Center = WorldToTexel(worldPos),
            Radius = Mathf.Max(worldRadius / MeshSize * _textureSize, 1.0f),
            Strength = angularStrength,
            Direction = Float2.Zero,
            HeightAmt = 0f,
            FoamAmt = 0f,
            Type = 2f,
        });
        MarkActivity();
    }

    /// <summary>
    /// 吸引/排斥: 正=吸引 (水面向中心汇聚), 负=排斥 (水面向外散开)。
    /// </summary>
    public void AddAttractor(Vector3 worldPos, float strength, float worldRadius)
    {
        if (Mathf.Abs(strength) <= 0.001f || _forceEntries.Count >= MaxForces) return;
        _forceEntries.Add(new ForceEntry
        {
            Center = WorldToTexel(worldPos),
            Radius = Mathf.Max(worldRadius / MeshSize * _textureSize, 1.0f),
            Strength = strength,
            Direction = Float2.Zero,
            HeightAmt = 0f,
            FoamAmt = 0f,
            Type = 3f,
        });
        MarkActivity();
    }

    /// <summary>
    /// 高度修改 (正=注水隆起, 负=排水凹陷)。
    /// </summary>
    public void AddHeightModifier(Vector3 worldPos, float amount, float worldRadius)
    {
        if (Mathf.Abs(amount) <= 0.001f || _forceEntries.Count >= MaxForces) return;
        _forceEntries.Add(new ForceEntry
        {
            Center = WorldToTexel(worldPos),
            Radius = Mathf.Max(worldRadius / MeshSize * _textureSize, 1.0f),
            Strength = 0f,
            Direction = Float2.Zero,
            HeightAmt = amount,
            FoamAmt = 0f,
            Type = 4f,
        });
        MarkActivity();
    }

    /// <summary>
    /// 泡沫注入源。
    /// </summary>
    public void AddFoamSource(Vector3 worldPos, float amount, float worldRadius)
    {
        if (amount <= 0.001f || _forceEntries.Count >= MaxForces) return;
        _forceEntries.Add(new ForceEntry
        {
            Center = WorldToTexel(worldPos),
            Radius = Mathf.Max(worldRadius / MeshSize * _textureSize, 1.0f),
            Strength = 0f,
            Direction = Float2.Zero,
            HeightAmt = 0f,
            FoamAmt = amount,
            Type = 5f,
        });
        MarkActivity();
    }

    /// <summary>
    /// 查询指定世界位置的水面高度 (基准高度 + 高度场)。
    /// </summary>
    public float GetWaterHeight(Vector3 worldPos)
    {
        if (_heightCache == null)
            return WaterSurfaceY;

        float u = (worldPos.X + MeshSize * 0.5f - Actor.Position.X) / MeshSize;
        float v = (-worldPos.Z + MeshSize * 0.5f + Actor.Position.Z) / MeshSize;

        if (u < 0f || u > 1f || v < 0f || v > 1f)
            return WaterSurfaceY;

        float fx = u * (HeightCacheSize - 1);
        float fy = v * (HeightCacheSize - 1);
        int ix = Mathf.Clamp(Mathf.FloorToInt(fx), 0, HeightCacheSize - 2);
        int iy = Mathf.Clamp(Mathf.FloorToInt(fy), 0, HeightCacheSize - 2);
        float tx = fx - ix;
        float ty = fy - iy;

        float h00 = _heightCache[iy * HeightCacheSize + ix];
        float h10 = _heightCache[iy * HeightCacheSize + ix + 1];
        float h01 = _heightCache[(iy + 1) * HeightCacheSize + ix];
        float h11 = _heightCache[(iy + 1) * HeightCacheSize + ix + 1];

        float h = Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), ty);
        return WaterSurfaceY + h;
    }

    /// <summary>
    /// 世界坐标转纹理坐标 (texel)。
    /// </summary>
    public Float2 WorldToTexel(Vector3 worldPos)
    {
        return new Float2(
            (worldPos.X + MeshSize * 0.5f - Actor.Position.X) / MeshSize * _textureSize,
            (-worldPos.Z + MeshSize * 0.5f + Actor.Position.Z) / MeshSize * _textureSize
        );
    }

    // ---------------------------------------------------------
    // 内部方法
    // ---------------------------------------------------------

    private void MarkActivity()
    {
        _lastActivityTime = FlaxEngine.Time.GameTime;
    }

    private unsafe void UploadForceBuffer(GPUContext context, int count)
    {
        if (_forceBuffer == null || count <= 0) return;

        for (int i = 0; i < count; i++)
            _forceUploadData[i] = _forceEntries[i];

        fixed (ForceEntry* ptr = _forceUploadData)
        {
            context.UpdateBuffer(_forceBuffer, new IntPtr(ptr), (uint)(count * ForceEntrySize));
        }
    }

    private SimData BuildSimData(float dt, int forceCount)
    {
        return new SimData
        {
            Gravity = Gravity,
            Depth = Depth,
            Viscosity = Viscosity,
            Drag = Drag,
            AdvectionStrength = AdvectionStrength,
            DeltaTime = dt,
            TexelSize = 1.0f / _textureSize,
            NormalStrength = NormalStrength,
            FoamGeneration = FoamGeneration,
            FoamDecay = FoamDecay,
            BoundaryMode = (float)Boundary,
            Time = (float)FlaxEngine.Time.GameTime,
            ForceCount = forceCount,
            WindStrength = WindStrength,
            WindGustAmount = WindGustAmount,
            WindNoiseScale = WindNoiseScale,
            WindGustSpeed = WindGustSpeed,
            WindWaveHeight = WindWaveHeight,
            WindFoamAmount = WindFoamAmount,
        };
    }

    private unsafe void DispatchSimulation(GPUContext context, float dt, int forceCount)
    {
        GPUTexture srcTexture = _pingPongFlip ? _stateB : _stateA;
        GPUTexture dstTexture = _pingPongFlip ? _stateA : _stateB;

        var cb = RippleShader.GPU.GetCB(0);
        if (cb == IntPtr.Zero)
            return;

        SimData data = BuildSimData(dt, forceCount);
        context.UpdateCB(cb, new IntPtr(&data));
        context.BindCB(0, cb);

        // 绑定力条目缓冲 (t1)
        if (_forceBuffer != null)
            context.BindSR(1, _forceBuffer.View());

        uint groupCount = (uint)(_textureSize / ThreadGroupSize);

        // Pass 1: SWE 流体模拟 + 力场交互
        context.BindSR(0, srcTexture);
        context.BindUA(0, dstTexture.View());
        var csSimulate = RippleShader.GPU.GetCS("CS_Simulate");
        context.Dispatch(csSimulate, groupCount, groupCount, 1);
        context.ResetUA();
        context.ResetSR();
        context.ResetCB();

        _pingPongFlip = !_pingPongFlip;
    }

    private unsafe void DispatchNormals(GPUContext context)
    {
        GPUTexture latestState = _pingPongFlip ? _stateB : _stateA;

        var cb = RippleShader.GPU.GetCB(0);
        if (cb == IntPtr.Zero)
            return;

        SimData data = BuildSimData(1.0f / SimulateSpeed, 0);
        context.UpdateCB(cb, new IntPtr(&data));
        context.BindCB(0, cb);

        uint groupCount = (uint)(_textureSize / ThreadGroupSize);

        // Pass 2: 高度场法线
        context.BindSR(0, latestState);
        context.BindUA(1, _normalField.View());
        var csNormals = RippleShader.GPU.GetCS("CS_ComputeNormals");
        context.Dispatch(csNormals, groupCount, groupCount, 1);
        context.ResetUA();
        context.ResetSR();
        context.ResetCB();
    }

    /// <summary>
    /// CFL 稳定性校验: 要求 dt * sqrt(g*H) 小于 1。违反时警告。
    /// </summary>
    private void ValidateCFL()
    {
        float dt = 1.0f / SimulateSpeed;
        float waveSpeed = Mathf.Sqrt(Mathf.Max(Gravity * Depth, 0f));
        float cfl = dt * waveSpeed;
        if (cfl >= 1.0f && !_cflWarned)
        {
            Debug.LogWarning(
                $"[InteractiveWater] CFL 条件违反 (dt*sqrt(g*H) = {cfl:F3} >= 1), 模拟可能发散。" +
                $"请降低 Gravity/Depth 或提高 SimulateSpeed。");
            _cflWarned = true;
        }
    }

    private static GPUTexture CreateStateTexture(int size)
    {
        var texture = new GPUTexture();
        GPUTextureDescription desc = GPUTextureDescription.New2D(
            size,
            size,
            PixelFormat.R16G16B16A16_Float,
            GPUTextureFlags.UnorderedAccess | GPUTextureFlags.ShaderResource
        );
        texture.Init(ref desc);
        return texture;
    }

    private void ReleaseBuffers()
    {
        if (_stateA)
        {
            _stateA.ReleaseGPU();
            Destroy(ref _stateA);
        }
        if (_stateB)
        {
            _stateB.ReleaseGPU();
            Destroy(ref _stateB);
        }
        if (_normalField)
        {
            _normalField.ReleaseGPU();
            Destroy(ref _normalField);
        }
        if (_forceBuffer != null)
        {
            _forceBuffer.ReleaseGPU();
            Destroy(ref _forceBuffer);
        }
    }
}
