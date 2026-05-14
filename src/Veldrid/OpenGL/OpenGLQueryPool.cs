using System;

namespace Veldrid.OpenGL
{
    internal class OpenGLQueryPool : QueryPool
    {
        private string _name;
        public override QueryType Type { get; }
        public override uint QueryCount { get; }
        public override string Name { get => _name; set => _name = value; }
        public override bool IsDisposed => false;

        public OpenGLQueryPool(ref QueryPoolDescription description)
        {
            Type = description.Type;
            QueryCount = description.QueryCount;
        }

        public override void Dispose() { }
    }
}
