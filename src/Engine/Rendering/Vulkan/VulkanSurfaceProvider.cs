namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Napojení na okno. Vulkan sám o oknech nic neví — spojení se surfacem dělá platformové
/// rozšíření (<c>VK_EXT_metal_surface</c> na macOS, <c>VK_KHR_win32_surface</c> na Windows,
/// <c>VK_KHR_xlib_surface</c> na Linuxu) a které to je, ví okenní knihovna.
///
/// Kontext proto okno nezná a dostane jen tohle: seznam rozšíření, která si okenní knihovna
/// vyžádala, a funkci, která z hotové instance udělá surface. Díky tomu nemusí engine
/// záviset na konkrétní okenní knihovně a dá se otestovat i bez okna.
/// </summary>
/// <param name="RequiredInstanceExtensions">
/// Rozšíření instance, bez kterých okenní knihovna surface nevytvoří.
/// </param>
/// <param name="CreateSurface">
/// Dostane handle <c>VkInstance</c>, vrátí handle <c>VkSurfaceKHR</c>. Nula znamená nezdar.
/// </param>
public sealed record VulkanSurfaceProvider(
    string[] RequiredInstanceExtensions,
    Func<nint, nint> CreateSurface);
