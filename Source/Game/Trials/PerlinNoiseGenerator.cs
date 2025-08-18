#if FLAX_EDITOR
using FlaxEditor.CustomEditors;
using FlaxEditor.CustomEditors.Editors;
using FlaxEditor.CustomEditors.Elements;
#endif
using System;
using FlaxEngine;
using FlaxEngine.GUI;
using FlaxEngine.Utilities;

namespace Game.Game.Trials;

[ExecuteInEditMode]
public class PerlinNoiseGenerator:Script
{
    public event Action<Texture> PerlinNoiseGenerated;
    public event Action PerlinNoiseDestroyed;
    public MaterialInstance Material;
    public string ParmaName;
    public PerlinNoiseParameters NoiseParameter;
    
    
    [HideInEditor] public PerlinNoiseParameters LastNoiseParameter;
    
    public struct PerlinNoiseParameters: IEquatable<PerlinNoiseParameters>
    {
        public float NoiseScale;
        public float NoiseAmount;
        public int NoiseSize;
        public Int2 NoisePosition;
        public bool IsSeamless;
        public float SeamlessStrength;
        public int SeamlessSampleTimes;
        public bool IsNormalMap;
        public float NormalStrength;

        public bool IsInvalid()
        {
            return NoiseSize>8192||NoiseScale<=0||NoiseAmount<=0;
        }
        
        public static bool operator ==(PerlinNoiseParameters left, PerlinNoiseParameters right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(PerlinNoiseParameters left, PerlinNoiseParameters right)
        {
            return !(left == right);
        }


        public bool Equals(PerlinNoiseParameters other)
        {
            return NoiseScale.Equals(other.NoiseScale) && NoiseAmount.Equals(other.NoiseAmount) && 
                   NoiseSize.Equals(other.NoiseSize) && NoisePosition.Equals(other.NoisePosition) && 
                   IsNormalMap == other.IsNormalMap && NormalStrength.Equals(other.NormalStrength) && 
                   IsSeamless == other.IsSeamless && SeamlessStrength.Equals(other.SeamlessStrength)&& 
                   SeamlessSampleTimes.Equals(other.SeamlessSampleTimes);
        }

        public override bool Equals(object obj)
        {
            return obj is PerlinNoiseParameters other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(NoiseScale, NoiseAmount, NoiseSize, NoisePosition, IsNormalMap, NormalStrength);
        }
    }
    
    private float[,] _perlinNoiseData;
    private PerlinNoise _perlinNoise;
    private Texture _tempTexture;

    public unsafe void Generate(PerlinNoiseParameters parameters)
    {
        if(NoiseParameter.IsInvalid()) return;
        if(LastNoiseParameter==NoiseParameter) return;
        if (parameters.NoiseSize <=0) return;
        LastNoiseParameter = NoiseParameter;
        
        _perlinNoise = new PerlinNoise
        {
            NoiseScale = parameters.NoiseScale,
            NoiseAmount = parameters.NoiseAmount
        };
        int size = parameters.NoiseSize;
        _perlinNoiseData= new float[size , size];

        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                float k = i + parameters.NoisePosition.X;
                float l = j + parameters.NoisePosition.Y;
                _perlinNoiseData[i, j] = _perlinNoise.Sample(k, l);
            }
        }
        
        // Create new texture asset
        Texture texture = Content.CreateVirtualAsset<Texture>();
        _tempTexture = texture;
        TextureBase.InitData initData = new()
        {
            Width = size,
            Height = size,
            ArraySize = 1,
            Format = PixelFormat.R8G8B8A8_UNorm
        };
        byte[] data = new byte[size * size * PixelFormatExtensions.SizeInBytes(initData.Format)];
        fixed (byte* dataPtr = data)
        {
            // Generate pixels data (linear gradient)
            Color32* colorsPtr = (Color32*)dataPtr;
            if (parameters.IsNormalMap)
            {
                Vector3[,] normals = new Vector3[size, size];
                
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // 计算相邻像素的高度差（梯度）
                        float x1 = _perlinNoiseData[Mathf.Clamp(x + 1, 0, size - 1), y];
                        float x2 = _perlinNoiseData[Mathf.Clamp(x - 1, 0, size - 1), y];
                        float y1 = _perlinNoiseData[x, Mathf.Clamp(y + 1, 0, size - 1)];
                        float y2 = _perlinNoiseData[x, Mathf.Clamp(y - 1, 0, size - 1)];
                        // 计算法线向量
                        float gradientX = (x1 - x2) * parameters.NormalStrength;
                        float gradientY = (y1 - y2) * parameters.NormalStrength;
                        Vector3 normal = Vector3.Normalize(new Vector3(-gradientX, -gradientY, 1.0f));
                        normals[x, y] = normal;

                    }
                }

                if (parameters.IsSeamless)
                {
                    Vector3[,] copy = new Vector3[size, size];

                    for (int y = 0; y < size; y++)
                    {
                        for (int x = 0; x < size; x++)
                        {
                            copy[x, y] = normals[x, y];
                        }
                    }

                    int count = (int)(size * parameters.SeamlessStrength);
                    int times = parameters.SeamlessSampleTimes;
                    for (int i = 0; i < times; i++)
                    {
                        for (int y = 0; y < size; y++)
                        {
                            for (int x = 0; x < count; x++)
                            {
                                //if (x > y) continue;
                                float density = 1f - (float)x /count;
                                normals[x, y] = (normals[size - x - 1, y] * density + normals[x, y] *(1-density)).Normalized;
                            }
                        }
                        for (int x = 0; x < size; x++)
                        {
                            for (int y = 0 ; y < count; y++)
                            {
                                //if (y > x) continue;
                                float density = 1f - (float)y /count;
                                normals[x, y] = (normals[x, size - y - 1] * density + normals[x, y] *(1-density)).Normalized;
                            }
                        }
                        
                        for (int y = 0; y < size; y++)
                        {
                            for (int x = 0; x < size; x++)
                            {
                                float density = Mathf.Abs(x - size * 0.5f) / size + Mathf.Abs(y - size * 0.5f) / size;
                                normals[x, y] = normals[x, y]*density + copy[x, y] * (1 - density);
                            }
                        }
                    }
                    
                }
                
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        Vector3 normal = normals[x, y];

                        // 将法线从[-1,1]映射到[0,1]并转换为Color32
                        Color32 c = new(
                            (byte)((normal.X + 1.0f) * 0.5f * 255),
                            (byte)((normal.Y + 1.0f) * 0.5f * 255),
                            (byte)((normal.Z + 1.0f) * 0.5f * 255),
                            255
                        );
                        colorsPtr[y * size + x] = c;
                    }
                }
            }
            else
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        byte value = (byte)(_perlinNoiseData[y, x]*255);
                        Color32 c = new(value,value,value,255);
                        colorsPtr[y * size + x] = c;
                    }
                }
            }
        }
        initData.Mips =
        [
            // Initialize mip maps data container description
            new TextureBase.InitData.MipData
            {
                Data = data,
                RowPitch = data.Length / initData.Height,
                SlicePitch = data.Length
            }
        ];
        texture.Init(ref initData);
        if(Material) Material.SetParameterValue(ParmaName,_tempTexture);
        PerlinNoiseGenerated?.Invoke(texture);
    }
    
    public override void OnEnable()
    {
        LastNoiseParameter = new PerlinNoiseParameters();
        GenerateTexture();
    }
    
    public override void OnDisable()
    {
        if(Material) Material .SetParameterValue(ParmaName,null);
        PerlinNoiseDestroyed?.Invoke();
        Destroy(ref _tempTexture);
    }

    public override void OnUpdate()
    {
        if ( NoiseParameter != LastNoiseParameter && !NoiseParameter.IsInvalid())
        {
            GenerateTexture();
            Debug.Log(1111);
        }
    }

    public void GenerateTexture()
    {
        Generate(NoiseParameter);
    }
    
    public void GenerateTexture(PerlinNoiseParameters parameters)
    {
        Generate(parameters);
    }
}

#region Editor
#if FLAX_EDITOR
[CustomEditor(typeof(PerlinNoiseGenerator))]
public class PerlinNoiseGeneratorEditor:GenericEditor
{
    private TextureBrush _textureBrush;
    private CustomElementsContainer<Image> _customContainer;
    private LayoutElementsContainer _layout;
    
    public override void Initialize(LayoutElementsContainer layout)
    {
        layout.Header("Perlin Noise Generator Editor");
        base.Initialize(layout);
        _layout = layout;
        layout.Space(2);
        _textureBrush = new TextureBrush();
        _customContainer = _layout.CustomContainer<Image>();
        _customContainer.CustomControl.Brush = _textureBrush;
        _customContainer.CustomControl.KeepAspectRatio = true;
        ((PerlinNoiseGenerator)Values[0]).PerlinNoiseGenerated += OnPerlinNoiseGenerated;
        ((PerlinNoiseGenerator)Values[0]).PerlinNoiseDestroyed += OnPerlinNoiseDestroyed;
    }

    protected override void Deinitialize()
    {
        ((PerlinNoiseGenerator)Values[0]).PerlinNoiseGenerated -= OnPerlinNoiseGenerated;
        ((PerlinNoiseGenerator)Values[0]).PerlinNoiseDestroyed -= OnPerlinNoiseDestroyed;
    }
    
    private void OnPerlinNoiseDestroyed()
    {
        _customContainer.CustomControl.Bounds = new Rectangle(0, 0, 0, 0);
        _customContainer.CustomControl.Margin = new Margin(0f);
    }

    private void OnPerlinNoiseGenerated(Texture texture)
    {
        _customContainer.CustomControl.Bounds = new Rectangle(0, 0, texture.Size.X, texture.Size.Y);
        _customContainer.CustomControl.Margin = new Margin(8f);
        _textureBrush.Texture = texture;
    }
}
#endif
#endregion