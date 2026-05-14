using System;

namespace Veldrid
{
    /// <summary>
    /// Describes a <see cref="QueryPool"/>.
    /// </summary>
    public struct QueryPoolDescription : IEquatable<QueryPoolDescription>
    {
        /// <summary>
        /// The type of query pool.
        /// </summary>
        public QueryType Type;
        /// <summary>
        /// The number of queries to be allocated in the pool.
        /// </summary>
        public uint QueryCount;

        /// <summary>
        /// Constructs a new <see cref="QueryPoolDescription"/>.
        /// </summary>
        /// <param name="type">The type of query pool.</param>
        /// <param name="queryCount">The number of queries to be allocated in the pool.</param>
        public QueryPoolDescription(QueryType type, uint queryCount)
        {
            Type = type;
            QueryCount = queryCount;
        }

        /// <summary>
        /// Element-wise equality.
        /// </summary>
        /// <param name="other">The other <see cref="QueryPoolDescription"/>.</param>
        /// <returns>True if all elements are equal; false otherwise.</returns>
        public bool Equals(QueryPoolDescription other)
        {
            return Type == other.Type && QueryCount == other.QueryCount;
        }

        /// <summary>
        /// Returns the hash code for this instance.
        /// </summary>
        /// <returns>A 32-bit signed integer that is the hash code for this instance.</returns>
        public override int GetHashCode()
        {
            return HashHelper.Combine((int)Type, QueryCount.GetHashCode());
        }
    }
}
