using Silk.NET.Vulkan;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Descriptor set s jednou texturou.
///
/// <para>
/// V OpenGL stačilo před kreslením zavolat <c>glBindTextureUnit(0, handle)</c>. Vulkan
/// vazby popisuje předem: textura se zapíše do descriptor setu, ten se jednou vytvoří
/// a při kreslení se už jen naváže. Protože se textura po celou dobu běhu nemění, stačí
/// jeden set na renderer a zapisuje se jedinkrát při startu.
/// </para>
/// </summary>
public sealed unsafe class VulkanTextureSet : IDisposable
{
    private readonly VulkanContext _context;
    private DescriptorPool _pool;

    public VulkanTextureSet(VulkanContext context, DescriptorSetLayout layout, VulkanTexture texture)
        : this(context, layout, Entry.From(texture))
    {
    }

    /// <summary>
    /// Sada s víc texturami. Zapisují se na vazby 0 až N−1 v pořadí, v jakém přijdou.
    /// </summary>
    /// <remarks>
    /// Potřebuje to voda: k atlasu bloků ještě kopii scény a hloubku. Na rozdíl od atlasu
    /// se ty dvě mění při každé změně velikosti okna, takže sada má <see cref="Write"/>
    /// a nezapisuje se jen jednou při startu.
    /// </remarks>
    public VulkanTextureSet(VulkanContext context, DescriptorSetLayout layout, params Entry[] entries)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Length == 0)
        {
            throw new ArgumentException("Descriptor set musí mít aspoň jednu texturu.", nameof(entries));
        }

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = (uint)entries.Length,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 1,
        };

        VulkanContext.Check(
            context.Vk.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool), "vkCreateDescriptorPool");

        DescriptorSetLayout setLayout = layout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };

        VulkanContext.Check(
            context.Vk.AllocateDescriptorSets(context.Device, &allocateInfo, out DescriptorSet set),
            "vkAllocateDescriptorSets");

        Handle = set;

        Write(entries);
    }

    /// <summary>
    /// Jedna vazba: sampler, pohled na obraz a layout, ve kterém obraz při čtení bude.
    /// </summary>
    /// <remarks>
    /// Layout je součástí vazby schválně. Běžné textury leží v <c>ShaderReadOnlyOptimal</c>,
    /// ale hloubka, ze které voda čte, je zároveň přílohou a musí být
    /// v <c>DepthReadOnlyOptimal</c>. Kdyby se tady dosadil jeden layout natvrdo, validační
    /// vrstva by hlásila nesoulad a chování by bylo nedefinované.
    /// </remarks>
    public readonly record struct Entry(
        Sampler Sampler, ImageView View, ImageLayout Layout = ImageLayout.ShaderReadOnlyOptimal)
    {
        public static Entry From(VulkanTexture texture)
        {
            ArgumentNullException.ThrowIfNull(texture);
            return new Entry(texture.Sampler, texture.View);
        }
    }

    /// <summary>
    /// Přepíše obsah sady. Volá se znovu, když se změní obrazy, na které ukazuje —
    /// tedy po každém předělání swapchainu.
    /// </summary>
    /// <remarks>
    /// Volající musí mít jistotu, že žádný rozpracovaný snímek sadu nečte. Po změně
    /// velikosti okna to platí: <c>HandleResize</c> předtím čeká na <c>vkDeviceWaitIdle</c>.
    /// </remarks>
    public void Write(params Entry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var images = stackalloc DescriptorImageInfo[entries.Length];
        var writes = stackalloc WriteDescriptorSet[entries.Length];

        for (int i = 0; i < entries.Length; i++)
        {
            images[i] = new DescriptorImageInfo
            {
                Sampler = entries[i].Sampler,
                ImageView = entries[i].View,
                ImageLayout = entries[i].Layout,
            };

            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = Handle,
                DstBinding = (uint)i,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &images[i],
            };
        }

        _context.Vk.UpdateDescriptorSets(_context.Device, (uint)entries.Length, writes, 0, null);
    }

    public DescriptorSet Handle { get; }

    /// <summary>Naváže set pro následující kreslení.</summary>
    public void Bind(CommandBuffer commandBuffer, PipelineLayout layout)
    {
        DescriptorSet set = Handle;
        _context.Vk.CmdBindDescriptorSets(
            commandBuffer, PipelineBindPoint.Graphics, layout, 0, 1, &set, 0, null);
    }

    public void Dispose()
    {
        if (_pool.Handle != 0)
        {
            // Sety patri poolu, takze se uvolni s nim a nemusi se rusit zvlast.
            _context.Vk.DestroyDescriptorPool(_context.Device, _pool, null);
            _pool = default;
        }
    }
}
