using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Prospect.Engine.IO;

namespace Prospect.Client.Render;

/// <summary>A GPU buffer and its allocation.</summary>
internal unsafe struct GpuBuffer
{
    public VkBuffer Buffer;
    public VmaAllocation Allocation;

    /// <summary>Where the buffer is mapped for writing, for buffers created with <see cref="CreateMapped"/>.</summary>
    public nint Mapped;

    /// <summary>A buffer the CPU rewrites every frame, kept mapped.</summary>
    public static GpuBuffer CreateMapped(GraphicsDevice gpu, VkBufferUsageFlags usage, int bytes)
    {
        var result = gpu.Allocator.CreateBuffer(
            new VkBufferCreateInfo { Size = new ByteSize64((ulong)bytes), Usage = usage },
            new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.Auto,
                Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit,
                // Frame writes and staging uploads do not issue explicit allocation flushes.
                RequiredFlags = VkMemoryPropertyFlags.HostCoherentBit,
            },
            out var buffer,
            out var allocation,
            out var info
        );
        if (result != VkResult.Success)
            throw new VkResultError(result, "Could not allocate mapped GPU buffer.");
        return new GpuBuffer
        {
            Buffer = buffer,
            Allocation = allocation,
            Mapped = (nint)info.PMappedData,
        };
    }

    /// <summary>A buffer in GPU memory filled once with the given data. Blocks until the upload is done.</summary>
    public static GpuBuffer CreateStatic<T>(GraphicsDevice gpu, VkBufferUsageFlags usage, ReadOnlySpan<T> data)
        where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        var staging = CreateMapped(gpu, VkBufferUsageFlags.TransferSrcBit, bytes.Length);
        var destination = default(GpuBuffer);
        var device = gpu.Device;
        var pool = VkCommandPool.NullHandle;
        var submitted = false;
        try
        {
            bytes.CopyTo(new Span<byte>((void*)staging.Mapped, bytes.Length));
            var result = gpu.Allocator.CreateBuffer(
                new VkBufferCreateInfo { Size = new ByteSize64((ulong)bytes.Length), Usage = usage | VkBufferUsageFlags.TransferDstBit },
                new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto },
                out var buffer, out var allocation, out _
            );
            if (result != VkResult.Success)
                throw new VkResultError(result, "Could not allocate static GPU buffer.");
            destination = new GpuBuffer { Buffer = buffer, Allocation = allocation };
            pool = device.CreateCommandPool(
                new VkCommandPoolCreateInfo { QueueFamilyIndex = gpu.QueueFamily, Flags = VkCommandPoolCreateFlags.TransientBit }, null);
            var commands = device.AllocateCommandBuffer(
                new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary });
            commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
            commands.CopyBuffer(staging.Buffer, buffer, [new VkBufferCopy { Size = new ByteSize64((ulong)bytes.Length) }]);
            commands.End();
            gpu.Queue.Submit([], [], [commands], [], VkFence.NullHandle);
            submitted = true;
            gpu.Queue.WaitIdle();
            submitted = false;
            var uploaded = destination;
            destination = default;
            return uploaded;
        }
        catch (VkResultError error) when (error.Result == VkResult.ErrorDeviceLost)
        {
            // A lost device cannot execute more work; its resources may be released during unwinding.
            submitted = false;
            throw;
        }
        finally
        {
            // Never free a staging buffer or command pool while a submitted copy can still use it.
            if (submitted)
            {
                try { gpu.Queue.WaitIdle(); }
                catch (VkResultError error) when (error.Result == VkResult.ErrorDeviceLost)
                {
                    // Finish cleanup after device loss, then propagate the original upload failure.
                }
            }
            if (!pool.IsNull()) device.DestroyCommandPool(pool, null);
            staging.Destroy(gpu);
            destination.Destroy(gpu);
        }
    }

    public void Destroy(GraphicsDevice gpu)
    {
        if (!Buffer.IsNull())
            gpu.Allocator.DestroyBuffer(Buffer, Allocation);
        this = default;
    }
}

/// <summary>Compiles the game's GLSL shaders to SPIR-V at startup.</summary>
internal static class ShaderCompiler
{
    /// <summary>
    /// Compiles one shader from <c>assets/shaders</c>. The stage comes from the file extension. Safe to call from
    /// several threads at once; each call uses its own compiler.
    /// </summary>
    public static byte[] Compile(string fileName)
    {
        var kind = Path.GetExtension(fileName) switch
        {
            ".vert" => ShaderKind.VertexShader,
            ".frag" => ShaderKind.FragmentShader,
            _ => throw new ArgumentException($"Unknown shader stage: {fileName}"),
        };

        var source = System.Text.Encoding.UTF8.GetBytes(Preprocess(fileName, 0));
        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();
        using var result = compiler.Compile(source, kind, System.Text.Encoding.UTF8.GetBytes(fileName + '\0'), "main"u8, options);
        if (result.GetCompilationStatus() != CompilationStatus.Success)
            throw new InvalidDataException($"Shader {fileName} failed to compile:\n{result.GetErrorMessage()}");

        unsafe
        {
            return new ReadOnlySpan<byte>(result.GetBytes(), (int)result.GetLength()).ToArray();
        }
    }

    /// <summary>Reads a shader file and splices in the files it includes.</summary>
    private static string Preprocess(string fileName, int depth)
    {
        if (depth > 8)
            throw new InvalidDataException($"Shader include nesting too deep at {fileName}");

        var text = new System.Text.StringBuilder();
        foreach (var line in File.ReadLines(AppPaths.Asset("shaders", fileName)))
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.StartsWith("#include"))
            {
                var open = line.IndexOf('"');
                var close = line.LastIndexOf('"');
                if (open < 0 || close <= open)
                    throw new InvalidDataException($"Malformed #include in {fileName}: {line}");
                text.AppendLine(Preprocess(line[(open + 1)..close], depth + 1));
            }
            else
            {
                text.AppendLine(line);
            }
        }

        return text.ToString();
    }
}
