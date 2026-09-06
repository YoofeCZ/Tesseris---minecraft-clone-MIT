using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Tesseris.Engine.Core;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Napojení <c>VK_EXT_debug_utils</c> na <see cref="Log"/>. Vulkanská obdoba dřívějšího
/// <c>GLDebug</c> a se stejným smyslem: selftest musí poznat, že si ovladač stěžoval,
/// i když se to na obrazovce neprojevilo.
///
/// <para>
/// <b>Rozdíl oproti OpenGL:</b> v GL posílal hlášky ovladač sám. Ve Vulkanu ovladač skoro
/// nemluví — drtivá většina užitečných zpráv pochází z <b>validační vrstvy</b>, a ta je
/// samostatná instalace. Když chybí, messenger se sice vytvoří, ale mlčí; proto se
/// dostupnost vrstvy hlásí do logu, aby se ticho nedalo splést se „všechno v pořádku".
/// </para>
/// </summary>
public sealed unsafe class VulkanDebug : IDisposable
{
    /// <summary>Jméno validační vrstvy. Bez ní messenger prakticky nic nenahlásí.</summary>
    public const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";

    private static int _errorCount;

    // Delegát musí držet spravovaná reference po celou dobu života messengeru. Je to
    // stejná past jako u GL debug callbacku: bez vlastního kořene ho GC posbírá a další
    // volání shodí proces na callbacku na uvolněný delegát.
    private readonly PfnDebugUtilsMessengerCallbackEXT _callback;

    private readonly Vk _vk;
    private readonly Instance _instance;
    private readonly ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _messenger;

    private VulkanDebug(Vk vk, Instance instance, ExtDebugUtils? debugUtils)
    {
        _vk = vk;
        _instance = instance;
        _debugUtils = debugUtils;
        _callback = new PfnDebugUtilsMessengerCallbackEXT(OnMessage);
    }

    /// <summary>
    /// Kolik zpráv závažnosti Error přišlo od startu. Selftest podle toho pozná, že se něco
    /// pokazilo. Stejná role jako <c>GLDebug.HighSeverityCount</c> v OpenGL verzi.
    /// </summary>
    public static int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>Je validační vrstva k dispozici? Když ne, ticho v logu nic nedokazuje.</summary>
    public static bool ValidationAvailable { get; private set; }

    /// <summary>
    /// Vytvoří strukturu pro messenger. Používá se dvakrát: jednou v řetězu
    /// <c>pNext</c> u <c>vkCreateInstance</c> (aby se odchytily i chyby při vzniku
    /// instance, kdy messenger ještě neexistuje) a podruhé při jeho skutečném vytvoření.
    /// </summary>
    internal static DebugUtilsMessengerCreateInfoEXT CreateInfo(PfnDebugUtilsMessengerCallbackEXT callback) =>
        new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt
                | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = callback,
        };

    /// <summary>
    /// Má se validační vrstva zapnout, pokud je k dispozici?
    ///
    /// <para><b>Ve hře musí být vypnutá.</b> Validace kontroluje každé volání Vulkanu
    /// a zdraží ho řádově — naměřeno: vydávání kreslení <b>21,8 ms</b> na jeden snímek
    /// při 2458 draw callech, tedy skoro 9 µs na draw call místo jednotek desetin.
    /// Do 28. 7. 2026 se zapínala vždy, když byla nainstalovaná, takže každý, kdo měl
    /// Vulkan SDK, hrál s brzdou.</para>
    ///
    /// <para>V selftestu zapnutá zůstává — bez ní by kritérium „nula chyb Vulkanu"
    /// netestovalo nic.</para>
    /// </summary>
    public static bool Requested { get; set; }

    /// <summary>
    /// Zjistí, jestli je validační vrstva nainstalovaná a vyžádaná. Volá se před vytvořením
    /// instance.
    /// </summary>
    public static bool DetectValidationLayer(Vk vk)
    {
        if (!Requested)
        {
            ValidationAvailable = false;
            return false;
        }

        uint count = 0;
        vk.EnumerateInstanceLayerProperties(ref count, null);

        var layers = new LayerProperties[count];
        fixed (LayerProperties* layersPtr = layers)
        {
            vk.EnumerateInstanceLayerProperties(ref count, layersPtr);
        }

        foreach (LayerProperties layer in layers)
        {
            if (SilkMarshal.PtrToString((nint)layer.LayerName) == ValidationLayerName)
            {
                ValidationAvailable = true;
                return true;
            }
        }

        ValidationAvailable = false;
        return false;
    }

    /// <summary>
    /// Zapne messenger nad hotovou instancí. Když rozšíření <c>VK_EXT_debug_utils</c>
    /// není, vrátí objekt, který nic nedělá — hra běží dál, jen bez diagnostiky.
    /// </summary>
    public static VulkanDebug Install(Vk vk, Instance instance)
    {
        ArgumentNullException.ThrowIfNull(vk);

        if (!vk.TryGetInstanceExtension(instance, out ExtDebugUtils debugUtils))
        {
            Log.Warn("VK_EXT_debug_utils není k dispozici, chyby ovladače se nedozvíme.");
            return new VulkanDebug(vk, instance, debugUtils: null);
        }

        var debug = new VulkanDebug(vk, instance, debugUtils);
        DebugUtilsMessengerCreateInfoEXT info = CreateInfo(debug._callback);

        Result result = debugUtils.CreateDebugUtilsMessenger(instance, &info, null, out debug._messenger);
        if (result != Result.Success)
        {
            Log.Warn($"Messenger se nevytvořil ({result}), chyby ovladače se nedozvíme.");
            return debug;
        }

        Log.Info(ValidationAvailable
            ? "Vulkan debug messenger zapnutý, validační vrstva aktivní."
            : "Vulkan debug messenger zapnutý, ale validační vrstva CHYBÍ — "
              + "nainstaluj `brew install vulkan-validationlayers`, jinak se většina chyb neohlásí.");

        return debug;
    }

    public void Dispose()
    {
        if (_debugUtils is not null && _messenger.Handle != 0)
        {
            _debugUtils.DestroyDebugUtilsMessenger(_instance, _messenger, null);
            _messenger = default;
        }

        _debugUtils?.Dispose();
    }

    private static uint OnMessage(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        string message = SilkMarshal.PtrToString((nint)data->PMessage) ?? "(bez textu)";
        string line = $"VK [{severity}] {types}: {message}";

        if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
        {
            Interlocked.Increment(ref _errorCount);
            Log.Error(line);
        }
        else
        {
            Log.Warn(line);
        }

        // Vzdy false: true by znamenalo "prerus volani, ktere zpravu vyvolalo",
        // coz se pouziva jen pri ladeni validacni vrstvy.
        return Vk.False;
    }
}
