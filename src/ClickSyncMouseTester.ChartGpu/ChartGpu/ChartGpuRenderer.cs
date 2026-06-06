#define TRACE
using SharpGen.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ClickSyncMouseTester.ChartGpu;

[SupportedOSPlatform("windows")]
internal sealed class ChartGpuRenderer : IDisposable
{
    private readonly struct SolidVertex(float x, float y, System.Windows.Media.Color color)
    {
        public readonly float X = x;

        public readonly float Y = y;

        public readonly float R = ToSrgbFloat(color.R);

        public readonly float G = ToSrgbFloat(color.G);

        public readonly float B = ToSrgbFloat(color.B);

        public readonly float A = ToSrgbFloat(color.A);
    }

    private readonly struct CircleVertex(float x, float y, float localX, float localY, System.Windows.Media.Color color)
    {
        public readonly float X = x;

        public readonly float Y = y;

        public readonly float LocalX = localX;

        public readonly float LocalY = localY;

        public readonly float R = ToSrgbFloat(color.R);

        public readonly float G = ToSrgbFloat(color.G);

        public readonly float B = ToSrgbFloat(color.B);

        public readonly float A = ToSrgbFloat(color.A);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataTransformConstants(float xScale, float plotLeftPixels, float plotWidthPixels, float chunkDeltaX, float yScale, float plotTopPixels, float plotHeightPixels, float chunkDeltaY, float pixelWidth, float pixelHeight, float screenYAxisPositiveDown, float sizePixels)
    {
        public readonly float XScale = xScale;

        public readonly float PlotLeftPixels = plotLeftPixels;

        public readonly float PlotWidthPixels = plotWidthPixels;

        public readonly float ChunkDeltaX = chunkDeltaX;

        public readonly float YScale = yScale;

        public readonly float PlotTopPixels = plotTopPixels;

        public readonly float PlotHeightPixels = plotHeightPixels;

        public readonly float ChunkDeltaY = chunkDeltaY;

        public readonly float PixelWidth = pixelWidth;

        public readonly float PixelHeight = pixelHeight;

        public readonly float ScreenYAxisPositiveDown = screenYAxisPositiveDown;

        public readonly float SizePixels = sizePixels;
    }

    private readonly struct DataLineInstance(float x0, float y0, float x1, float y1, System.Windows.Media.Color color)
    {
        public readonly float X0 = x0;

        public readonly float Y0 = y0;

        public readonly float X1 = x1;

        public readonly float Y1 = y1;

        public readonly float R = ToSrgbFloat(color.R);

        public readonly float G = ToSrgbFloat(color.G);

        public readonly float B = ToSrgbFloat(color.B);

        public readonly float A = ToSrgbFloat(color.A);
    }

    private readonly struct DataCircleInstance(float x, float y, System.Windows.Media.Color color)
    {
        public readonly float X = x;

        public readonly float Y = y;

        public readonly float R = ToSrgbFloat(color.R);

        public readonly float G = ToSrgbFloat(color.G);

        public readonly float B = ToSrgbFloat(color.B);

        public readonly float A = ToSrgbFloat(color.A);
    }

    private sealed class PipelineResources : IDisposable
    {
        public ID3D11VertexShader VertexShader { get; }

        public ID3D11PixelShader PixelShader { get; }

        public ID3D11InputLayout InputLayout { get; }

        public PipelineResources(ID3D11VertexShader vertexShader, ID3D11PixelShader pixelShader, ID3D11InputLayout inputLayout)
        {
            VertexShader = vertexShader;
            PixelShader = pixelShader;
            InputLayout = inputLayout;
        }

        public void Dispose()
        {
            TryDisposePipelineResource(InputLayout, "pipeline input layout");
            TryDisposePipelineResource(PixelShader, "pipeline pixel shader");
            TryDisposePipelineResource(VertexShader, "pipeline vertex shader");
        }

        private static void TryDisposePipelineResource(ComObject? resource, string resourceName)
        {
            if (resource == null)
            {
                return;
            }
            try
            {
                resource.Dispose();
            }
            catch (Exception ex) when (IsIgnorableComTeardownException(ex))
            {
                Trace.TraceWarning($"ChartGpuRenderer ignored teardown failure while disposing {resourceName}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private sealed class DynamicVertexBuffer<T> : IDisposable where T : unmanaged
    {
        private readonly ID3D11Device _device;

        private readonly string _debugName;

        private uint _capacityBytes;

        public ID3D11Buffer? Buffer { get; private set; }

        public uint Stride => (uint)Unsafe.SizeOf<T>();

        public DynamicVertexBuffer(ID3D11Device device, string debugName)
        {
            _device = device;
            _debugName = debugName;
        }

        public unsafe void Upload(ID3D11DeviceContext context, ReadOnlySpan<T> vertices)
        {
            if (vertices.Length != 0)
            {
                EnsureCapacity((uint)(vertices.Length * Unsafe.SizeOf<T>()));
                MappedSubresource mappedSubresource = context.Map(Buffer, MapMode.WriteDiscard);
                fixed (T* source = vertices)
                {
                    System.Buffer.MemoryCopy(source, (void*)mappedSubresource.DataPointer, _capacityBytes, (nuint)(vertices.Length * Unsafe.SizeOf<T>()));
                }
                context.Unmap(Buffer, 0u);
            }
        }

        public void Dispose()
        {
            if (Buffer != null)
            {
                try
                {
                    Buffer.Dispose();
                }
                catch (Exception ex) when (IsIgnorableComTeardownException(ex))
                {
                    Trace.TraceWarning($"ChartGpuRenderer ignored teardown failure while disposing {_debugName} vertex buffer: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Buffer = null;
            _capacityBytes = 0u;
        }

        private void EnsureCapacity(uint requiredBytes)
        {
            if (Buffer == null || requiredBytes > _capacityBytes)
            {
                Dispose();
                _capacityBytes = NextPowerOfTwo(Math.Max(requiredBytes, Stride));
                Buffer = _device.CreateBuffer(_capacityBytes, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
                Buffer.DebugName = "ChartGpu." + _debugName + ".VertexBuffer";
            }
        }

        private static uint NextPowerOfTwo(uint value)
        {
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value++;
            return value;
        }
    }

    private static readonly FeatureLevel[] SupportedFeatureLevels = new FeatureLevel[3]
    {
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    };

    private static readonly InputElementDescription[] SolidInputElements = new InputElementDescription[2]
    {
        new InputElementDescription("POSITION", 0u, Format.R32G32_Float, 0u, 0u),
        new InputElementDescription("COLOR", 0u, Format.R32G32B32A32_Float, 8u, 0u)
    };

    private static readonly InputElementDescription[] CircleInputElements = new InputElementDescription[3]
    {
        new InputElementDescription("POSITION", 0u, Format.R32G32_Float, 0u, 0u),
        new InputElementDescription("LOCAL", 0u, Format.R32G32_Float, 8u, 0u),
        new InputElementDescription("COLOR", 0u, Format.R32G32B32A32_Float, 16u, 0u)
    };

    private static readonly InputElementDescription[] DataLineInputElements = new InputElementDescription[3]
    {
        new InputElementDescription("START", 0u, Format.R32G32_Float, 0u, 0u, InputClassification.PerInstanceData, 1u),
        new InputElementDescription("END", 0u, Format.R32G32_Float, 8u, 0u, InputClassification.PerInstanceData, 1u),
        new InputElementDescription("COLOR", 0u, Format.R32G32B32A32_Float, 16u, 0u, InputClassification.PerInstanceData, 1u)
    };

    private static readonly InputElementDescription[] DataCircleInstanceInputElements = new InputElementDescription[2]
    {
        new InputElementDescription("POSITION", 0u, Format.R32G32_Float, 0u, 0u, InputClassification.PerInstanceData, 1u),
        new InputElementDescription("COLOR", 0u, Format.R32G32B32A32_Float, 8u, 0u, InputClassification.PerInstanceData, 1u)
    };

    private const Format RenderTargetFormat = Format.B8G8R8A8_UNorm;

    private const uint SwapChainBufferCount = 2u;

    private const int MaxDataVertexBufferCacheEntries = 4096;

    private const long MaxDataVertexBufferCacheBytes = 384L * 1024L * 1024L;

    private static readonly uint[] PreferredMultisampleSampleCounts = new uint[2] { 4u, 2u };

    private const string SolidShaderSource = "struct VSInput\r\n{\r\n    float2 position : POSITION;\r\n    float4 color : COLOR;\r\n};\r\n\r\nstruct VSOutput\r\n{\r\n    float4 position : SV_Position;\r\n    float4 color : TEXCOORD0;\r\n};\r\n\r\nVSOutput VSMain(VSInput input)\r\n{\r\n    VSOutput output;\r\n    output.position = float4(input.position, 0.0, 1.0);\r\n    output.color = input.color;\r\n    return output;\r\n}\r\n\r\nfloat4 PSMain(VSOutput input) : SV_Target\r\n{\r\n    return input.color;\r\n}";

    private const string CircleShaderSource = "struct VSInput\r\n{\r\n    float2 position : POSITION;\r\n    float2 local : LOCAL;\r\n    float4 color : COLOR;\r\n};\r\n\r\nstruct VSOutput\r\n{\r\n    float4 position : SV_Position;\r\n    float2 local : TEXCOORD0;\r\n    float4 color : TEXCOORD1;\r\n};\r\n\r\nVSOutput VSMain(VSInput input)\r\n{\r\n    VSOutput output;\r\n    output.position = float4(input.position, 0.0, 1.0);\r\n    output.local = input.local;\r\n    output.color = input.color;\r\n    return output;\r\n}\r\n\r\nfloat4 PSMain(VSOutput input) : SV_Target\r\n{\r\n    float distanceSquared = dot(input.local, input.local);\r\n    float coverage = 1.0 - distanceSquared;\r\n    float aa = max(fwidth(distanceSquared), 1e-4);\r\n    float alpha = saturate(coverage / aa + 0.5);\r\n    clip(alpha - 1e-4);\r\n    return float4(input.color.rgb, input.color.a * alpha);\r\n}";

    private const string DataTransformShaderPrelude = @"
cbuffer DataTransform : register(b0)
{
    float4 xTransform;
    float4 yTransform;
    float4 renderTransform;
};

float2 DataToPixel(float2 dataPosition)
{
    float normalizedX = (xTransform.w + dataPosition.x) * xTransform.x;
    float normalizedY = (yTransform.w + dataPosition.y) * yTransform.x;
    float pixelX = xTransform.y + normalizedX * xTransform.z;
    float pixelYRatio = renderTransform.z > 0.5 ? normalizedY : (1.0 - normalizedY);
    float pixelY = yTransform.y + pixelYRatio * yTransform.z;
    return float2(pixelX, pixelY);
}

float2 PixelToClip(float2 pixelPosition)
{
    return float2(pixelPosition.x / renderTransform.x * 2.0 - 1.0, 1.0 - pixelPosition.y / renderTransform.y * 2.0);
}
";

    private const string DataLineShaderSource = DataTransformShaderPrelude + @"
struct VSInput
{
    float2 startPosition : START;
    float2 endPosition : END;
    float4 color : COLOR;
};

struct VSOutput
{
    float4 position : SV_Position;
    float4 color : TEXCOORD0;
};

VSOutput VSMain(VSInput input, uint vertexId : SV_VertexID)
{
    uint corner = vertexId % 6;
    float along = (corner == 2 || corner == 4 || corner == 5) ? 1.0 : 0.0;
    float side = (corner == 1 || corner == 2 || corner == 4) ? 1.0 : -1.0;
    float localX = (corner == 1 || corner == 2 || corner == 4) ? 1.0 : -1.0;
    float localY = (corner == 2 || corner == 4 || corner == 5) ? 1.0 : -1.0;
    float2 startPixel = DataToPixel(input.startPosition);
    float2 endPixel = DataToPixel(input.endPosition);
    float2 delta = endPixel - startPixel;
    float lengthPixels = length(delta);
    float2 normal = lengthPixels > 0.001 ? float2(-delta.y, delta.x) / lengthPixels : float2(1.0, 0.0);
    float2 basePixel = lerp(startPixel, endPixel, along);
    float2 pixel = lengthPixels > 0.001
        ? basePixel + normal * side * renderTransform.w
        : startPixel + float2(localX, localY) * renderTransform.w;

    VSOutput output;
    output.position = float4(PixelToClip(pixel), 0.0, 1.0);
    output.color = input.color;
    return output;
}

float4 PSMain(VSOutput input) : SV_Target
{
    return input.color;
}";

    private const string DataCircleShaderSource = DataTransformShaderPrelude + @"
struct VSInput
{
    float2 position : POSITION;
    float4 color : COLOR;
};

struct VSOutput
{
    float4 position : SV_Position;
    float2 local : TEXCOORD0;
    float4 color : TEXCOORD1;
};

VSOutput VSMain(VSInput input, uint vertexId : SV_VertexID)
{
    uint corner = vertexId % 6;
    float localX = (corner == 1 || corner == 2 || corner == 4) ? 1.0 : -1.0;
    float localY = (corner == 2 || corner == 4 || corner == 5) ? 1.0 : -1.0;
    float2 local = float2(localX, localY);
    float2 centerPixel = DataToPixel(input.position);
    float2 pixel = centerPixel + local * renderTransform.w;

    VSOutput output;
    output.position = float4(PixelToClip(pixel), 0.0, 1.0);
    output.local = local;
    output.color = input.color;
    return output;
}

float4 PSMain(VSOutput input) : SV_Target
{
    float distanceSquared = dot(input.local, input.local);
    float coverage = 1.0 - distanceSquared;
    float aa = max(fwidth(distanceSquared), 1e-4);
    float alpha = saturate(coverage / aa + 0.5);
    clip(alpha - 1e-4);
    return float4(input.color.rgb, input.color.a * alpha);
}";

    private nint _currentHwnd;

    private bool _isDisposed;

    private string _failureReason = string.Empty;

    private bool _hasPermanentFailure;

    private ID3D11Device? _device;

    private ID3D11DeviceContext? _context;

    private IDXGIFactory2? _factory;

    private IDXGISwapChain1? _swapChain;

    private ID3D11Texture2D? _swapChainBackBuffer;

    private ID3D11RenderTargetView? _swapChainRenderTargetView;

    private ID3D11Texture2D? _multisampleRenderTarget;

    private ID3D11RenderTargetView? _multisampleRenderTargetView;

    private PipelineResources? _solidPipeline;

    private PipelineResources? _circlePipeline;

    private PipelineResources? _dataLinePipeline;

    private PipelineResources? _dataCirclePipeline;

    private ID3D11BlendState? _blendState;

    private ID3D11RasterizerState? _rasterizerState;

    private DynamicVertexBuffer<SolidVertex>? _solidVertexBuffer;

    private DynamicVertexBuffer<CircleVertex>? _circleVertexBuffer;

    private DynamicVertexBuffer<DataLineInstance>? _dataLineInstanceBuffer;

    private DynamicVertexBuffer<DataCircleInstance>? _dataCircleInstanceBuffer;

    private ID3D11Buffer? _dataTransformConstantBuffer;

    private readonly Dictionary<SeriesVertexCacheKey, CachedVertexBuffer<DataCircleInstance>> _dataCircleVertexBufferCache = new Dictionary<SeriesVertexCacheKey, CachedVertexBuffer<DataCircleInstance>>();

    private readonly Dictionary<SeriesVertexCacheKey, CachedVertexBuffer<DataLineInstance>> _dataLineInstanceBufferCache = new Dictionary<SeriesVertexCacheKey, CachedVertexBuffer<DataLineInstance>>();

    private readonly LinkedList<SeriesVertexCacheKey> _dataVertexBufferCacheLru = new LinkedList<SeriesVertexCacheKey>();

    private long _dataVertexBufferCacheBytes;

    private FeatureLevel _featureLevel;

    private SampleDescription _renderTargetSampleDescription = SampleDescription.Default;

    private int _swapChainPixelWidth;

    private int _swapChainPixelHeight;

    private int _multisampleRenderTargetPixelWidth;

    private int _multisampleRenderTargetPixelHeight;

    public GpuRenderStats RenderStats { get; } = new GpuRenderStats();

    public bool IsAvailable
    {
        get
        {
            if (!_isDisposed && !_hasPermanentFailure && _device != null && _context != null)
            {
                return string.IsNullOrWhiteSpace(_failureReason);
            }
            return false;
        }
    }

    public string FailureReason => _failureReason;

    public void Render(nint hwnd, GpuPlotSceneFrame? scene, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight)
    {
        if (_isDisposed || hwnd == IntPtr.Zero || _hasPermanentFailure || logicalWidth <= 0.0 || logicalHeight <= 0.0 || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        string stage = "initialization";
        try
        {
            RenderStats.IsDeviceAvailable = TryExecuteRenderOperation(delegate
            {
                stage = "device initialization";
                EnsureDeviceResources();
                stage = "swap chain setup";
                EnsureSwapChain(hwnd, pixelWidth, pixelHeight);
                stage = "scene rendering";
                RenderScene(_swapChainRenderTargetView, _swapChainBackBuffer, scene, logicalWidth, logicalHeight, pixelWidth, pixelHeight);
                stage = "present";
                _swapChain.Present(1u, PresentFlags.None).CheckError();
            }, (Exception ex) => new InvalidOperationException("The Direct3D 11 renderer failed during " + stage + ". " + ex.Message, ex), () => IsPermanentInitializationFailure(stage));
        }
        finally
        {
            stopwatch.Stop();
            PopulateRenderStats(scene, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public BitmapSource? RenderToBitmap(nint hwnd, GpuPlotSceneFrame? scene, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight)
    {
        if (_isDisposed || hwnd == IntPtr.Zero || scene == null || _hasPermanentFailure || logicalWidth <= 0.0 || logicalHeight <= 0.0 || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return null;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        string stage = "initialization";
        try
        {
            BitmapSource bitmap = null;
            RenderStats.IsDeviceAvailable = TryExecuteRenderOperation(delegate
            {
                stage = "device initialization";
                EnsureDeviceResources();
                stage = "offscreen rendering";
                bitmap = RenderSceneToBitmap(scene, logicalWidth, logicalHeight, pixelWidth, pixelHeight);
            }, (Exception ex) => new InvalidOperationException("The Direct3D 11 bitmap export failed during " + stage + ". " + ex.Message, ex), () => IsPermanentInitializationFailure(stage));
            return bitmap;
        }
        finally
        {
            stopwatch.Stop();
            PopulateRenderStats(scene, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public BitmapSource? RenderOffscreen(GpuPlotSceneFrame? scene, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight)
    {
        if (_isDisposed || scene == null || _hasPermanentFailure || logicalWidth <= 0.0 || logicalHeight <= 0.0 || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return null;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        string stage = "initialization";
        try
        {
            BitmapSource bitmap = null;
            RenderStats.IsDeviceAvailable = TryExecuteRenderOperation(delegate
            {
                stage = "device initialization";
                EnsureDeviceResources();
                stage = "offscreen rendering";
                bitmap = RenderSceneToBitmap(scene, logicalWidth, logicalHeight, pixelWidth, pixelHeight);
            }, (Exception ex) => new InvalidOperationException("The Direct3D 11 offscreen renderer failed during " + stage + ". " + ex.Message, ex), () => IsPermanentInitializationFailure(stage));
            return bitmap;
        }
        finally
        {
            stopwatch.Stop();
            PopulateRenderStats(scene, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            DisposeDeviceResources();
        }
    }

    private void EnsureDeviceResources()
    {
        if (_device != null && _context != null)
        {
            return;
        }
        ID3D11Device device = null;
        ID3D11DeviceContext immediateContext = null;
        IDXGIFactory2 iDXGIFactory = null;
        PipelineResources pipelineResources = null;
        PipelineResources pipelineResources2 = null;
        PipelineResources pipelineResources3 = null;
        PipelineResources pipelineResources4 = null;
        ID3D11BlendState iD3D11BlendState = null;
        ID3D11RasterizerState iD3D11RasterizerState = null;
        DynamicVertexBuffer<SolidVertex> dynamicVertexBuffer = null;
        DynamicVertexBuffer<CircleVertex> dynamicVertexBuffer2 = null;
        DynamicVertexBuffer<DataLineInstance> dynamicVertexBuffer3 = null;
        DynamicVertexBuffer<DataCircleInstance> dynamicVertexBuffer4 = null;
        ID3D11Buffer dataTransformConstantBuffer = null;
        SampleDescription sampleDescription = SampleDescription.Default;
        try
        {
            Result value = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, SupportedFeatureLevels, out device, out _featureLevel, out immediateContext);
            if (value.Failure || device == null || immediateContext == null)
            {
                throw new InvalidOperationException($"Failed to create the Direct3D 11 hardware device. HRESULT: {value}");
            }
            iDXGIFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(debug: false);
            sampleDescription = ResolveRenderTargetSampleDescription(device);
            pipelineResources = CreatePipelineResources(device, SolidShaderSource, SolidInputElements, "Solid");
            pipelineResources2 = CreatePipelineResources(device, CircleShaderSource, CircleInputElements, "Scatter");
            pipelineResources3 = CreatePipelineResources(device, DataLineShaderSource, DataLineInputElements, "Data line");
            pipelineResources4 = CreatePipelineResources(device, DataCircleShaderSource, DataCircleInstanceInputElements, "Data scatter");
            iD3D11BlendState = device.CreateBlendState(BlendDescription.NonPremultiplied);
            RasterizerDescription cullNone = RasterizerDescription.CullNone;
            cullNone.MultisampleEnable = sampleDescription.Count > 1;
            cullNone.AntialiasedLineEnable = false;
            cullNone.ScissorEnable = false;
            cullNone.DepthClipEnable = true;
            iD3D11RasterizerState = device.CreateRasterizerState(cullNone);
            dynamicVertexBuffer = new DynamicVertexBuffer<SolidVertex>(device, "Solid");
            dynamicVertexBuffer2 = new DynamicVertexBuffer<CircleVertex>(device, "Scatter");
            dynamicVertexBuffer3 = new DynamicVertexBuffer<DataLineInstance>(device, "DataLine");
            dynamicVertexBuffer4 = new DynamicVertexBuffer<DataCircleInstance>(device, "DataScatterInstance");
            dataTransformConstantBuffer = device.CreateBuffer((uint)Unsafe.SizeOf<DataTransformConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            dataTransformConstantBuffer.DebugName = "ChartGpu.DataTransform.ConstantBuffer";
        }
        catch (Exception innerException)
        {
            dataTransformConstantBuffer?.Dispose();
            dynamicVertexBuffer4?.Dispose();
            dynamicVertexBuffer3?.Dispose();
            dynamicVertexBuffer2?.Dispose();
            dynamicVertexBuffer?.Dispose();
            iD3D11RasterizerState?.Dispose();
            iD3D11BlendState?.Dispose();
            pipelineResources4?.Dispose();
            pipelineResources3?.Dispose();
            pipelineResources2?.Dispose();
            pipelineResources?.Dispose();
            iDXGIFactory?.Dispose();
            immediateContext?.Dispose();
            device?.Dispose();
            throw new InvalidOperationException("Failed to initialize the Direct3D 11 device resources.", innerException);
        }
        _device = device;
        _context = immediateContext;
        _factory = iDXGIFactory;
        _solidPipeline = pipelineResources;
        _circlePipeline = pipelineResources2;
        _dataLinePipeline = pipelineResources3;
        _dataCirclePipeline = pipelineResources4;
        _blendState = iD3D11BlendState;
        _rasterizerState = iD3D11RasterizerState;
        _solidVertexBuffer = dynamicVertexBuffer;
        _circleVertexBuffer = dynamicVertexBuffer2;
        _dataLineInstanceBuffer = dynamicVertexBuffer3;
        _dataCircleInstanceBuffer = dynamicVertexBuffer4;
        _dataTransformConstantBuffer = dataTransformConstantBuffer;
        _renderTargetSampleDescription = sampleDescription;
        ClearFailure();
    }

    private void EnsureSwapChain(nint hwnd, int pixelWidth, int pixelHeight)
    {
        if (_device == null || _context == null || _factory == null)
        {
            throw new InvalidOperationException("The Direct3D 11 renderer device is not initialized.");
        }
        if (_swapChain == null || hwnd != _currentHwnd)
        {
            ResetSwapChain();
            SwapChainDescription1 desc = new SwapChainDescription1((uint)pixelWidth, (uint)pixelHeight, Format.B8G8R8A8_UNorm, stereo: false, Usage.RenderTargetOutput, 2u, Scaling.None, SwapEffect.FlipSequential);
            _swapChain = _factory.CreateSwapChainForHwnd(_device, hwnd, desc);
            _currentHwnd = hwnd;
            _swapChainPixelWidth = pixelWidth;
            _swapChainPixelHeight = pixelHeight;
            RecreateSwapChainRenderTarget();
        }
        else if (pixelWidth != _swapChainPixelWidth || pixelHeight != _swapChainPixelHeight)
        {
            UnbindRenderTargets();
            DisposeSwapChainRenderTarget();
            _context.ClearState();
            _swapChain.ResizeBuffers(2u, (uint)pixelWidth, (uint)pixelHeight, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            _swapChainPixelWidth = pixelWidth;
            _swapChainPixelHeight = pixelHeight;
            RecreateSwapChainRenderTarget();
        }
    }

    private void RecreateSwapChainRenderTarget()
    {
        if (_device == null || _swapChain == null)
        {
            throw new InvalidOperationException("The Direct3D 11 swap chain is not initialized.");
        }
        DisposeSwapChainRenderTarget();
        _swapChainBackBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0u);
        _swapChainRenderTargetView = _device.CreateRenderTargetView(_swapChainBackBuffer);
    }

    private void RenderScene(ID3D11RenderTargetView renderTargetView, ID3D11Resource resolveDestination, GpuPlotSceneFrame? scene, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight)
    {
        if (_device == null || _context == null || _solidPipeline == null || _circlePipeline == null || _blendState == null || _rasterizerState == null)
        {
            throw new InvalidOperationException("The Direct3D 11 renderer is not ready.");
        }
        ID3D11RenderTargetView renderTargetView2 = renderTargetView;
        ID3D11Texture2D iD3D11Texture2D = null;
        if (_renderTargetSampleDescription.Count > 1)
        {
            EnsureMultisampleRenderTarget(pixelWidth, pixelHeight);
            if (_multisampleRenderTarget == null || _multisampleRenderTargetView == null)
            {
                throw new InvalidOperationException("The Direct3D 11 multisample render target is not ready.");
            }
            renderTargetView2 = _multisampleRenderTargetView;
            iD3D11Texture2D = _multisampleRenderTarget;
        }
        ConfigureRenderTarget(renderTargetView2, pixelWidth, pixelHeight);
        GpuPlotStyleSnapshot gpuPlotStyleSnapshot = scene?.Style ?? new GpuPlotStyleSnapshot();
        _context.ClearRenderTargetView(renderTargetView2, ToColor4(gpuPlotStyleSnapshot.PlotBackgroundColor));
        List<SolidVertex> vertices = BuildGridAndGapVertices(scene, logicalWidth, logicalHeight, pixelWidth, pixelHeight);
        DrawSolidBatch(vertices);
        if (scene?.Series != null)
        {
            double dpiScaleX = ResolveAxisScale(logicalWidth, pixelWidth);
            double dpiScaleY = ResolveAxisScale(logicalHeight, pixelHeight);
            GpuSeriesSubmission[] series = scene.Series;
            foreach (GpuSeriesSubmission gpuSeriesSubmission in series)
            {
                switch (gpuSeriesSubmission.Kind)
                {
                    case GpuSeriesKind.Scatter:
                        if (gpuSeriesSubmission.UseDataCoordinates)
                        {
                            DrawDataCircleSeries(gpuSeriesSubmission, scene, pixelWidth, pixelHeight);
                        }
                        else
                        {
                            DrawCircleBatch(BuildScatterVertices(gpuSeriesSubmission, dpiScaleX, dpiScaleY, pixelWidth, pixelHeight));
                        }
                        break;
                    case GpuSeriesKind.Line:
                    case GpuSeriesKind.Stem:
                        if (gpuSeriesSubmission.UseDataCoordinates)
                        {
                            DrawDataLineSeries(gpuSeriesSubmission, scene, pixelWidth, pixelHeight);
                        }
                        else
                        {
                            DrawSolidBatch(BuildSegmentVertices(gpuSeriesSubmission, dpiScaleX, dpiScaleY, pixelWidth, pixelHeight));
                        }
                        break;
                    case GpuSeriesKind.Histogram:
                        DrawSolidBatch(BuildHistogramVertices(gpuSeriesSubmission, scene, pixelWidth, pixelHeight));
                        break;
                }
            }
        }
        List<SolidVertex> vertices2 = BuildBorderVertices(gpuPlotStyleSnapshot.PlotBorderColor, logicalWidth, logicalHeight, pixelWidth, pixelHeight);
        DrawSolidBatch(vertices2);
        if (iD3D11Texture2D != null)
        {
            _context.UnsetRenderTargets();
            _context.ResolveSubresource(resolveDestination, 0u, iD3D11Texture2D, 0u, Format.B8G8R8A8_UNorm);
        }
    }

    private unsafe BitmapSource RenderSceneToBitmap(GpuPlotSceneFrame scene, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight)
    {
        if (_device == null || _context == null)
        {
            throw new InvalidOperationException("The Direct3D 11 device is not initialized.");
        }
        Texture2DDescription description = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)pixelWidth, (uint)pixelHeight, 1u, 1u, BindFlags.RenderTarget);
        Texture2DDescription description2 = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)pixelWidth, (uint)pixelHeight, 1u, 1u, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
        using ID3D11Texture2D iD3D11Texture2D = _device.CreateTexture2D(in description);
        using ID3D11RenderTargetView renderTargetView = _device.CreateRenderTargetView(iD3D11Texture2D);
        using ID3D11Texture2D iD3D11Texture2D2 = _device.CreateTexture2D(in description2);
        RenderScene(renderTargetView, iD3D11Texture2D, scene, logicalWidth, logicalHeight, pixelWidth, pixelHeight);
        _context.CopyResource(iD3D11Texture2D2, iD3D11Texture2D);
        _context.Flush();
        MappedSubresource mappedSubresource = _context.Map(iD3D11Texture2D2, 0u);
        try
        {
            int num;
            byte[] array;
            checked
            {
                num = pixelWidth * 4;
                array = new byte[num * pixelHeight];
            }
            fixed (byte* ptr = array)
            {
                for (int i = 0; i < pixelHeight; i++)
                {
                    nint source = mappedSubresource.DataPointer + (nint)(mappedSubresource.RowPitch * i);
                    byte* destination = ptr + num * i;
                    Buffer.MemoryCopy((void*)source, destination, num, num);
                }
            }
            BitmapSource bitmapSource = BitmapSource.Create(pixelWidth, pixelHeight, 96.0, 96.0, PixelFormats.Bgra32, null, array, num);
            ((Freezable)bitmapSource).Freeze();
            return bitmapSource;
        }
        finally
        {
            _context.Unmap(iD3D11Texture2D2, 0u);
        }
    }

    private void ConfigureRenderTarget(ID3D11RenderTargetView renderTargetView, int pixelWidth, int pixelHeight)
    {
        if (_context == null)
        {
            throw new InvalidOperationException("The Direct3D 11 device context is not initialized.");
        }
        _context.OMSetRenderTargets(renderTargetView);
        Viewport viewport = new Viewport(0f, 0f, pixelWidth, pixelHeight, 0f, 1f);
        _context.RSSetViewport(viewport);
        _context.RSSetState(_rasterizerState);
        _context.OMSetBlendState(_blendState);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }

    private void DrawSolidBatch(List<SolidVertex> vertices)
    {
        if (vertices.Count != 0 && _context != null && _solidPipeline != null && _solidVertexBuffer != null)
        {
            _solidVertexBuffer.Upload(_context, CollectionsMarshal.AsSpan(vertices));
            BindVertexBuffer(_solidVertexBuffer.Buffer, _solidVertexBuffer.Stride);
            _context.IASetInputLayout(_solidPipeline.InputLayout);
            _context.VSSetShader(_solidPipeline.VertexShader);
            _context.PSSetShader(_solidPipeline.PixelShader);
            _context.Draw((uint)vertices.Count, 0u);
        }
    }

    private void DrawCircleBatch(List<CircleVertex> vertices)
    {
        if (vertices.Count != 0 && _context != null && _circlePipeline != null && _circleVertexBuffer != null)
        {
            _circleVertexBuffer.Upload(_context, CollectionsMarshal.AsSpan(vertices));
            BindVertexBuffer(_circleVertexBuffer.Buffer, _circleVertexBuffer.Stride);
            _context.IASetInputLayout(_circlePipeline.InputLayout);
            _context.VSSetShader(_circlePipeline.VertexShader);
            _context.PSSetShader(_circlePipeline.PixelShader);
            _context.Draw((uint)vertices.Count, 0u);
        }
    }

    private readonly struct SeriesVertexCacheKey
    {
        private readonly object _sourceKey;

        private readonly GpuSeriesKind _kind;

        private readonly int _datasetSlot;

        private readonly double _xOffset;

        private readonly object _geometryKey;

        private readonly int _chunkIndex;

        private readonly System.Windows.Media.Color _color;

        public SeriesVertexCacheKey(object sourceKey, GpuSeriesKind kind, int datasetSlot, double xOffset, object geometryKey, int chunkIndex, System.Windows.Media.Color color)
        {
            _sourceKey = sourceKey;
            _kind = kind;
            _datasetSlot = datasetSlot;
            _xOffset = xOffset;
            _geometryKey = geometryKey;
            _chunkIndex = chunkIndex;
            _color = color;
        }

        public override bool Equals(object? obj)
        {
            return obj is SeriesVertexCacheKey other && ReferenceEquals(_sourceKey, other._sourceKey) && _kind == other._kind && _datasetSlot == other._datasetSlot && _xOffset.Equals(other._xOffset) && ReferenceEquals(_geometryKey, other._geometryKey) && _chunkIndex == other._chunkIndex && _color == other._color;
        }

        public override int GetHashCode()
        {
            int geometryHashCode = _geometryKey != null ? RuntimeHelpers.GetHashCode(_geometryKey) : 0;
            return HashCode.Combine(RuntimeHelpers.GetHashCode(_sourceKey), _kind, _datasetSlot, _xOffset, geometryHashCode, _chunkIndex, _color);
        }
    }

    private sealed class CachedVertexBuffer<T> : IDisposable where T : unmanaged
    {
        public DynamicVertexBuffer<T> VertexBuffer { get; }

        public int VertexCount { get; }

        public long ByteSize { get; }

        public LinkedListNode<SeriesVertexCacheKey> LruNode { get; set; }

        public CachedVertexBuffer(DynamicVertexBuffer<T> vertexBuffer, int vertexCount)
        {
            VertexBuffer = vertexBuffer;
            VertexCount = vertexCount;
            ByteSize = Math.Max(0L, (long)vertexCount * Unsafe.SizeOf<T>());
        }

        public void Dispose()
        {
            VertexBuffer.Dispose();
        }
    }

    private void DrawDataLineSeries(GpuSeriesSubmission submission, GpuPlotSceneFrame scene, int pixelWidth, int pixelHeight)
    {
        if (_context == null || _dataLinePipeline == null || _dataLineInstanceBuffer == null)
        {
            return;
        }
        if (submission.SegmentChunks == null || submission.SegmentChunks.Length == 0)
        {
            return;
        }

        float halfThicknessPixels = ResolveRenderedLineThickness(submission.ThicknessPixels) / 2f;
        foreach (GpuSegmentChunk chunk in submission.SegmentChunks)
        {
            if (chunk?.Segments == null || chunk.Segments.Length == 0)
            {
                continue;
            }

            UploadDataTransformConstants(scene, pixelWidth, pixelHeight, halfThicknessPixels, chunk.OriginX, chunk.OriginY);
            if (TryDrawCachedDataLineChunk(submission, chunk))
            {
                continue;
            }

            List<DataLineInstance> instances = BuildDataLineInstances(chunk, submission.Color);
            if (instances.Count == 0)
            {
                continue;
            }
            if (TryCreateSeriesVertexCacheKey(submission, chunk.ChunkIndex, out SeriesVertexCacheKey cacheKey))
            {
                CachedVertexBuffer<DataLineInstance> cachedBuffer = CreateCachedDataLineBuffer(cacheKey, instances);
                DrawCachedDataLineBuffer(cachedBuffer);
                continue;
            }

            _dataLineInstanceBuffer.Upload(_context, CollectionsMarshal.AsSpan(instances));
            BindVertexBuffer(_dataLineInstanceBuffer.Buffer, _dataLineInstanceBuffer.Stride);
            DrawDataLineInstanceBuffer(instances.Count);
        }
    }

    private void DrawDataCircleSeries(GpuSeriesSubmission submission, GpuPlotSceneFrame scene, int pixelWidth, int pixelHeight)
    {
        if (_context == null || _dataCirclePipeline == null || _dataCircleInstanceBuffer == null)
        {
            return;
        }
        if (submission.PointChunks == null || submission.PointChunks.Length == 0)
        {
            return;
        }

        float radiusPixels = ResolveRenderedScatterRadius(submission.RadiusPixels);
        foreach (GpuPointChunk chunk in submission.PointChunks)
        {
            if (chunk?.Points == null || chunk.Points.Length == 0)
            {
                continue;
            }

            UploadDataTransformConstants(scene, pixelWidth, pixelHeight, radiusPixels, chunk.OriginX, chunk.OriginY);
            if (TryDrawCachedDataCircleChunk(submission, chunk))
            {
                continue;
            }

            List<DataCircleInstance> instances = BuildDataScatterInstances(chunk, submission.Color);
            if (instances.Count == 0)
            {
                continue;
            }
            if (TryCreateSeriesVertexCacheKey(submission, chunk.ChunkIndex, out SeriesVertexCacheKey cacheKey))
            {
                CachedVertexBuffer<DataCircleInstance> cachedBuffer = CreateCachedDataCircleBuffer(cacheKey, instances);
                DrawCachedDataCircleBuffer(cachedBuffer);
                continue;
            }

            _dataCircleInstanceBuffer.Upload(_context, CollectionsMarshal.AsSpan(instances));
            BindVertexBuffer(_dataCircleInstanceBuffer.Buffer, _dataCircleInstanceBuffer.Stride);
            DrawDataCircleVertexBuffer(instances.Count);
        }
    }

    private bool TryDrawCachedDataCircleChunk(GpuSeriesSubmission submission, GpuPointChunk chunk)
    {
        if (!TryCreateSeriesVertexCacheKey(submission, chunk.ChunkIndex, out SeriesVertexCacheKey cacheKey))
        {
            return false;
        }
        if (!_dataCircleVertexBufferCache.TryGetValue(cacheKey, out CachedVertexBuffer<DataCircleInstance> cachedBuffer) || cachedBuffer.VertexCount <= 0)
        {
            return false;
        }
        TouchCachedDataBuffer(cachedBuffer);
        DrawCachedDataCircleBuffer(cachedBuffer);
        return true;
    }

    private bool TryDrawCachedDataLineChunk(GpuSeriesSubmission submission, GpuSegmentChunk chunk)
    {
        if (!TryCreateSeriesVertexCacheKey(submission, chunk.ChunkIndex, out SeriesVertexCacheKey cacheKey))
        {
            return false;
        }
        if (!_dataLineInstanceBufferCache.TryGetValue(cacheKey, out CachedVertexBuffer<DataLineInstance> cachedBuffer) || cachedBuffer.VertexCount <= 0)
        {
            return false;
        }
        TouchCachedDataBuffer(cachedBuffer);
        DrawCachedDataLineBuffer(cachedBuffer);
        return true;
    }

    private CachedVertexBuffer<DataCircleInstance> CreateCachedDataCircleBuffer(SeriesVertexCacheKey cacheKey, List<DataCircleInstance> instances)
    {
        DynamicVertexBuffer<DataCircleInstance> vertexBuffer = new DynamicVertexBuffer<DataCircleInstance>(_device, "CachedDataScatterInstance");
        vertexBuffer.Upload(_context, CollectionsMarshal.AsSpan(instances));
        CachedVertexBuffer<DataCircleInstance> cachedBuffer = new CachedVertexBuffer<DataCircleInstance>(vertexBuffer, instances.Count);
        StoreCachedDataCircleBuffer(cacheKey, cachedBuffer);
        return cachedBuffer;
    }

    private CachedVertexBuffer<DataLineInstance> CreateCachedDataLineBuffer(SeriesVertexCacheKey cacheKey, List<DataLineInstance> instances)
    {
        DynamicVertexBuffer<DataLineInstance> vertexBuffer = new DynamicVertexBuffer<DataLineInstance>(_device, "CachedDataLine");
        vertexBuffer.Upload(_context, CollectionsMarshal.AsSpan(instances));
        CachedVertexBuffer<DataLineInstance> cachedBuffer = new CachedVertexBuffer<DataLineInstance>(vertexBuffer, instances.Count);
        StoreCachedDataLineBuffer(cacheKey, cachedBuffer);
        return cachedBuffer;
    }

    private void StoreCachedDataCircleBuffer(SeriesVertexCacheKey cacheKey, CachedVertexBuffer<DataCircleInstance> cachedBuffer)
    {
        if (_dataCircleVertexBufferCache.TryGetValue(cacheKey, out CachedVertexBuffer<DataCircleInstance> existingBuffer))
        {
            RemoveCachedDataBuffer(cacheKey, existingBuffer, _dataCircleVertexBufferCache);
        }
        _dataCircleVertexBufferCache[cacheKey] = cachedBuffer;
        AddCachedDataBuffer(cacheKey, cachedBuffer);
        TrimCachedDataBuffers();
    }

    private void StoreCachedDataLineBuffer(SeriesVertexCacheKey cacheKey, CachedVertexBuffer<DataLineInstance> cachedBuffer)
    {
        if (_dataLineInstanceBufferCache.TryGetValue(cacheKey, out CachedVertexBuffer<DataLineInstance> existingBuffer))
        {
            RemoveCachedDataBuffer(cacheKey, existingBuffer, _dataLineInstanceBufferCache);
        }
        _dataLineInstanceBufferCache[cacheKey] = cachedBuffer;
        AddCachedDataBuffer(cacheKey, cachedBuffer);
        TrimCachedDataBuffers();
    }

    private void AddCachedDataBuffer<T>(SeriesVertexCacheKey cacheKey, CachedVertexBuffer<T> cachedBuffer) where T : unmanaged
    {
        cachedBuffer.LruNode = _dataVertexBufferCacheLru.AddLast(cacheKey);
        _dataVertexBufferCacheBytes += cachedBuffer.ByteSize;
    }

    private void TouchCachedDataBuffer<T>(CachedVertexBuffer<T> cachedBuffer) where T : unmanaged
    {
        LinkedListNode<SeriesVertexCacheKey> node = cachedBuffer?.LruNode;
        if (node == null || node.List == null || ReferenceEquals(node.List.Last, node))
        {
            return;
        }

        _dataVertexBufferCacheLru.Remove(node);
        _dataVertexBufferCacheLru.AddLast(node);
    }

    private void RemoveCachedDataBuffer<T>(SeriesVertexCacheKey cacheKey, CachedVertexBuffer<T> cachedBuffer, Dictionary<SeriesVertexCacheKey, CachedVertexBuffer<T>> cache) where T : unmanaged
    {
        cache.Remove(cacheKey);
        LinkedListNode<SeriesVertexCacheKey> node = cachedBuffer.LruNode;
        if (node?.List != null)
        {
            _dataVertexBufferCacheLru.Remove(node);
        }
        _dataVertexBufferCacheBytes = Math.Max(0L, _dataVertexBufferCacheBytes - cachedBuffer.ByteSize);
        cachedBuffer.Dispose();
    }

    private void DrawCachedDataCircleBuffer(CachedVertexBuffer<DataCircleInstance> cachedBuffer)
    {
        if (cachedBuffer?.VertexBuffer?.Buffer == null || _context == null)
        {
            return;
        }
        BindVertexBuffer(cachedBuffer.VertexBuffer.Buffer, cachedBuffer.VertexBuffer.Stride);
        DrawDataCircleVertexBuffer(cachedBuffer.VertexCount);
    }

    private void DrawCachedDataLineBuffer(CachedVertexBuffer<DataLineInstance> cachedBuffer)
    {
        if (cachedBuffer?.VertexBuffer?.Buffer == null || _context == null)
        {
            return;
        }
        BindVertexBuffer(cachedBuffer.VertexBuffer.Buffer, cachedBuffer.VertexBuffer.Stride);
        DrawDataLineInstanceBuffer(cachedBuffer.VertexCount);
    }

    private void DrawDataCircleVertexBuffer(int vertexCount)
    {
        if (vertexCount <= 0 || _context == null || _dataCirclePipeline == null)
        {
            return;
        }
        _context.IASetInputLayout(_dataCirclePipeline.InputLayout);
        _context.VSSetShader(_dataCirclePipeline.VertexShader);
        _context.PSSetShader(_dataCirclePipeline.PixelShader);
        _context.DrawInstanced(6u, (uint)vertexCount, 0u, 0u);
    }

    private void DrawDataLineInstanceBuffer(int vertexCount)
    {
        if (vertexCount <= 0 || _context == null || _dataLinePipeline == null)
        {
            return;
        }
        _context.IASetInputLayout(_dataLinePipeline.InputLayout);
        _context.VSSetShader(_dataLinePipeline.VertexShader);
        _context.PSSetShader(_dataLinePipeline.PixelShader);
        _context.DrawInstanced(6u, (uint)vertexCount, 0u, 0u);
    }

    private void TrimCachedDataBuffers()
    {
        while ((_dataCircleVertexBufferCache.Count + _dataLineInstanceBufferCache.Count > MaxDataVertexBufferCacheEntries || _dataVertexBufferCacheBytes > MaxDataVertexBufferCacheBytes) && _dataVertexBufferCacheLru.First != null)
        {
            SeriesVertexCacheKey cacheKey = _dataVertexBufferCacheLru.First.Value;
            if (_dataCircleVertexBufferCache.TryGetValue(cacheKey, out CachedVertexBuffer<DataCircleInstance> circleBuffer))
            {
                RemoveCachedDataBuffer(cacheKey, circleBuffer, _dataCircleVertexBufferCache);
                continue;
            }
            if (_dataLineInstanceBufferCache.TryGetValue(cacheKey, out CachedVertexBuffer<DataLineInstance> lineBuffer))
            {
                RemoveCachedDataBuffer(cacheKey, lineBuffer, _dataLineInstanceBufferCache);
                continue;
            }

            _dataVertexBufferCacheLru.RemoveFirst();
        }
    }

    private static bool TryCreateSeriesVertexCacheKey(GpuSeriesSubmission submission, int chunkIndex, out SeriesVertexCacheKey cacheKey)
    {
        cacheKey = default;
        if (submission?.SourceKey == null)
        {
            return false;
        }
        cacheKey = new SeriesVertexCacheKey(submission.SourceKey, submission.Kind, submission.DatasetSlot, submission.XOffset, submission.GeometryKey, chunkIndex, submission.Color);
        return true;
    }

    private void BindVertexBuffer(ID3D11Buffer buffer, uint stride)
    {
        if (_context != null)
        {
            _context.IASetVertexBuffer(0u, buffer, stride);
        }
    }

    private unsafe void UploadDataTransformConstants(GpuPlotSceneFrame scene, int pixelWidth, int pixelHeight, float sizePixels, double chunkOriginX, double chunkOriginY)
    {
        if (_context == null || _dataTransformConstantBuffer == null)
        {
            return;
        }

        GpuViewportState viewport = scene?.Viewport ?? new GpuViewportState();
        double xSpan = viewport.XMaximum - viewport.XMinimum;
        double ySpan = viewport.YMaximum - viewport.YMinimum;
        float xScale = Math.Abs(xSpan) > 1E-09 ? (float)(1.0 / xSpan) : 1f;
        float yScale = Math.Abs(ySpan) > 1E-09 ? (float)(1.0 / ySpan) : 1f;
        DataTransformConstants constants = new DataTransformConstants(
            xScale,
            0f,
            Math.Max(1f, pixelWidth),
            (float)(chunkOriginX - viewport.XMinimum),
            yScale,
            0f,
            Math.Max(1f, pixelHeight),
            (float)(chunkOriginY - viewport.YMinimum),
            Math.Max(1f, pixelWidth),
            Math.Max(1f, pixelHeight),
            scene != null && scene.ScreenYAxisPositiveDown ? 1f : 0f,
            Math.Max(0.5f, sizePixels));

        MappedSubresource mappedSubresource = _context.Map(_dataTransformConstantBuffer, MapMode.WriteDiscard);
        try
        {
            *(DataTransformConstants*)mappedSubresource.DataPointer = constants;
        }
        finally
        {
            _context.Unmap(_dataTransformConstantBuffer, 0u);
        }
        _context.VSSetConstantBuffer(0u, _dataTransformConstantBuffer);
    }

    private unsafe static PipelineResources CreatePipelineResources(ID3D11Device device, string shaderSource, InputElementDescription[] inputElements, string label)
    {
        byte[] array = CompileShader(shaderSource, "VSMain", "vs_4_0", label + " vertex shader");
        byte[] array2 = CompileShader(shaderSource, "PSMain", "ps_4_0", label + " pixel shader");
        fixed (byte* shaderBytecode = array)
        {
            fixed (byte* shaderBytecode2 = array2)
            {
                ID3D11VertexShader vertexShader = device.CreateVertexShader(shaderBytecode, (nuint)array.Length);
                ID3D11PixelShader pixelShader = device.CreatePixelShader(shaderBytecode2, (nuint)array2.Length);
                ID3D11InputLayout inputLayout = device.CreateInputLayout(inputElements, array);
                return new PipelineResources(vertexShader, pixelShader, inputLayout);
            }
        }
    }

    private static byte[] CompileShader(string source, string entryPoint, string target, string label)
    {
        try
        {
            return Compiler.Compile(source, entryPoint, label, target, ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3).ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to compile the Direct3D 11 " + label + ". " + ex.Message, ex);
        }
    }

    private void PopulateRenderStats(GpuPlotSceneFrame? scene, double elapsedMilliseconds)
    {
        RenderStats.LastRenderMilliseconds = elapsedMilliseconds;
        RenderStats.SubmittedSeriesCount = (scene?.Series?.Length).GetValueOrDefault();
        RenderStats.SubmittedChunkCount = CountSubmittedChunks(scene);
    }

    private bool TryExecuteRenderOperation(Action operation, Func<Exception, Exception> wrapFailure, Func<bool> isPermanentFailure)
    {
        for (int i = 0; i < 2; i++)
        {
            try
            {
                operation();
                ClearFailure();
                return true;
            }
            catch (Exception ex)
            {
                Exception exception = wrapFailure(ex);
                bool flag = i == 0 && IsRecoverableRuntimeFailure(ex);
                HandleRenderFailure(exception, resetDevice: true, !flag && isPermanentFailure());
                if (!flag)
                {
                    return false;
                }
            }
        }
        return false;
    }

    private static bool IsPermanentInitializationFailure(string stage)
    {
        if (!string.Equals(stage, "initialization", StringComparison.Ordinal))
        {
            return string.Equals(stage, "device initialization", StringComparison.Ordinal);
        }
        return true;
    }

    private static bool IsRecoverableRuntimeFailure(Exception exception)
    {
        Result? result = ExtractResultCode(exception);
        if (!result.HasValue)
        {
            return false;
        }
        int code = result.Value.Code;
        if (code != Vortice.DXGI.ResultCode.DeviceRemoved.Code && code != Vortice.DXGI.ResultCode.DeviceReset.Code && code != Vortice.DXGI.ResultCode.DeviceHung.Code && code != Vortice.DXGI.ResultCode.DriverInternalError.Code)
        {
            return code == Vortice.DXGI.ResultCode.AccessLost.Code;
        }
        return true;
    }

    private static Result? ExtractResultCode(Exception exception)
    {
        if (exception is SharpGenException ex)
        {
            return ex.ResultCode;
        }
        if (exception.InnerException == null)
        {
            if (exception.HResult == 0)
            {
                return null;
            }
            return Result.GetResultFromException(exception);
        }
        return ExtractResultCode(exception.InnerException);
    }

    private void HandleRenderFailure(Exception exception, bool resetDevice, bool permanent)
    {
        _failureReason = exception.Message;
        _hasPermanentFailure = permanent;
        if (resetDevice)
        {
            DisposeDeviceResources();
        }
    }

    private void ClearFailure()
    {
        _failureReason = string.Empty;
        _hasPermanentFailure = false;
    }

    private void DisposeDeviceResources()
    {
        ResetSwapChain();
        DisposeMultisampleRenderTarget();
        ClearDataVertexBufferCaches();
        _solidVertexBuffer?.Dispose();
        _solidVertexBuffer = null;
        _circleVertexBuffer?.Dispose();
        _circleVertexBuffer = null;
        _dataLineInstanceBuffer?.Dispose();
        _dataLineInstanceBuffer = null;
        _dataCircleInstanceBuffer?.Dispose();
        _dataCircleInstanceBuffer = null;
        TryDisposeComObject(ref _dataTransformConstantBuffer, "data transform constant buffer");
        _solidPipeline?.Dispose();
        _solidPipeline = null;
        _circlePipeline?.Dispose();
        _circlePipeline = null;
        _dataLinePipeline?.Dispose();
        _dataLinePipeline = null;
        _dataCirclePipeline?.Dispose();
        _dataCirclePipeline = null;
        TryDisposeComObject(ref _blendState, "blend state");
        TryDisposeComObject(ref _rasterizerState, "rasterizer state");
        TryClearDeviceContext();
        TryFlushDeviceContext();
        TryDisposeComObject(ref _context, "immediate context");
        TryDisposeComObject(ref _device, "device");
        TryDisposeComObject(ref _factory, "factory");
        _currentHwnd = IntPtr.Zero;
        _swapChainPixelWidth = 0;
        _swapChainPixelHeight = 0;
    }

    private void ClearDataVertexBufferCaches()
    {
        foreach (CachedVertexBuffer<DataCircleInstance> cachedBuffer in _dataCircleVertexBufferCache.Values)
        {
            cachedBuffer.Dispose();
        }
        _dataCircleVertexBufferCache.Clear();

        foreach (CachedVertexBuffer<DataLineInstance> cachedBuffer in _dataLineInstanceBufferCache.Values)
        {
            cachedBuffer.Dispose();
        }
        _dataLineInstanceBufferCache.Clear();
        _dataVertexBufferCacheLru.Clear();
        _dataVertexBufferCacheBytes = 0L;
    }

    private void DisposeMultisampleRenderTarget()
    {
        TryDisposeComObject(ref _multisampleRenderTargetView, "multisample render target view");
        TryDisposeComObject(ref _multisampleRenderTarget, "multisample render target");
        _multisampleRenderTargetPixelWidth = 0;
        _multisampleRenderTargetPixelHeight = 0;
    }

    private void ResetSwapChain()
    {
        UnbindRenderTargets();
        DisposeSwapChainRenderTarget();
        TryDisposeComObject(ref _swapChain, "swap chain");
        _currentHwnd = IntPtr.Zero;
        _swapChainPixelWidth = 0;
        _swapChainPixelHeight = 0;
    }

    private void UnbindRenderTargets()
    {
        if (_context != null)
        {
            TryInvokeDeviceContextCleanup(delegate (ID3D11DeviceContext context)
            {
                context.UnsetRenderTargets();
            }, "unbind render targets");
        }
    }

    private void DisposeSwapChainRenderTarget()
    {
        TryDisposeComObject(ref _swapChainRenderTargetView, "swap-chain render target view");
        TryDisposeComObject(ref _swapChainBackBuffer, "swap-chain back buffer");
    }

    private void TryClearDeviceContext()
    {
        if (_context != null)
        {
            TryInvokeDeviceContextCleanup(delegate (ID3D11DeviceContext context)
            {
                context.ClearState();
            }, "clear state");
        }
    }

    private void TryFlushDeviceContext()
    {
        if (_context != null)
        {
            TryInvokeDeviceContextCleanup(delegate (ID3D11DeviceContext context)
            {
                context.Flush();
            }, "flush");
        }
    }

    private void TryInvokeDeviceContextCleanup(Action<ID3D11DeviceContext> cleanupAction, string operationName)
    {
        if (_context == null || cleanupAction == null)
        {
            return;
        }
        try
        {
            cleanupAction(_context);
        }
        catch (Exception ex) when (IsIgnorableComTeardownException(ex))
        {
            Trace.TraceWarning($"ChartGpuRenderer skipped device-context cleanup during {operationName}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryDisposeComObject<T>(ref T? comObject, string resourceName) where T : ComObject
    {
        if (comObject == null)
        {
            return;
        }
        try
        {
            comObject.Dispose();
        }
        catch (Exception ex) when (IsIgnorableComTeardownException(ex))
        {
            Trace.TraceWarning($"ChartGpuRenderer ignored teardown failure while disposing {resourceName}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            comObject = null;
        }
    }

    private static bool IsIgnorableComTeardownException(Exception exception)
    {
        if (exception is NullReferenceException || exception is ObjectDisposedException)
        {
            return true;
        }
        return false;
    }

    private static Color4 ToColor4(System.Windows.Media.Color color)
    {
        return new Color4(ToSrgbFloat(color.R), ToSrgbFloat(color.G), ToSrgbFloat(color.B), ToSrgbFloat(color.A));
    }

    private static float PixelToClipX(float pixelX, int pixelWidth)
    {
        return pixelX / (float)pixelWidth * 2f - 1f;
    }

    private static float PixelToClipY(float pixelY, int pixelHeight)
    {
        return 1f - pixelY / (float)pixelHeight * 2f;
    }

    private static bool IsFinite(float value)
    {
        if (!float.IsNaN(value))
        {
            return !float.IsInfinity(value);
        }
        return false;
    }

    private static bool IsFinite(double value)
    {
        if (!double.IsNaN(value))
        {
            return !double.IsInfinity(value);
        }
        return false;
    }

    private static float ToSrgbFloat(byte channel)
    {
        return (float)(int)channel / 255f;
    }

    private static double ResolveAxisScale(double logicalLength, int pixelLength)
    {
        if (logicalLength <= 0.0 || pixelLength <= 0)
        {
            return 1.0;
        }
        return (double)pixelLength / logicalLength;
    }

    private static SampleDescription ResolveRenderTargetSampleDescription(ID3D11Device? device)
    {
        if (device == null)
        {
            return SampleDescription.Default;
        }
        uint[] preferredMultisampleSampleCounts = PreferredMultisampleSampleCounts;
        foreach (uint num in preferredMultisampleSampleCounts)
        {
            if (device.CheckMultisampleQualityLevels(Format.B8G8R8A8_UNorm, num) != 0)
            {
                return new SampleDescription(num, 0u);
            }
        }
        return SampleDescription.Default;
    }

    private static float ResolveRenderedLineThickness(float thicknessPixels)
    {
        return Math.Max(1f, thicknessPixels);
    }

    private void EnsureMultisampleRenderTarget(int pixelWidth, int pixelHeight)
    {
        if (_device == null)
        {
            throw new InvalidOperationException("The Direct3D 11 device is not initialized.");
        }
        if (_renderTargetSampleDescription.Count <= 1)
        {
            DisposeMultisampleRenderTarget();
        }
        else if (_multisampleRenderTarget == null || _multisampleRenderTargetView == null || _multisampleRenderTargetPixelWidth != pixelWidth || _multisampleRenderTargetPixelHeight != pixelHeight)
        {
            DisposeMultisampleRenderTarget();
            Texture2DDescription description = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)pixelWidth, (uint)pixelHeight, 1u, 1u, BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, _renderTargetSampleDescription.Count, _renderTargetSampleDescription.Quality);
            _multisampleRenderTarget = _device.CreateTexture2D(in description);
            _multisampleRenderTargetView = _device.CreateRenderTargetView(_multisampleRenderTarget);
            _multisampleRenderTargetPixelWidth = pixelWidth;
            _multisampleRenderTargetPixelHeight = pixelHeight;
        }
    }

    private static float ResolveRenderedScatterRadius(float radiusPixels)
    {
        return Math.Max(0.5f, radiusPixels);
    }

    private static float SnapStrokeCoordinatePixels(float logicalValue, float thicknessPixels, double dpiScale)
    {
        if (float.IsNaN(logicalValue) || float.IsInfinity(logicalValue) || dpiScale <= 0.0)
        {
            return logicalValue;
        }
        return (float)(Math.Floor((double)logicalValue * dpiScale) + (double)ResolveRenderedLineThickness(thicknessPixels) / 2.0);
    }

    private static float SnapMinStrokeCoordinatePixels(double logicalValue, float thicknessPixels, double dpiScale)
    {
        if (double.IsNaN(logicalValue) || double.IsInfinity(logicalValue) || dpiScale <= 0.0)
        {
            return (float)logicalValue;
        }
        return (float)(Math.Floor(logicalValue * dpiScale) + (double)ResolveRenderedLineThickness(thicknessPixels) / 2.0);
    }

    private static float SnapMaxStrokeCoordinatePixels(double logicalValue, float thicknessPixels, double dpiScale)
    {
        if (double.IsNaN(logicalValue) || double.IsInfinity(logicalValue) || dpiScale <= 0.0)
        {
            return (float)logicalValue;
        }
        return (float)(Math.Ceiling(logicalValue * dpiScale) - (double)ResolveRenderedLineThickness(thicknessPixels) / 2.0);
    }

    private static int CountSubmittedChunks(GpuPlotSceneFrame? scene)
    {
        if (scene?.Series == null)
        {
            return 0;
        }
        int num = 0;
        GpuSeriesSubmission[] series = scene.Series;
        foreach (GpuSeriesSubmission gpuSeriesSubmission in series)
        {
            int num2 = num;
            GpuPointChunk[] pointChunks = gpuSeriesSubmission.PointChunks;
            num = num2 + ((pointChunks != null) ? pointChunks.Length : 0);
            int num3 = num;
            GpuSegmentChunk[] segmentChunks = gpuSeriesSubmission.SegmentChunks;
            num = num3 + ((segmentChunks != null) ? segmentChunks.Length : 0);
            int num4 = num;
            GpuHistogramBinChunk[] histogramBinChunks = gpuSeriesSubmission.HistogramBinChunks;
            num = num4 + ((histogramBinChunks != null) ? histogramBinChunks.Length : 0);
        }
        return num;
    }

    private List<SolidVertex> BuildGridAndGapVertices(GpuPlotSceneFrame? scene, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight)
    {
        List<SolidVertex> list = new List<SolidVertex>();
        if (scene == null)
        {
            return list;
        }
        double num = ResolveAxisScale(logicalWidth, pixelWidth);
        double num2 = ResolveAxisScale(logicalHeight, pixelHeight);
        float num3 = (float)logicalHeight * (float)num2;
        float x = (float)logicalWidth * (float)num;
        if (scene.GapBands != null)
        {
            GpuGapBand[] gapBands = scene.GapBands;
            foreach (GpuGapBand gpuGapBand in gapBands)
            {
                float leftPixels = (float)((double)gpuGapBand.LeftPixels * num);
                float rightPixels = (float)((double)(gpuGapBand.LeftPixels + gpuGapBand.WidthPixels) * num);
                AddSolidQuad(list, leftPixels, 0f, rightPixels, num3, gpuGapBand.FillColor, pixelWidth, pixelHeight);
                float thicknessPixels = ResolveRenderedLineThickness(gpuGapBand.LineThicknessPixels);
                float num4 = SnapStrokeCoordinatePixels(gpuGapBand.CenterXPixels, thicknessPixels, num);
                AddLineQuad(list, num4, 0f, num4, num3, thicknessPixels, gpuGapBand.LineColor, pixelWidth, pixelHeight);
            }
        }
        if (scene.GridLines != null)
        {
            GpuGridLine[] gridLines = scene.GridLines;
            foreach (GpuGridLine gpuGridLine in gridLines)
            {
                float thicknessPixels2 = ResolveRenderedLineThickness(gpuGridLine.ThicknessPixels);
                if (gpuGridLine.IsVertical)
                {
                    float num5 = SnapStrokeCoordinatePixels(gpuGridLine.PositionPixels, thicknessPixels2, num);
                    AddLineQuad(list, num5, 0f, num5, num3, thicknessPixels2, gpuGridLine.Color, pixelWidth, pixelHeight);
                }
                else
                {
                    float num6 = SnapStrokeCoordinatePixels(gpuGridLine.PositionPixels, thicknessPixels2, num2);
                    AddLineQuad(list, 0f, num6, x, num6, thicknessPixels2, gpuGridLine.Color, pixelWidth, pixelHeight);
                }
            }
        }
        return list;
    }

    private static List<SolidVertex> BuildSegmentVertices(GpuSeriesSubmission submission, double dpiScaleX, double dpiScaleY, int pixelWidth, int pixelHeight)
    {
        if (submission.SegmentChunks == null)
        {
            return new List<SolidVertex>();
        }
        List<SolidVertex> list = new List<SolidVertex>(CountSegmentInstances(submission.SegmentChunks) * 6);
        float thicknessPixels = ResolveRenderedLineThickness(submission.ThicknessPixels);
        GpuSegmentChunk[] segmentChunks = submission.SegmentChunks;
        foreach (GpuSegmentChunk gpuSegmentChunk in segmentChunks)
        {
            if (gpuSegmentChunk?.Segments != null && gpuSegmentChunk.Segments.Length != 0)
            {
                GpuSegmentVertex[] segments = gpuSegmentChunk.Segments;
                for (int j = 0; j < segments.Length; j++)
                {
                    GpuSegmentVertex gpuSegmentVertex = segments[j];
                    AddLineQuad(list, (float)((double)gpuSegmentVertex.X0 * dpiScaleX), (float)((double)gpuSegmentVertex.Y0 * dpiScaleY), (float)((double)gpuSegmentVertex.X1 * dpiScaleX), (float)((double)gpuSegmentVertex.Y1 * dpiScaleY), thicknessPixels, submission.Color, pixelWidth, pixelHeight);
                }
            }
        }
        return list;
    }

    private static List<CircleVertex> BuildScatterVertices(GpuSeriesSubmission submission, double dpiScaleX, double dpiScaleY, int pixelWidth, int pixelHeight)
    {
        if (submission.PointChunks == null)
        {
            return new List<CircleVertex>();
        }
        List<CircleVertex> list = new List<CircleVertex>(CountPointInstances(submission.PointChunks) * 6);
        float radiusPixels = ResolveRenderedScatterRadius(submission.RadiusPixels);
        GpuPointChunk[] pointChunks = submission.PointChunks;
        foreach (GpuPointChunk gpuPointChunk in pointChunks)
        {
            if (gpuPointChunk?.Points != null && gpuPointChunk.Points.Length != 0)
            {
                GpuPointVertex[] points = gpuPointChunk.Points;
                for (int j = 0; j < points.Length; j++)
                {
                    GpuPointVertex gpuPointVertex = points[j];
                    float centerX = (float)((double)gpuPointVertex.X * dpiScaleX);
                    float centerY = (float)((double)gpuPointVertex.Y * dpiScaleY);
                    AddCircleQuad(list, centerX, centerY, radiusPixels, submission.Color, pixelWidth, pixelHeight);
                }
            }
        }
        return list;
    }

    private static List<DataLineInstance> BuildDataLineInstances(GpuSegmentChunk chunk, System.Windows.Media.Color color)
    {
        if (chunk?.Segments == null || chunk.Segments.Length == 0)
        {
            return new List<DataLineInstance>();
        }
        GpuSegmentVertex[] segments = chunk.Segments;
        List<DataLineInstance> list = new List<DataLineInstance>(segments.Length);
        for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
        {
            GpuSegmentVertex segment = segments[segmentIndex];
            AddDataLineInstance(list, segment.X0, segment.Y0, segment.X1, segment.Y1, color);
        }
        return list;
    }

    private static List<DataCircleInstance> BuildDataScatterInstances(GpuPointChunk chunk, System.Windows.Media.Color color)
    {
        if (chunk?.Points == null || chunk.Points.Length == 0)
        {
            return new List<DataCircleInstance>();
        }
        GpuPointVertex[] points = chunk.Points;
        List<DataCircleInstance> list = new List<DataCircleInstance>(points.Length);
        for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
        {
            GpuPointVertex point = points[pointIndex];
            if (IsFinite(point.X) && IsFinite(point.Y))
            {
                list.Add(new DataCircleInstance(point.X, point.Y, color));
            }
        }
        return list;
    }

    private static List<SolidVertex> BuildHistogramVertices(GpuSeriesSubmission submission, GpuPlotSceneFrame scene, int pixelWidth, int pixelHeight)
    {
        if (submission?.HistogramBinChunks == null || scene == null)
        {
            return new List<SolidVertex>();
        }

        List<SolidVertex> vertices = new List<SolidVertex>(CountHistogramBinInstances(submission.HistogramBinChunks) * 6);

        GpuViewportState viewport = scene.Viewport ?? new GpuViewportState();
        double xRange = viewport.XMaximum - viewport.XMinimum;
        double yRange = viewport.YMaximum - viewport.YMinimum;
        if (xRange <= 0.0 || yRange <= 0.0)
        {
            return vertices;
        }

        double plotWidth = Math.Max(1, pixelWidth);
        double plotHeight = Math.Max(1, pixelHeight);
        bool positiveDown = scene.ScreenYAxisPositiveDown;
        foreach (GpuHistogramBinChunk chunk in submission.HistogramBinChunks)
        {
            if (chunk?.Bins == null)
            {
                continue;
            }

            foreach (GpuHistogramBinVertex bin in chunk.Bins)
            {
                double minimumX = chunk.OriginX + bin.MinimumX;
                double maximumX = chunk.OriginX + bin.MaximumX;
                double value = chunk.OriginY + bin.Value;
                if (!IsFinite(minimumX) || !IsFinite(maximumX) || !IsFinite(value) || maximumX <= minimumX)
                {
                    continue;
                }

                float left = (float)((minimumX - viewport.XMinimum) / xRange * plotWidth);
                float right = (float)((maximumX - viewport.XMinimum) / xRange * plotWidth);
                float baselineY = MapDataYToPixels(0.0, viewport, plotHeight, positiveDown);
                float valueY = MapDataYToPixels(value, viewport, plotHeight, positiveDown);
                float top = Math.Min(baselineY, valueY);
                float bottom = Math.Max(baselineY, valueY);
                float inset = Math.Min(1.5f, Math.Max(0f, (right - left) * 0.08f));
                AddSolidQuad(vertices, left + inset, top, right - inset, bottom, submission.Color, pixelWidth, pixelHeight);
            }
        }
        return vertices;
    }

    private static int CountSegmentInstances(GpuSegmentChunk[] segmentChunks)
    {
        int count = 0;
        if (segmentChunks == null)
        {
            return count;
        }
        foreach (GpuSegmentChunk segmentChunk in segmentChunks)
        {
            if (segmentChunk?.Segments != null)
            {
                count += segmentChunk.Segments.Length;
            }
        }
        return count;
    }

    private static int CountPointInstances(GpuPointChunk[] pointChunks)
    {
        int count = 0;
        if (pointChunks == null)
        {
            return count;
        }
        foreach (GpuPointChunk pointChunk in pointChunks)
        {
            if (pointChunk?.Points != null)
            {
                count += pointChunk.Points.Length;
            }
        }
        return count;
    }

    private static int CountHistogramBinInstances(GpuHistogramBinChunk[] histogramBinChunks)
    {
        int count = 0;
        if (histogramBinChunks == null)
        {
            return count;
        }
        foreach (GpuHistogramBinChunk chunk in histogramBinChunks)
        {
            if (chunk?.Bins != null)
            {
                count += chunk.Bins.Length;
            }
        }
        return count;
    }

    private static float MapDataYToPixels(double y, GpuViewportState viewport, double plotHeight, bool positiveDown)
    {
        double yRange = viewport.YMaximum - viewport.YMinimum;
        double normalized = yRange > 0.0 ? (y - viewport.YMinimum) / yRange : 0.0;
        double screenNormalized = positiveDown ? normalized : 1.0 - normalized;
        return (float)(screenNormalized * plotHeight);
    }

    private static List<SolidVertex> BuildBorderVertices(System.Windows.Media.Color color, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight)
    {
        List<SolidVertex> list = new List<SolidVertex>(24);
        double dpiScale = ResolveAxisScale(logicalWidth, pixelWidth);
        double dpiScale2 = ResolveAxisScale(logicalHeight, pixelHeight);
        float num = SnapMinStrokeCoordinatePixels(0.0, 1f, dpiScale);
        float num2 = SnapMaxStrokeCoordinatePixels(logicalWidth, 1f, dpiScale);
        float num3 = SnapMinStrokeCoordinatePixels(0.0, 1f, dpiScale2);
        float num4 = SnapMaxStrokeCoordinatePixels(logicalHeight, 1f, dpiScale2);
        AddLineQuad(list, num, num3, num2, num3, 1f, color, pixelWidth, pixelHeight);
        AddLineQuad(list, num2, num3, num2, num4, 1f, color, pixelWidth, pixelHeight);
        AddLineQuad(list, num2, num4, num, num4, 1f, color, pixelWidth, pixelHeight);
        AddLineQuad(list, num, num4, num, num3, 1f, color, pixelWidth, pixelHeight);
        return list;
    }

    private static void AddSolidQuad(List<SolidVertex> vertices, float leftPixels, float topPixels, float rightPixels, float bottomPixels, System.Windows.Media.Color color, int pixelWidth, int pixelHeight)
    {
        if (IsFinite(leftPixels) && IsFinite(topPixels) && IsFinite(rightPixels) && IsFinite(bottomPixels) && !(Math.Abs(rightPixels - leftPixels) <= float.Epsilon) && !(Math.Abs(bottomPixels - topPixels) <= float.Epsilon))
        {
            SolidVertex item = CreateSolidVertex(leftPixels, topPixels, color, pixelWidth, pixelHeight);
            SolidVertex item2 = CreateSolidVertex(rightPixels, topPixels, color, pixelWidth, pixelHeight);
            SolidVertex item3 = CreateSolidVertex(rightPixels, bottomPixels, color, pixelWidth, pixelHeight);
            SolidVertex item4 = CreateSolidVertex(leftPixels, bottomPixels, color, pixelWidth, pixelHeight);
            vertices.Add(item);
            vertices.Add(item2);
            vertices.Add(item3);
            vertices.Add(item);
            vertices.Add(item3);
            vertices.Add(item4);
        }
    }

    private static void AddLineQuad(List<SolidVertex> vertices, float x0, float y0, float x1, float y1, float thicknessPixels, System.Windows.Media.Color color, int pixelWidth, int pixelHeight)
    {
        if (IsFinite(x0) && IsFinite(y0) && IsFinite(x1) && IsFinite(y1))
        {
            float num = ResolveRenderedLineThickness(thicknessPixels);
            float num2 = x1 - x0;
            float num3 = y1 - y0;
            float num4 = MathF.Sqrt(num2 * num2 + num3 * num3);
            if (num4 < 0.001f)
            {
                float num5 = num / 2f;
                AddSolidQuad(vertices, x0 - num5, y0 - num5, x0 + num5, y0 + num5, color, pixelWidth, pixelHeight);
                return;
            }
            float num6 = num / 2f;
            float num7 = (0f - num3) / num4 * num6;
            float num8 = num2 / num4 * num6;
            SolidVertex item = CreateSolidVertex(x0 - num7, y0 - num8, color, pixelWidth, pixelHeight);
            SolidVertex item2 = CreateSolidVertex(x0 + num7, y0 + num8, color, pixelWidth, pixelHeight);
            SolidVertex item3 = CreateSolidVertex(x1 + num7, y1 + num8, color, pixelWidth, pixelHeight);
            SolidVertex item4 = CreateSolidVertex(x1 - num7, y1 - num8, color, pixelWidth, pixelHeight);
            vertices.Add(item);
            vertices.Add(item2);
            vertices.Add(item3);
            vertices.Add(item);
            vertices.Add(item3);
            vertices.Add(item4);
        }
    }

    private static void AddCircleQuad(List<CircleVertex> vertices, float centerX, float centerY, float radiusPixels, System.Windows.Media.Color color, int pixelWidth, int pixelHeight)
    {
        if (IsFinite(centerX) && IsFinite(centerY) && IsFinite(radiusPixels))
        {
            float num = ResolveRenderedScatterRadius(radiusPixels);
            float pixelX = centerX - num;
            float pixelY = centerY - num;
            float pixelX2 = centerX + num;
            float pixelY2 = centerY + num;
            CircleVertex item = CreateCircleVertex(pixelX, pixelY, -1f, -1f, color, pixelWidth, pixelHeight);
            CircleVertex item2 = CreateCircleVertex(pixelX2, pixelY, 1f, -1f, color, pixelWidth, pixelHeight);
            CircleVertex item3 = CreateCircleVertex(pixelX2, pixelY2, 1f, 1f, color, pixelWidth, pixelHeight);
            CircleVertex item4 = CreateCircleVertex(pixelX, pixelY2, -1f, 1f, color, pixelWidth, pixelHeight);
            vertices.Add(item);
            vertices.Add(item2);
            vertices.Add(item3);
            vertices.Add(item);
            vertices.Add(item3);
            vertices.Add(item4);
        }
    }

    private static void AddDataLineInstance(List<DataLineInstance> instances, float x0, float y0, float x1, float y1, System.Windows.Media.Color color)
    {
        if (IsFinite(x0) && IsFinite(y0) && IsFinite(x1) && IsFinite(y1))
        {
            instances.Add(new DataLineInstance(x0, y0, x1, y1, color));
        }
    }

    private static SolidVertex CreateSolidVertex(float pixelX, float pixelY, System.Windows.Media.Color color, int pixelWidth, int pixelHeight)
    {
        return new SolidVertex(PixelToClipX(pixelX, pixelWidth), PixelToClipY(pixelY, pixelHeight), color);
    }

    private static CircleVertex CreateCircleVertex(float pixelX, float pixelY, float localX, float localY, System.Windows.Media.Color color, int pixelWidth, int pixelHeight)
    {
        return new CircleVertex(PixelToClipX(pixelX, pixelWidth), PixelToClipY(pixelY, pixelHeight), localX, localY, color);
    }
}


