using System;

namespace Veldrid.D3D11
{
    internal class D3D11QueryPool : QueryPool
    {
        private string _name;
        public override QueryType Type { get; }
        public override uint QueryCount { get; }
        public override string Name { get => _name; set => _name = value; }
        public override bool IsDisposed => false;

        public D3D11QueryPool(ref QueryPoolDescription description)
        {
            Type = description.Type;
            QueryCount = description.QueryCount;
        }

        public override void Dispose() { }
    }
}
