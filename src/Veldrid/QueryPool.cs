using System;

namespace Veldrid
{
    /// <summary>
    /// A device object that manages a collection of GPU queries.
    /// </summary>
    public abstract class QueryPool : DeviceResource, IDisposable
    {
        /// <summary>
        /// The type of query in this pool.
        /// </summary>
        public abstract QueryType Type { get; }
        /// <summary>
        /// The number of queries in the pool.
        /// </summary>
        public abstract uint QueryCount { get; }

        /// <summary>
        /// Gets a value indicating whether this instance has been disposed.
        /// </summary>
        public abstract bool IsDisposed { get; }

        /// <inheritdoc/>
        public abstract string Name { get; set; }

        /// <inheritdoc/>
        public abstract void Dispose();
    }
}
