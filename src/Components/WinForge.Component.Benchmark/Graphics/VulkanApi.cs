using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Bindings a <c>vulkan-1.dll</c>, escritos a mano y SOLO con lo que este backend usa.
///
/// Por qué a mano y no con un paquete de bindings: el componente ya declara esa regla para
/// Direct3D (usa las DLL del sistema y ningún binario propio), y el cargador de Vulkan de
/// Windows exporta TODOS los comandos del núcleo, así que <c>DllImport</c> alcanza. Un paquete
/// de bindings completo agregaría megabytes al zip del componente para usar treinta funciones,
/// y estas estructuras son todas "blittables": un struct de Vulkan y su versión de C# tienen la
/// misma memoria. Por eso los campos conservan los nombres de la especificación: quien dude de
/// un offset lo puede mirar en el spec y ver el mismo nombre.
/// </summary>
internal static class VulkanApi
{
    internal const string Library = "vulkan-1.dll";

    /// <summary>Tope de espera de las vallas: sin límite, el driver manda.</summary>
    internal const ulong UInt64Max = ulong.MaxValue;

    // =====================================================================
    // Disponibilidad
    // =====================================================================

    /// <summary>
    /// ¿Hay un cargador de Vulkan instalado? Se pregunta por el ARCHIVO y no probando crear una
    /// instancia: "faltan los drivers" y "el driver no arrancó" son dos motivos distintos y el
    /// selector de la página tiene que poder decir cuál es.
    /// </summary>
    internal static bool IsLoaderPresent
    {
        get
        {
            try
            {
                string system32 = Environment.SystemDirectory;
                if (!string.IsNullOrEmpty(system32) && File.Exists(Path.Combine(system32, Library))) return true;
                // En Windows ARM64/ARM64EC el cargador puede estar en System32 aunque el proceso
                // sea x64: si no está ahí, se prueba el directorio del sistema del proceso.
                return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), Library));
            }
            catch { return false; }
        }
    }

    // =====================================================================
    // Constantes de offsets (VkPhysicalDeviceProperties)
    // =====================================================================

    // VkPhysicalDeviceProperties es enorme (nombre de 256 bytes + las ~120 capacidades del
    // dispositivo + sparse). En vez de declararlo entero —y arriesgar un tamaño mal calculado,
    // que sería corrupción de memoria porque Vulkan ESCRIBE el struct completo—, se reserva un
    // bloque con sobra y se leen los tres datos que hacen falta por su offset del spec.
    // Orden real de VkPhysicalDeviceProperties (ojo: deviceName va ANTES del uuid de la caché):
    //   apiVersion(0) driverVersion(4) vendorID(8) deviceID(12) deviceType(16)
    //   deviceName[256] en 20 · pipelineCacheUUID[16] en 276 · 4 de relleno · limits en 296
    //   · sparseProperties. El relleno NO es opcional: limits arranca con dos uint64 (los
    //   VkDeviceSize) y el compilador alinea el struct a 8 bytes, así que 292 se corre a 296.
    internal const int PropertiesBufferSize = 4096;
    internal const int DeviceNameOffset = 20;
    internal const int DeviceNameSize = 256;               // VK_MAX_PHYSICAL_DEVICE_NAME_SIZE
    internal const int PipelineCacheUuidSize = 16;         // VK_UUID_SIZE
    internal const int LimitsAlignment = 4;                // 276 + 16 = 292 → 296 (alineación a 8)
    internal const int LimitsOffset = DeviceNameOffset + DeviceNameSize + PipelineCacheUuidSize + LimitsAlignment;   // 296
    /// <summary>
    /// Dentro de VkPhysicalDeviceLimits: los dos únicos datos de ese struct enorme que la escena usa.
    /// Verificado contra la placa con un volcado: en 420 está timestampComputeAndGraphics (1 = sí) y
    /// en 424 timestampPeriod (1,0 en la GeForce medida); a continuación vienen maxClipDistances,
    /// maxCullDistances, maxCombinedClipAndCullDistances, discreteQueuePriorities y pointSizeRange
    /// con los valores que reporta NVIDIA, así que la posición no es casualidad. Con estos dos
    /// offsets mal (estaban 12 bytes antes) el tiempo de GPU de Vulkan salía siempre 0,00 ms: se
    /// leía un float sin sentido en vez del período de los timestamps.
    /// </summary>
    internal const int TimestampComputeAndGraphicsOffset = LimitsOffset + 420;
    internal const int TimestampPeriodOffset = LimitsOffset + 424;

    // VkPhysicalDeviceMemoryProperties: uint32 memoryTypeCount; VkMemoryType[32]; uint32
    // memoryHeapCount; VkMemoryHeap[32]. Se reserva con sobra y se leen los montones por offset.
    internal const int MemoryPropertiesBufferSize = 2048;
    internal const int MemoryTypeCountOffset = 0;
    internal const int MemoryTypesOffset = 4;             // VkMemoryType = 8 bytes (flags + heapIndex)
    internal const int MemoryHeapCountOffset = 260;       // 4 + 32 * 8
    internal const int MemoryHeapsOffset = 264;           // VkMemoryHeap = 16 bytes (size + flags + padding)

    /// <summary>VkMemoryHeap::size (bytes).</summary>
    internal const int MemoryHeapSizeOffset = 0;

    /// <summary>VkMemoryHeap::flags: VK_MEMORY_HEAP_DEVICE_LOCAL_BIT.</summary>
    internal const int MemoryHeapFlagsOffset = 8;
    internal const int MemoryHeapDeviceLocal = 1;

    /// <summary>Tamaño de VkQueueFamilyProperties: flags, queueCount, timestampValidBits y el granularity.</summary>
    internal const int QueueFamilyPropertiesSize = 24;

    /// <summary>VkQueueFamilyProperties::timestampValidBits.</summary>
    internal const int QueueFamilyTimestampValidBitsOffset = 8;

    // =====================================================================
    // Enums (los valores son los del spec)
    // =====================================================================

    internal static class VkResult
    {
        internal const int Success = 0;
        internal const int NotReady = 1;
        internal const int Timeout = 2;
        internal const int SuboptimalKhr = 1000001003;
        internal const int ErrorOutOfHostMemory = -1;
        internal const int ErrorDeviceLost = -4;
        internal const int ErrorSurfaceLostKhr = -1000000000;
        internal const int ErrorOutOfDateKhr = -1000001004;
    }

    internal static class VkStructureType
    {
        internal const int ApplicationInfo = 0;
        internal const int InstanceCreateInfo = 1;
        internal const int DeviceQueueCreateInfo = 2;
        internal const int DeviceCreateInfo = 3;
        internal const int SubmitInfo = 4;
        internal const int MemoryAllocateInfo = 5;
        internal const int FenceCreateInfo = 8;
        internal const int SemaphoreCreateInfo = 9;
        internal const int QueryPoolCreateInfo = 11;
        internal const int BufferCreateInfo = 12;
        internal const int ImageCreateInfo = 14;
        internal const int ImageViewCreateInfo = 15;
        internal const int ShaderModuleCreateInfo = 16;
        internal const int SamplerCreateInfo = 31;
        internal const int ImageMemoryBarrier = 45;
        internal const int PipelineShaderStageCreateInfo = 18;
        internal const int PipelineVertexInputStateCreateInfo = 19;
        internal const int PipelineInputAssemblyStateCreateInfo = 20;
        internal const int PipelineViewportStateCreateInfo = 22;
        internal const int PipelineRasterizationStateCreateInfo = 23;
        internal const int PipelineMultisampleStateCreateInfo = 24;
        internal const int PipelineDepthStencilStateCreateInfo = 25;
        internal const int PipelineColorBlendStateCreateInfo = 26;
        internal const int PipelineDynamicStateCreateInfo = 27;
        internal const int GraphicsPipelineCreateInfo = 28;
        internal const int PipelineLayoutCreateInfo = 30;
        internal const int DescriptorSetLayoutCreateInfo = 32;
        internal const int DescriptorPoolCreateInfo = 33;
        internal const int DescriptorSetAllocateInfo = 34;
        internal const int WriteDescriptorSet = 35;
        internal const int FramebufferCreateInfo = 37;
        internal const int RenderPassCreateInfo = 38;
        internal const int CommandPoolCreateInfo = 39;
        internal const int CommandBufferAllocateInfo = 40;
        internal const int CommandBufferBeginInfo = 42;
        internal const int RenderPassBeginInfo = 43;
        internal const int BufferMemoryBarrier = 44;
        internal const int Win32SurfaceCreateInfoKhr = 1000009000;
        internal const int SwapchainCreateInfoKhr = 1000001000;
        internal const int PresentInfoKhr = 1000001001;
    }

    internal static class VkFormat
    {
        internal const int Undefined = 0;
        internal const int R8G8B8A8Unorm = 37;
        internal const int R8G8B8A8Srgb = 43;
        internal const int B8G8R8A8Unorm = 44;
        internal const int B8G8R8A8Srgb = 50;
        // VK_FORMAT_R32G32_SFLOAT: la UV del vértice (ver el input layout del pipeline).
        internal const int R32G32Sfloat = 103;
        internal const int R32G32B32Sfloat = 106;
        internal const int R32G32B32A32Sfloat = 109;
        internal const int D16Unorm = 124;
        internal const int D32Sfloat = 126;
        internal const int D24UnormS8Uint = 129;
        internal const int D32SfloatS8Uint = 130;
    }

    internal static class VkImageTiling
    {
        internal const int Optimal = 0;
        internal const int Linear = 1;
    }

    internal static class VkImageType
    {
        internal const int Type1D = 0;
        internal const int Type2D = 1;
        internal const int Type3D = 2;
    }

    internal static class VkImageViewType
    {
        internal const int Type2D = 1;
        // VK_IMAGE_VIEW_TYPE_2D_ARRAY: el arreglo de materiales de la escena (ver Scenes.MaterialAtlas).
        internal const int Type2DArray = 5;
    }

    internal static class VkImageLayout
    {
        internal const int Undefined = 0;
        internal const int ColorAttachmentOptimal = 2;
        internal const int DepthStencilAttachmentOptimal = 3;
        internal const int ShaderReadOnlyOptimal = 5;
        internal const int TransferDstOptimal = 7;
        internal const int PresentSrcKhr = 1000001002;
    }

    internal static class VkSharingMode
    {
        internal const int Exclusive = 0;
        internal const int Concurrent = 1;
    }

    internal static class VkImageUsage
    {
        internal const int TransferSrc = 1;
        internal const int TransferDst = 2;
        internal const int Sampled = 4;
        internal const int ColorAttachment = 16;
        internal const int DepthStencilAttachment = 32;
    }

    internal static class VkFilter
    {
        internal const int Nearest = 0;
        internal const int Linear = 1;
    }

    internal static class VkSamplerMipmapMode
    {
        internal const int Nearest = 0;
        internal const int Linear = 1;
    }

    internal static class VkSamplerAddressMode
    {
        internal const int Repeat = 0;
        internal const int MirroredRepeat = 1;
        internal const int ClampToEdge = 2;
        internal const int ClampToBorder = 3;
    }

    internal static class VkBorderColor
    {
        internal const int FloatTransparentBlack = 0;
        internal const int FloatOpaqueBlack = 1;
    }

    internal static class VkImageAspect
    {
        internal const int Color = 1;
        internal const int Depth = 2;
    }

    internal static class VkAttachmentLoadOp
    {
        internal const int Load = 0;
        internal const int Clear = 1;
        internal const int DontCare = 2;
    }

    internal static class VkAttachmentStoreOp
    {
        internal const int Store = 0;
        internal const int DontCare = 1;
    }

    internal static class VkPresentMode
    {
        internal const int Immediate = 0;
        internal const int Mailbox = 1;
        internal const int Fifo = 2;
        internal const int FifoRelaxed = 3;
    }

    internal static class VkCompositeAlpha
    {
        internal const int Opaque = 1;
        internal const int Premultiplied = 2;
        internal const int Postmultiplied = 4;
        internal const int Inherit = 8;
    }

    internal static class VkSurfaceTransform
    {
        internal const int Identity = 1;
    }

    internal static class VkColorSpace
    {
        internal const int SrgbNonlinear = 0;
    }

    internal static class VkQueueFlag
    {
        internal const int Graphics = 1;
        internal const int Compute = 2;
        internal const int Transfer = 4;
    }

    internal static class VkPipelineBindPoint
    {
        internal const int Graphics = 0;
    }

    internal static class VkPipelineStage
    {
        internal const int TopOfPipe = 1;
        internal const int VertexInput = 4;
        internal const int FragmentShader = 8;
        /// <summary>VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT: donde corre la prueba de profundidad.
        /// Lo usa la dependencia del render pass de sombras (ver README-SOMBRAS.md).</summary>
        internal const int EarlyFragmentTests = 256;
        /// <summary>VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT: donde se ESCRIBE la profundidad.</summary>
        internal const int LateFragmentTests = 512;
        internal const int ColorAttachmentOutput = 1024;
        internal const int Transfer = 4096;
        internal const int BottomOfPipe = 8192;
    }

    internal static class VkAccess
    {
        internal const int VertexAttributeRead = 4;
        internal const int ShaderRead = 32;
        internal const int ColorAttachmentWrite = 256;
        /// <summary>VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT: la escritura que hace la pasada de
        /// sombras sobre su mapa de profundidad.</summary>
        internal const int DepthStencilAttachmentWrite = 1024;
        internal const int TransferWrite = 4096;
    }

    internal static class VkBufferUsage
    {
        internal const int TransferSrc = 1;
        internal const int TransferDst = 2;
        internal const int UniformBuffer = 16;
        internal const int StorageBuffer = 32;
        internal const int VertexBuffer = 128;
    }

    internal static class VkMemoryProperty
    {
        internal const int DeviceLocal = 1;
        internal const int HostVisible = 2;
        internal const int HostCoherent = 4;
    }

    internal static class VkDescriptorType
    {
        internal const int UniformBuffer = 6;
        internal const int StorageBuffer = 7;
        // La textura de detalle: en Vulkan la imagen y el sampler son DOS descriptores
        // distintos (el mismo modelo del HLSL: register t0 y register s0). SampledImage es 2
        // (0 = Sampler, 1 = CombinedImageSampler); un tipo fuera de tabla revienta al escribir
        // el descriptor.
        internal const int SampledImage = 2;
        internal const int Sampler = 0;
    }

    internal static class VkShaderStage
    {
        internal const int Vertex = 1;
        internal const int Fragment = 16;
    }

    internal static class VkPrimitiveTopology
    {
        internal const int TriangleList = 3;
    }

    internal static class VkPolygonMode
    {
        internal const int Fill = 0;
    }

    internal static class VkCullMode
    {
        internal const int None = 0;
    }

    internal static class VkFrontFace
    {
        internal const int CounterClockwise = 0;
    }

    internal static class VkCompareOp
    {
        internal const int Never = 0;
        internal const int Less = 1;
        internal const int Always = 7;
    }

    /// <summary>
    /// VkBlendFactor / VkBlendOp del blend de la franja de métricas: One / OneMinusSourceAlpha
    /// sobre colores YA premultiplicados, el mismo que usa Direct3D 11. En OpenGL son
    /// GL_ONE / GL_ONE_MINUS_SRC_ALPHA y en Direct3D 12, Blend.One / Blend.InverseSourceAlpha.
    /// </summary>
    internal static class VkBlendFactor
    {
        internal const int One = 1;
        internal const int OneMinusSourceAlpha = 7;
    }

    internal static class VkBlendOp
    {
        internal const int Add = 0;
    }

    internal static class VkStencilOp
    {
        internal const int Keep = 0;
    }

    internal static class VkSampleCount
    {
        internal const int Count1 = 1;
    }

    internal static class VkVertexInputRate
    {
        internal const int Vertex = 0;
        internal const int Instance = 1;
    }

    internal static class VkDynamicState
    {
        internal const int Viewport = 0;
        internal const int Scissor = 1;
    }

    internal static class VkCommandBufferLevel
    {
        internal const int Primary = 0;
    }

    internal static class VkCommandBufferUsage
    {
        internal const int OneTimeSubmit = 1;
    }

    internal static class VkCommandPoolCreate
    {
        internal const int ResetCommandBuffer = 2;
    }

    internal static class VkFenceCreate
    {
        internal const int Signaled = 1;
    }

    internal static class VkSubpassContents
    {
        internal const int Inline = 0;
    }

    internal static class VkQueryType
    {
        internal const int Timestamp = 2;
    }

    internal static class VkQueryResult
    {
        internal const int QueryResult64Bit = 1;
        internal const int Wait = 2;
    }

    internal static class VkDeviceType
    {
        internal const int Other = 0;
        internal const int IntegratedGpu = 1;
        internal const int DiscreteGpu = 2;
        internal const int VirtualGpu = 3;
        internal const int Cpu = 4;
    }

    /// <summary>Bit de VkColorComponentFlagBits: los cuatro canales (D3D11 también escribe los cuatro).</summary>
    internal const int ColorComponentAll = 0xF;

    // =====================================================================
    // Structs
    // =====================================================================

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkExtent2D
    {
        public uint width;
        public uint height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkOffset2D
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkRect2D
    {
        public VkOffset2D offset;
        public VkExtent2D extent;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkViewport
    {
        public float x;
        public float y;
        public float width;
        public float height;
        public float minDepth;
        public float maxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkExtent3D
    {
        public uint width;
        public uint height;
        public uint depth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkOffset3D
    {
        public int x;
        public int y;
        public int z;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkImageSubresourceLayers
    {
        public int aspectMask;
        public uint mipLevel;
        public uint baseArrayLayer;
        public uint layerCount;
    }

    /// <summary>Región de copia buffer → imagen (ver CreateDetailTexture en el backend).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VkBufferImageCopy
    {
        public ulong bufferOffset;
        public uint bufferRowLength;
        public uint bufferImageHeight;
        public VkImageSubresourceLayers imageSubresource;
        public VkOffset3D imageOffset;
        public VkExtent3D imageExtent;
    }

    /// <summary>Barrera de layout de una imagen (la textura de detalle arranca en Undefined).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VkImageMemoryBarrier
    {
        public int sType;
        public nint pNext;
        public int srcAccessMask;
        public int dstAccessMask;
        public int oldLayout;
        public int newLayout;
        public uint srcQueueFamilyIndex;
        public uint dstQueueFamilyIndex;
        public nint image;
        public VkImageSubresourceRange subresourceRange;
    }

    /// <summary>
    /// Sampler de la textura de detalle: filtrado lineal, wrap en las dos direcciones y sin
    /// anisotropía (el detalle es un patrón, no una foto). La anisotropía necesita la feature
    /// samplerAnisotropy, que no se pide al crear el dispositivo.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSamplerCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public int magFilter;
        public int minFilter;
        public int mipmapMode;
        public int addressModeU;
        public int addressModeV;
        public int addressModeW;
        public float mipLodBias;
        public uint anisotropyEnable;
        public float maxAnisotropy;
        public uint compareEnable;
        public int compareOp;
        public float minLod;
        public float maxLod;
        public int borderColor;
        public uint unnormalizedCoordinates;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkApplicationInfo
    {
        public int sType;
        public nint pNext;
        public nint pApplicationName;
        public uint applicationVersion;
        public nint pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkInstanceCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public nint pApplicationInfo;
        public uint enabledLayerCount;
        public nint ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public nint ppEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkWin32SurfaceCreateInfoKHR
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public nint hinstance;
        public nint hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkDeviceQueueCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint queueFamilyIndex;
        public uint queueCount;
        public nint pQueuePriorities;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkDeviceCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint queueCreateInfoCount;
        public nint pQueueCreateInfos;
        public uint enabledLayerCount;
        public nint ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public nint ppEnabledExtensionNames;
        public nint pEnabledFeatures;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSurfaceCapabilitiesKHR
    {
        public uint minImageCount;
        public uint maxImageCount;
        public VkExtent2D currentExtent;
        public VkExtent2D minImageExtent;
        public VkExtent2D maxImageExtent;
        public uint maxImageArrayLayers;
        public uint supportedTransforms;
        public uint currentTransform;
        public uint supportedCompositeAlpha;
        public uint supportedUsageFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSurfaceFormatKHR
    {
        public int format;
        public int colorSpace;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSwapchainCreateInfoKHR
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public nint surface;
        public uint minImageCount;
        public int imageFormat;
        public int imageColorSpace;
        public VkExtent2D imageExtent;
        public uint imageArrayLayers;
        public int imageUsage;
        public int imageSharingMode;
        public uint queueFamilyIndexCount;
        public nint pQueueFamilyIndices;
        public int preTransform;
        public int compositeAlpha;
        public int presentMode;
        public uint clipped;
        public nint oldSwapchain;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkComponentMapping
    {
        public int r;
        public int g;
        public int b;
        public int a;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkImageSubresourceRange
    {
        public int aspectMask;
        public uint baseMipLevel;
        public uint levelCount;
        public uint baseArrayLayer;
        public uint layerCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkImageViewCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public nint image;
        public int viewType;
        public int format;
        public VkComponentMapping components;
        public VkImageSubresourceRange subresourceRange;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkImageCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public int imageType;
        public int format;
        public VkExtent3D extent;
        public uint mipLevels;
        public uint arrayLayers;
        public int samples;
        public int tiling;
        public int usage;
        public int sharingMode;
        public uint queueFamilyIndexCount;
        public nint pQueueFamilyIndices;
        public int initialLayout;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkAttachmentDescription
    {
        public uint flags;
        public int format;
        public int samples;
        public int loadOp;
        public int storeOp;
        public int stencilLoadOp;
        public int stencilStoreOp;
        public int initialLayout;
        public int finalLayout;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkAttachmentReference
    {
        public uint attachment;
        public int layout;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSubpassDescription
    {
        public uint flags;
        public int pipelineBindPoint;
        public uint inputAttachmentCount;
        public nint pInputAttachments;
        public uint colorAttachmentCount;
        public nint pColorAttachments;
        public nint pResolveAttachments;
        public nint pDepthStencilAttachment;
        public uint preserveAttachmentCount;
        public nint pPreserveAttachments;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSubpassDependency
    {
        public uint srcSubpass;
        public uint dstSubpass;
        public int srcStageMask;
        public int dstStageMask;
        public int srcAccessMask;
        public int dstAccessMask;
        public int dependencyFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkRenderPassCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint attachmentCount;
        public nint pAttachments;
        public uint subpassCount;
        public nint pSubpasses;
        public uint dependencyCount;
        public nint pDependencies;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkFramebufferCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public nint renderPass;
        public uint attachmentCount;
        public nint pAttachments;
        public uint width;
        public uint height;
        public uint layers;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkShaderModuleCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public nuint codeSize;
        public nint pCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineShaderStageCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public int stage;
        public nint module;
        public nint pName;
        public nint pSpecializationInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkVertexInputBindingDescription
    {
        public uint binding;
        public uint stride;
        public int inputRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkVertexInputAttributeDescription
    {
        public uint location;
        public uint binding;
        public int format;
        public uint offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineVertexInputStateCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint vertexBindingDescriptionCount;
        public nint pVertexBindingDescriptions;
        public uint vertexAttributeDescriptionCount;
        public nint pVertexAttributeDescriptions;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineInputAssemblyStateCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public int topology;
        public uint primitiveRestartEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineViewportStateCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint viewportCount;
        public nint pViewports;
        public uint scissorCount;
        public nint pScissors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineRasterizationStateCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint depthClampEnable;
        public uint rasterizerDiscardEnable;
        public int polygonMode;
        public int cullMode;
        public int frontFace;
        public uint depthBiasEnable;
        public float depthBiasConstantFactor;
        public float depthBiasClamp;
        public float depthBiasSlopeFactor;
        public float lineWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineMultisampleStateCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public int rasterizationSamples;
        public uint sampleShadingEnable;
        public float minSampleShading;
        public nint pSampleMask;
        public uint alphaToCoverageEnable;
        public uint alphaToOneEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkStencilOpState
    {
        public int failOp;
        public int passOp;
        public int depthFailOp;
        public int compareOp;
        public uint compareMask;
        public uint writeMask;
        public uint reference;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineDepthStencilStateCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint depthTestEnable;
        public uint depthWriteEnable;
        public int depthCompareOp;
        public uint depthBoundsTestEnable;
        public uint stencilTestEnable;
        public VkStencilOpState front;
        public VkStencilOpState back;
        public float minDepthBounds;
        public float maxDepthBounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineColorBlendAttachmentState
    {
        public uint blendEnable;
        public int srcColorBlendFactor;
        public int dstColorBlendFactor;
        public int colorBlendOp;
        public int srcAlphaBlendFactor;
        public int dstAlphaBlendFactor;
        public int alphaBlendOp;
        public int colorWriteMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineColorBlendStateCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint logicOpEnable;
        public int logicOp;
        public uint attachmentCount;
        public nint pAttachments;
        public float blendConstant0;
        public float blendConstant1;
        public float blendConstant2;
        public float blendConstant3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineDynamicStateCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint dynamicStateCount;
        public nint pDynamicStates;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkGraphicsPipelineCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint stageCount;
        public nint pStages;
        public nint pVertexInputState;
        public nint pInputAssemblyState;
        public nint pTessellationState;
        public nint pViewportState;
        public nint pRasterizationState;
        public nint pMultisampleState;
        public nint pDepthStencilState;
        public nint pColorBlendState;
        public nint pDynamicState;
        public nint layout;
        public nint renderPass;
        public uint subpass;
        public nint basePipelineHandle;
        public int basePipelineIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkDescriptorSetLayoutBinding
    {
        public uint binding;
        public int descriptorType;
        public uint descriptorCount;
        public int stageFlags;
        public nint pImmutableSamplers;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkDescriptorSetLayoutCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint bindingCount;
        public nint pBindings;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPipelineLayoutCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint setLayoutCount;
        public nint pSetLayouts;
        public uint pushConstantRangeCount;
        public nint pPushConstantRanges;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkDescriptorPoolSize
    {
        public int type;
        public uint descriptorCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkDescriptorPoolCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint maxSets;
        public uint poolSizeCount;
        public nint pPoolSizes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkDescriptorSetAllocateInfo
    {
        public int sType;
        public nint pNext;
        public nint descriptorPool;
        public uint descriptorSetCount;
        public nint pSetLayouts;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkDescriptorBufferInfo
    {
        public nint buffer;
        public ulong offset;
        public ulong range;
    }

    /// <summary>
    /// Descriptor de imagen para vkUpdateDescriptorSets: con SampledImage apunta a la vista y el
    /// sampler va en cero; con Sampler es al revés (la vista en cero y el sampler asignado).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VkDescriptorImageInfo
    {
        public nint sampler;
        public nint imageView;
        public int imageLayout;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkWriteDescriptorSet
    {
        public int sType;
        public nint pNext;
        public nint dstSet;
        public uint dstBinding;
        public uint dstArrayElement;
        public uint descriptorCount;
        public int descriptorType;
        public nint pImageInfo;
        public nint pBufferInfo;
        public nint pTexelBufferView;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkBufferCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public ulong size;
        public int usage;
        public int sharingMode;
        public uint queueFamilyIndexCount;
        public nint pQueueFamilyIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkMemoryRequirements
    {
        public ulong size;
        public ulong alignment;
        public uint memoryTypeBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkMemoryAllocateInfo
    {
        public int sType;
        public nint pNext;
        public ulong allocationSize;
        public uint memoryTypeIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkCommandPoolCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public uint queueFamilyIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkCommandBufferAllocateInfo
    {
        public int sType;
        public nint pNext;
        public nint commandPool;
        public int level;
        public uint commandBufferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkCommandBufferBeginInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public nint pInheritanceInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkRenderPassBeginInfo
    {
        public int sType;
        public nint pNext;
        public nint renderPass;
        public nint framebuffer;
        public VkRect2D renderArea;
        public uint clearValueCount;
        public nint pClearValues;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkClearColorValue
    {
        public float r;
        public float g;
        public float b;
        public float a;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkClearDepthStencilValue
    {
        public float depth;
        public uint stencil;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct VkClearValue
    {
        [FieldOffset(0)] public VkClearColorValue color;
        [FieldOffset(0)] public VkClearDepthStencilValue depthStencil;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkBufferCopy
    {
        public ulong srcOffset;
        public ulong dstOffset;
        public ulong size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkBufferMemoryBarrier
    {
        public int sType;
        public nint pNext;
        public int srcAccessMask;
        public int dstAccessMask;
        public uint srcQueueFamilyIndex;
        public uint dstQueueFamilyIndex;
        public nint buffer;
        public ulong offset;
        public ulong size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSubmitInfo
    {
        public int sType;
        public nint pNext;
        public uint waitSemaphoreCount;
        public nint pWaitSemaphores;
        public nint pWaitDstStageMask;
        public uint commandBufferCount;
        public nint pCommandBuffers;
        public uint signalSemaphoreCount;
        public nint pSignalSemaphores;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkPresentInfoKHR
    {
        public int sType;
        public nint pNext;
        public uint waitSemaphoreCount;
        public nint pWaitSemaphores;
        public uint swapchainCount;
        public nint pSwapchains;
        public nint pImageIndices;
        public nint pResults;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkFenceCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkSemaphoreCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VkQueryPoolCreateInfo
    {
        public int sType;
        public nint pNext;
        public uint flags;
        public int queryType;
        public uint queryCount;
        public int pipelineStatistics;
    }

    // =====================================================================
    // Comandos
    // =====================================================================

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateInstance(ref VkInstanceCreateInfo createInfo, nint allocator, out nint instance);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyInstance(nint instance, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkEnumerateInstanceVersion(out uint apiVersion);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkEnumerateInstanceExtensionProperties(nint layerName, ref uint count, [Out] VkExtensionProperties[]? properties);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkEnumeratePhysicalDevices(nint instance, ref uint count, [Out] nint[]? devices);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkGetPhysicalDeviceProperties(nint physicalDevice, nint properties);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkGetPhysicalDeviceQueueFamilyProperties(nint physicalDevice, ref uint count, nint properties);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkGetPhysicalDeviceSurfaceSupportKHR(nint physicalDevice, uint queueFamilyIndex, nint surface, out uint supported);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkGetPhysicalDeviceSurfaceCapabilitiesKHR(nint physicalDevice, nint surface, out VkSurfaceCapabilitiesKHR capabilities);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkGetPhysicalDeviceSurfaceFormatsKHR(nint physicalDevice, nint surface, ref uint count, [Out] VkSurfaceFormatKHR[]? formats);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkGetPhysicalDeviceSurfacePresentModesKHR(nint physicalDevice, nint surface, ref uint count, [Out] int[]? modes);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateDevice(nint physicalDevice, ref VkDeviceCreateInfo createInfo, nint allocator, out nint device);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyDevice(nint device, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkGetDeviceQueue(nint device, uint queueFamilyIndex, uint queueIndex, out nint queue);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkDeviceWaitIdle(nint device);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateWin32SurfaceKHR(nint instance, ref VkWin32SurfaceCreateInfoKHR createInfo, nint allocator, out nint surface);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroySurfaceKHR(nint instance, nint surface, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateSwapchainKHR(nint device, ref VkSwapchainCreateInfoKHR createInfo, nint allocator, out nint swapchain);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroySwapchainKHR(nint device, nint swapchain, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkGetSwapchainImagesKHR(nint device, nint swapchain, ref uint count, [Out] nint[]? images);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkAcquireNextImageKHR(nint device, nint swapchain, ulong timeout, nint semaphore, nint fence, out uint imageIndex);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkQueuePresentKHR(nint queue, ref VkPresentInfoKHR presentInfo);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateImageView(nint device, ref VkImageViewCreateInfo createInfo, nint allocator, out nint view);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyImageView(nint device, nint view, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateImage(nint device, ref VkImageCreateInfo createInfo, nint allocator, out nint image);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyImage(nint device, nint image, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateSampler(nint device, ref VkSamplerCreateInfo createInfo, nint allocator, out nint sampler);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroySampler(nint device, nint sampler, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdCopyBufferToImage(nint commandBuffer, nint srcBuffer, nint dstImage, int dstImageLayout,
        uint regionCount, ref VkBufferImageCopy regions);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkGetImageMemoryRequirements(nint device, nint image, out VkMemoryRequirements requirements);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkBindImageMemory(nint device, nint image, nint memory, ulong offset);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateRenderPass(nint device, ref VkRenderPassCreateInfo createInfo, nint allocator, out nint renderPass);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyRenderPass(nint device, nint renderPass, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateFramebuffer(nint device, ref VkFramebufferCreateInfo createInfo, nint allocator, out nint framebuffer);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyFramebuffer(nint device, nint framebuffer, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateShaderModule(nint device, ref VkShaderModuleCreateInfo createInfo, nint allocator, out nint module);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyShaderModule(nint device, nint module, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateDescriptorSetLayout(nint device, ref VkDescriptorSetLayoutCreateInfo createInfo, nint allocator, out nint layout);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyDescriptorSetLayout(nint device, nint layout, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreatePipelineLayout(nint device, ref VkPipelineLayoutCreateInfo createInfo, nint allocator, out nint layout);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyPipelineLayout(nint device, nint layout, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateGraphicsPipelines(nint device, nint pipelineCache, uint createInfoCount, nint createInfos, nint allocator, [Out] nint[]? pipelines);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyPipeline(nint device, nint pipeline, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateBuffer(nint device, ref VkBufferCreateInfo createInfo, nint allocator, out nint buffer);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyBuffer(nint device, nint buffer, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkGetBufferMemoryRequirements(nint device, nint buffer, out VkMemoryRequirements requirements);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkBindBufferMemory(nint device, nint buffer, nint memory, ulong offset);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkGetPhysicalDeviceMemoryProperties(nint physicalDevice, nint memoryProperties);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkAllocateMemory(nint device, ref VkMemoryAllocateInfo allocateInfo, nint allocator, out nint memory);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkFreeMemory(nint device, nint memory, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkMapMemory(nint device, nint memory, ulong offset, ulong size, uint flags, out nint data);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkUnmapMemory(nint device, nint memory);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateDescriptorPool(nint device, ref VkDescriptorPoolCreateInfo createInfo, nint allocator, out nint descriptorPool);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyDescriptorPool(nint device, nint descriptorPool, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkAllocateDescriptorSets(nint device, ref VkDescriptorSetAllocateInfo allocateInfo, [Out] nint[]? descriptorSets);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkUpdateDescriptorSets(nint device, uint writeCount, nint writes, uint copyCount, nint copies);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateCommandPool(nint device, ref VkCommandPoolCreateInfo createInfo, nint allocator, out nint commandPool);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyCommandPool(nint device, nint commandPool, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkAllocateCommandBuffers(nint device, ref VkCommandBufferAllocateInfo allocateInfo, [Out] nint[]? commandBuffers);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkResetCommandBuffer(nint commandBuffer, uint flags);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkBeginCommandBuffer(nint commandBuffer, ref VkCommandBufferBeginInfo beginInfo);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkEndCommandBuffer(nint commandBuffer);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdBeginRenderPass(nint commandBuffer, ref VkRenderPassBeginInfo beginInfo, int contents);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdEndRenderPass(nint commandBuffer);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdBindPipeline(nint commandBuffer, int pipelineBindPoint, nint pipeline);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdBindDescriptorSets(nint commandBuffer, int pipelineBindPoint, nint layout, uint firstSet, uint descriptorSetCount, nint descriptorSets, uint dynamicOffsetCount, nint dynamicOffsets);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdBindVertexBuffers(nint commandBuffer, uint firstBinding, uint bindingCount, nint buffers, nint offsets);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdDraw(nint commandBuffer, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdSetViewport(nint commandBuffer, uint firstViewport, uint viewportCount, ref VkViewport viewport);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdSetScissor(nint commandBuffer, uint firstScissor, uint scissorCount, ref VkRect2D scissor);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdWriteTimestamp(nint commandBuffer, int pipelineStage, nint queryPool, uint query);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdResetQueryPool(nint commandBuffer, nint queryPool, uint firstQuery, uint queryCount);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdCopyBuffer(nint commandBuffer, nint srcBuffer, nint dstBuffer, uint regionCount, ref VkBufferCopy regions);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkCmdPipelineBarrier(nint commandBuffer, int srcStageMask, int dstStageMask, int dependencyFlags,
        uint memoryBarrierCount, nint memoryBarriers, uint bufferMemoryBarrierCount, ref VkBufferMemoryBarrier bufferMemoryBarriers,
        uint imageMemoryBarrierCount, nint imageMemoryBarriers);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateQueryPool(nint device, ref VkQueryPoolCreateInfo createInfo, nint allocator, out nint queryPool);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyQueryPool(nint device, nint queryPool, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkGetQueryPoolResults(nint device, nint queryPool, uint firstQuery, uint queryCount,
        nuint dataSize, nint data, ulong stride, int flags);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateFence(nint device, ref VkFenceCreateInfo createInfo, nint allocator, out nint fence);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroyFence(nint device, nint fence, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkWaitForFences(nint device, uint fenceCount, nint fences, uint waitAll, ulong timeout);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkResetFences(nint device, uint fenceCount, nint fences);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkGetFenceStatus(nint device, nint fence);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkCreateSemaphore(nint device, ref VkSemaphoreCreateInfo createInfo, nint allocator, out nint semaphore);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern void vkDestroySemaphore(nint device, nint semaphore, nint allocator);

    [DllImport(Library, CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    internal static extern int vkQueueSubmit(nint queue, uint submitCount, ref VkSubmitInfo submits, nint fence);

    /// <summary>VkExtensionProperties (nombre de 256 bytes + versión), para enumerar extensiones.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct VkExtensionProperties
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string extensionName;
        public uint specVersion;
    }

    // =====================================================================
    // Momento en que se escriben los timestamps (helper compartido con el backend)
    // =====================================================================

    /// <summary>Versión de API que se pide a la instancia (Vulkan 1.0: la escena no necesita más).</summary>
    internal const uint ApiVersion10 = (1u << 22);

    /// <summary>
    /// Reloj de alta resolución del sistema, en ms. Se usa para medir la entrega del frame
    /// (present) y las esperas por la cola: son medidas del HOST, no de la placa.
    /// </summary>
    internal static double NowMs() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}
