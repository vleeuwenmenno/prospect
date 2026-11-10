using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Prospect.Engine.Diagnostics;

namespace Prospect.Client.Render;

/// <summary>The Vulkan instance, the chosen GPU, its logical device, one graphics queue and the memory allocator.</summary>
internal sealed unsafe class GraphicsDevice : IDisposable
{
    private readonly VkDebugUtilsMessengerEXT _debugMessenger;
    private static int _validationErrors;

    public GraphicsDevice(GlfwWindow window, bool enableValidation)
    {
        // GLFW cannot find BRUTAL's Vulkan loader by itself; touching Vulkan first loads it.
        _ = Vulkan.GetInstanceProcAddr;

        var instanceExtensions = new HashSet<string>();
        VulkanHelpers.AddPlatformRequiredExtensions(instanceExtensions);
        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var glfwExtensionCount);
        for (var i = 0; i < glfwExtensionCount; i++)
            instanceExtensions.Add(Utf8.GetString(glfwExtensions[i]));

        var layers = new HashSet<string>();
        if (enableValidation)
        {
            if (VulkanHelpers.Validation.IsSupported)
            {
                VulkanHelpers.AddValidationLayers(layers);
                instanceExtensions.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
                ValidationEnabled = true;
            }
            else
            {
                Logs.Render.Warning(
                    "Vulkan validation was requested but VK_LAYER_KHRONOS_validation is not installed; running without it"
                );
            }
        }

        Instance = Vulkan.CreateInstance(
            new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo { ApiVersion = (int)VulkanHelpers.Api.VERSION_1_3 },
                Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                EnabledExtensions = instanceExtensions,
                EnabledLayers = layers,
            }
        );

        if (ValidationEnabled)
        {
            var messengerInfo = new VkDebugUtilsMessengerCreateInfoEXT
            {
                MessageSeverity =
                    VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT,
                MessageType =
                    VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT
                    | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT
                    | VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT,
                PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&OnDebugMessage),
            };
            _debugMessenger = Instance.CreateDebugUtilsMessengerEXT(messengerInfo, null);
        }

        if (Glfw.CreateWindowSurface(Instance.VkHandle, window, out var surfaceHandle) != (int)VkResult.Success)
            throw new InvalidOperationException("Failed to create a Vulkan surface for the window");
        Surface = new VkSurfaceKHR((nint)surfaceHandle);

        (PhysicalDevice, QueueFamily) = PickPhysicalDevice();
        var properties = PhysicalDevice.GetProperties();
        DeviceName = Utf8.GetString(properties.DeviceName);

        var deviceExtensions = new HashSet<string>();
        VulkanHelpers.AddSwapchainExtensions(deviceExtensions);
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(deviceExtensions, PhysicalDevice);

        var priority = 1.0f;
        var vulkan13 = new VkPhysicalDeviceVulkan13Features { DynamicRendering = true, Synchronization2 = true };
        Device = PhysicalDevice.CreateDevice(
            new DeviceEx.CreateInfo
            {
                Extensions = deviceExtensions,
                QueueCreateInfos =
                [
                    new VkDeviceQueueCreateInfo
                    {
                        QueueFamilyIndex = QueueFamily,
                        QueueCount = 1,
                        QueuePriorities = &priority,
                    },
                ],
                Next = &vulkan13,
            }
        );
        Queue = Device.GetQueue(QueueFamily, 0);

        Allocator = Device.CreateVmaAllocator(
            new VmaAllocatorCreateInfo
            {
                Instance = Instance,
                PhysicalDevice = PhysicalDevice,
                Device = Device,
                VulkanApiVersion = (uint)VulkanHelpers.Api.VERSION_1_3,
            }
        );

        Logs.Render.Info($"GPU: {DeviceName}, Vulkan 1.3, validation {(ValidationEnabled ? "on" : "off")}");
    }

    public Instance Instance { get; }
    public VkSurfaceKHR Surface { get; }
    public PhysicalDevice PhysicalDevice { get; }
    public DeviceEx Device { get; }
    public int QueueFamily { get; }
    public Queue Queue { get; }
    public VmaAllocator Allocator { get; }
    public string DeviceName { get; }
    public bool ValidationEnabled { get; }

    /// <summary>Validation errors reported by the driver layers since startup. Always zero in a correct build.</summary>
    public static int ValidationErrorCount => Volatile.Read(ref _validationErrors);

    public void Dispose()
    {
        Device.WaitIdle();
        Device.DestroyVmaAllocator(Allocator);
        Device.Dispose();
        Instance.DestroySurfaceKHR(Surface, null);
        if (ValidationEnabled)
            Instance.DestroyDebugUtilsMessengerEXT(_debugMessenger, null);
        Instance.Dispose();
    }

    private (PhysicalDevice Device, int QueueFamily) PickPhysicalDevice()
    {
        PhysicalDevice? best = null;
        var bestFamily = -1;
        var bestScore = -1;

        foreach (var candidate in Instance.PhysicalDevices)
        {
            var properties = candidate.GetProperties();
            if (properties.ApiVersion < (int)VulkanHelpers.Api.VERSION_1_3)
                continue;

            var families = new VkQueueFamilyProperties[candidate.GetQueueFamilyProperties_Count()];
            candidate.GetQueueFamilyProperties_Write(families);
            var family = -1;
            for (var i = 0; i < families.Length && family < 0; i++)
            {
                if ((families[i].QueueFlags & VkQueueFlags.GraphicsBit) != 0 && candidate.GetSurfaceSupportKHR(i, Surface))
                    family = i;
            }

            if (family < 0)
                continue;

            var score = properties.DeviceType switch
            {
                VkPhysicalDeviceType.DiscreteGpu => 3,
                VkPhysicalDeviceType.IntegratedGpu => 2,
                VkPhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };
            if (score > bestScore)
                (best, bestFamily, bestScore) = (candidate, family, score);
        }

        if (best is null)
            throw new InvalidOperationException(
                "No GPU supports Vulkan 1.3 with a queue that can draw and present to the window. Update the graphics driver."
            );
        return (best, bestFamily);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 OnDebugMessage(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData
    )
    {
        var message = Utf8.GetString(data->Message);
        if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0)
        {
            Interlocked.Increment(ref _validationErrors);
            Logs.Render.Error($"Vulkan validation: {message}");
        }
        else
        {
            Logs.Render.Warning($"Vulkan validation: {message}");
        }

        return false;
    }
}
