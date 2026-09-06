using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using Tesseris.Engine.Core;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Najde Vulkan loader a zpřístupní ho jak nám, tak GLFW.
///
/// Na Windows a Linuxu je to bez práce — <c>vulkan-1.dll</c> a <c>libvulkan.so.1</c> leží
/// v systémových cestách a najdou se samy. Na macOS ne, a je to podstatné:
///
/// * Vulkan tam není nativní API. Běží přes <b>MoltenVK</b>, což je překladová vrstva
///   nad Metalem, a instaluje se stranou — nejčastěji Homebrew do <c>/opt/homebrew/lib</c>
///   nebo LunarG SDK do <c>~/VulkanSDK</c>.
/// * <c>/opt/homebrew/lib</c> <b>není</b> ve výchozích cestách dyld, takže <c>dlopen</c>
///   podle holého jména knihovnu nenajde. Ověřeno měřením: bez zásahu selže jak Silk.NET,
///   tak GLFW.
/// * Nestačí knihovnu předem načíst přes <c>NativeLibrary.Load</c> — brew má install name
///   <c>/opt/homebrew/opt/vulkan-loader/lib/libvulkan.1.dylib</c>, takže pozdější dlopen
///   podle holého jména na už načtenou knihovnu netrefí.
///
/// Řešení je proto dvojí a obojí musí proběhnout <b>před vytvořením okna</b>: knihovna se
/// načte absolutní cestou pro nás a zároveň se předá GLFW přes <c>glfwInitVulkanLoader</c>.
/// Bez toho by hra potřebovala <c>DYLD_FALLBACK_LIBRARY_PATH</c>, což od hry nikdo nečeká.
/// </summary>
public static class VulkanLoader
{
    private static Vk? _api;

    /// <summary>Odkud se loader nakonec vzal. Jde do logu, aby šlo poznat, která instalace hraje.</summary>
    public static string LoadedFrom { get; private set; } = "(nenačteno)";

    /// <summary>
    /// Načtený Vulkan. Před prvním použitím musí proběhnout <see cref="Initialize"/>.
    /// </summary>
    public static Vk Api =>
        _api ?? throw new InvalidOperationException(
            $"Vulkan není načtený — zavolej {nameof(VulkanLoader)}.{nameof(Initialize)}() před vytvořením okna.");

    /// <summary>
    /// Najde a načte Vulkan loader. Volá se jednou, ještě před vytvořením okna — GLFW si
    /// nastavení loaderu přebírá při <c>glfwInit</c> a později už na něj nesáhne.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Loader se nikde nenašel.</exception>
    public static void Initialize()
    {
        if (_api is not null)
        {
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            InitializeMacOS();
            return;
        }

        _api = Vk.GetApi();
        LoadedFrom = "systémová cesta";
        Log.Info($"Vulkan loader: {LoadedFrom}.");
    }

    private static void InitializeMacOS()
    {
        string? path = FindMacOSLoader();

        if (path is null)
        {
            throw new PlatformNotSupportedException(
                "Vulkan loader se na tomhle Macu nenašel. Vulkan není na macOS nativní API — "
                + "běží přes MoltenVK. Nainstaluj ho:\n"
                + "    brew install molten-vk vulkan-loader\n"
                + "nebo použij LunarG Vulkan SDK do ~/VulkanSDK.");
        }

        // Absolutní cesta je nutná: brew nemá /opt/homebrew/lib ve výchozích cestách dyld.
        _api = new Vk(new DefaultNativeContext(path));
        LoadedFrom = path;

        TeachGlfwAboutLoader(path);

        Log.Info($"Vulkan loader: {path}.");
    }

    /// <summary>
    /// Projde místa, kam MoltenVK a jeho loader typicky patří. Pořadí je od nejkonkrétnějšího
    /// (proměnná <c>VULKAN_SDK</c>) k nejobecnějšímu.
    ///
    /// <c>libMoltenVK.dylib</c> je až úplně poslední a je to nouzová varianta: MoltenVK
    /// umí dělat i ICD bez loaderu, ale pak nejsou k dispozici vrstvy (validace) a chování
    /// se liší od zbytku Vulkanu.
    /// </summary>
    private static string? FindMacOSLoader()
    {
        var candidates = new List<string>();

        string? sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (!string.IsNullOrEmpty(sdk))
        {
            candidates.Add(Path.Combine(sdk, "lib", "libvulkan.1.dylib"));
        }

        candidates.Add("/opt/homebrew/lib/libvulkan.1.dylib");
        candidates.Add("/usr/local/lib/libvulkan.1.dylib");

        // LunarG SDK se rozbaluje do ~/VulkanSDK/<verze>/macOS/lib.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string sdkRoot = Path.Combine(home, "VulkanSDK");
        if (Directory.Exists(sdkRoot))
        {
            // Nejnovější verze první — jména jsou 1.3.280.1 a podobně, řadí se rozumně textově.
            foreach (string versionDirectory in Directory.GetDirectories(sdkRoot).OrderDescending())
            {
                candidates.Add(Path.Combine(versionDirectory, "macOS", "lib", "libvulkan.1.dylib"));
            }
        }

        candidates.Add("/opt/homebrew/lib/libMoltenVK.dylib");
        candidates.Add("/usr/local/lib/libMoltenVK.dylib");

        return candidates.Find(File.Exists);
    }

    /// <summary>
    /// Předá GLFW ukazatel na <c>vkGetInstanceProcAddr</c>, aby si loader nemuselo hledat samo.
    ///
    /// GLFW 3.4 na to má <c>glfwInitVulkanLoader</c>, ale OpenTK 4.9.4 tu funkci nebinduje —
    /// symbol z dodávaného dylibu ale exportovaný je, takže se dá zavolat přímo.
    ///
    /// Nezdar tady není fatální: měřením se ukázalo, že GLFW knihovnu najde i tak, protože
    /// ji do procesu mezitím načetl Silk.NET. Je to ale spoleh na chování dyld, ne na
    /// dokumentované rozhraní, takže se to zkusí správnou cestou a nezdar se jen zaloguje.
    /// </summary>
    private static void TeachGlfwAboutLoader(string vulkanPath)
    {
        try
        {
            nint vulkanHandle = NativeLibrary.Load(vulkanPath);
            if (!NativeLibrary.TryGetExport(vulkanHandle, "vkGetInstanceProcAddr", out nint getProcAddress))
            {
                Log.Warn("V loaderu chybí vkGetInstanceProcAddr, GLFW si Vulkan bude hledat samo.");
                return;
            }

            if (!TryLoadGlfw(out nint glfwHandle))
            {
                Log.Warn("Knihovna GLFW se nenašla, GLFW si Vulkan bude hledat samo.");
                return;
            }

            if (!NativeLibrary.TryGetExport(glfwHandle, "glfwInitVulkanLoader", out nint initVulkanLoader))
            {
                Log.Warn("GLFW nemá glfwInitVulkanLoader (starší než 3.4), bude si Vulkan hledat samo.");
                return;
            }

            unsafe
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)initVulkanLoader)(getProcAddress);
            }

            Log.Info("GLFW dostalo ukazatel na vkGetInstanceProcAddr.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Log.Warn($"GLFW se nepodařilo říct o Vulkanu ({ex.GetType().Name}), bude si ho hledat samo.");
        }
    }

    /// <summary>
    /// Najde tu kopii GLFW, kterou bude používat OpenTK. Musí to být <b>stejný soubor</b>,
    /// jinak by se nastavení loaderu zapsalo do jiného modulu, než který pak GLFW inicializuje.
    /// OpenTK bere nativní knihovnu z <c>runtimes/{rid}/native</c> vedle binárky.
    /// </summary>
    private static bool TryLoadGlfw(out nint handle)
    {
        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "libglfw.3.dylib"),
            Path.Combine(AppContext.BaseDirectory, "libglfw.3.dylib"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
            {
                return true;
            }
        }

        return NativeLibrary.TryLoad("libglfw.3.dylib", out handle);
    }
}
