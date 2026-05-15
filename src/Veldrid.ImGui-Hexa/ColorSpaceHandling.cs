namespace Veldrid.ImGuiHexa
{
    /// <summary>
    /// Identifies how an ImGuiRenderer should treat vertex colors.
    /// </summary>
    public enum ColorSpaceHandling
    {
        /// <summary>
        /// Vertex colors are passed to the shader as-is.
        /// </summary>
        Legacy,

        /// <summary>
        /// Vertex colors are converted from sRGB to linear space before being rendered.
        /// </summary>
        Linear
    }
}
