namespace AcDream.App.Rendering.Gpu;

internal interface IGpuBuffer : IDisposable
{
    string Name { get; }
    long SizeBytes { get; }
    GpuBufferUsage Usage { get; }
    GpuMemoryResidency Residency { get; }

    bool HostWritesAreCoherent { get; }

    void Upload(long offsetBytes, ReadOnlySpan<byte> data);

    void CopyTo(IGpuBuffer destination, long sourceOffsetBytes, long destinationOffsetBytes, long byteCount);

    void Read(long offsetBytes, Span<byte> destination);
}

/// <summary>A sampled texture or an attachment image.</summary>
internal interface IGpuTexture : IDisposable
{
    string Name { get; }
    GpuTextureKind Kind { get; }
    GpuTextureFormat Format { get; }
    int Width { get; }
    int Height { get; }
    int LayerCount { get; }
    int MipLevelCount { get; }

    /// <summary>
    /// Uploads one mip level of one array layer. <paramref name="data"/> is raw
    /// texels for uncompressed formats and raw blocks for BC formats.
    /// </summary>
    void Upload(int mipLevel, int layer, ReadOnlySpan<byte> data);

    void GenerateMipChain();
}

/// <summary>
/// Immutable sampler state, de-duplicated by description and owned by the
/// device for its whole lifetime: the same instance is handed to every
/// consumer that asks for the description, so no consumer may destroy it.
/// </summary>
internal interface IGpuSampler
{
    GpuSamplerDescription Description { get; }
}

internal interface IGpuPipeline : IDisposable
{
    GpuPipelineDescription Description { get; }
}

internal interface IGpuRenderTarget : IDisposable
{
    GpuRenderTargetDescription Description { get; }

    IGpuTexture ColorTexture { get; }

    IGpuTexture? DepthTexture { get; }
}

internal interface IGpuDirectionalDepthTarget : IDisposable
{
    GpuDirectionalDepthTargetDescription Description { get; }

    IGpuTexture DepthTexture { get; }
}

internal interface IGpuTimerPool
{
    /// <summary>True when the backend can measure GPU time at all.</summary>
    bool IsSupported { get; }

    /// <summary>Milliseconds measured for <paramref name="scopeName"/> in the most recent retired frame.</summary>
    bool TryResolve(string scopeName, out double milliseconds);

    bool TryTakeResolved(string scopeName, out double milliseconds);

    /// <summary>Counts completed read-backs; a reader that wants each measured
    /// frame once compares this against what it saw last.</summary>
    int ResolveGeneration { get; }

    /// <summary>The ranges the last read-back covered, in record order.</summary>
    IReadOnlyList<(string Name, double Milliseconds)> LastResolved { get; }
}
