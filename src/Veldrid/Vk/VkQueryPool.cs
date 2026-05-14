using Vulkan;
using static Vulkan.VulkanNative;
using static Veldrid.Vk.VulkanUtil;
using System;

namespace Veldrid.Vk
{
    internal unsafe class VkQueryPool : QueryPool
    {
        private readonly VkGraphicsDevice _gd;
        private readonly Vulkan.VkQueryPool _deviceQueryPool;
        private bool _destroyed;
        private string _name;

        public override QueryType Type { get; }
        public override uint QueryCount { get; }

        public override string Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetResourceName(this, value);
            }
        }

        public override bool IsDisposed => _destroyed;

        public Vulkan.VkQueryPool DeviceQueryPool => _deviceQueryPool;

        public VkQueryPool(VkGraphicsDevice gd, ref QueryPoolDescription description)
        {
            _gd = gd;
            Type = description.Type;
            QueryCount = description.QueryCount;

            VkQueryPoolCreateInfo queryPoolCI = VkQueryPoolCreateInfo.New();
            queryPoolCI.queryType = VdToVkQueryType(description.Type);
            queryPoolCI.queryCount = description.QueryCount;

            VkResult result = vkCreateQueryPool(_gd.Device, ref queryPoolCI, null, out _deviceQueryPool);
            CheckResult(result);
        }

        private VkQueryType VdToVkQueryType(QueryType type)
        {
            switch (type)
            {
                case QueryType.Timestamp:
                    return VkQueryType.Timestamp;
                default:
                    throw Illegal.Value<QueryType>();
            }
        }

        public override void Dispose()
        {
            if (!_destroyed)
            {
                _destroyed = true;
                vkDestroyQueryPool(_gd.Device, _deviceQueryPool, null);
            }
        }
    }
}
