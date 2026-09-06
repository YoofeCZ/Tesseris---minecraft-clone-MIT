using System.Reflection;
using Silk.NET.Vulkan;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Načítání shaderů. Vulkan nebere zdrojový GLSL, jen přeložený SPIR-V — ten vzniká
/// při buildu (viz <c>Directory.Build.targets</c>) a je vložený do sestavení.
/// </summary>
public static unsafe class SpirV
{
    /// <summary>
    /// Vytvoří shader modul z prostředku vloženého do zadaného sestavení.
    /// </summary>
    /// <param name="name">Jméno souboru, třeba <c>chunk.vert.spv</c>.</param>
    public static ShaderModule LoadModule(VulkanContext context, Assembly assembly, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assembly);

        byte[] code = ReadResource(assembly, name);

        // SPIR-V je proud 32bitovych slov, takze delka musi byt delitelna ctyrmi.
        // Kdyz neni, neni to SPIR-V a ovladac by na tom spadl az nekde uvnitr.
        if (code.Length % 4 != 0)
        {
            throw new InvalidOperationException(
                $"Shader '{name}' má {code.Length} B, což není násobek čtyř — to není platný SPIR-V.");
        }

        fixed (byte* codePtr = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)codePtr,
            };

            VulkanContext.Check(
                context.Vk.CreateShaderModule(context.Device, &info, null, out ShaderModule module),
                $"vkCreateShaderModule ({name})");

            return module;
        }
    }

    /// <summary>
    /// Vytáhne prostředek podle jména souboru.
    ///
    /// Hledá se podle konce jména, ne na přesnou shodu: MSBuild prostředkům přidává
    /// předponu odvozenou z cesty v <c>obj/</c>, takže se z <c>chunk.vert.spv</c> stane
    /// <c>Tesseris.Game.obj.Release.net8._0.shaders.chunk.vert.spv</c>. Vázat se na tvar
    /// té předpony by znamenalo, že se build rozbije při změně konfigurace nebo cíle.
    /// </summary>
    private static byte[] ReadResource(Assembly assembly, string name)
    {
        string[] all = assembly.GetManifestResourceNames();
        string? match = Array.Find(all, candidate => candidate.EndsWith(name, StringComparison.Ordinal));

        if (match is null)
        {
            throw new InvalidOperationException(
                $"Shader '{name}' není v sestavení {assembly.GetName().Name}. "
                + $"Nalezené prostředky: {(all.Length == 0 ? "žádné" : string.Join(", ", all))}. "
                + "Shadery se překládají při buildu — zkontroluj, že je nainstalovaný glslangValidator.");
        }

        using Stream stream = assembly.GetManifestResourceStream(match)
            ?? throw new InvalidOperationException($"Prostředek '{match}' nejde otevřít.");

        byte[] buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
