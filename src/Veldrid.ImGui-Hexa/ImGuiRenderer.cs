using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.IO;

namespace Veldrid.ImGuiHexa
{
    /// <summary>
    /// Can render draw lists produced by ImGui.
    /// Also provides functions for updating ImGui input.
    /// </summary>
    public unsafe class ImGuiRenderer : IDisposable
    {
        private GraphicsDevice _gd;
        private readonly Assembly _assembly;
        private ColorSpaceHandling _colorSpaceHandling;

        // Device objects
        private DeviceBuffer _vertexBuffer;
        private DeviceBuffer _indexBuffer;
        private DeviceBuffer _projMatrixBuffer;
        private Shader _vertexShader;
        private Shader _fragmentShader;
        private ResourceLayout _layout;
        private ResourceLayout _textureLayout;
        private Pipeline _pipeline;
        private ResourceSet _mainResourceSet;

        private int _windowWidth;
        private int _windowHeight;
        private Vector2 _scaleFactor = Vector2.One;

        // Image trackers for User-Bound Textures
        private readonly Dictionary<TextureView, ResourceSetInfo> _setsByView
            = new Dictionary<TextureView, ResourceSetInfo>();
        private readonly Dictionary<Texture, TextureView> _autoViewsByTexture
            = new Dictionary<Texture, TextureView>();
        private readonly Dictionary<IntPtr, ResourceSetInfo> _viewsById = new Dictionary<IntPtr, ResourceSetInfo>();
        private readonly List<IDisposable> _ownedResources = new List<IDisposable>();
        private int _lastAssignedID = 100;

        // Image trackers for ImGui-Managed Textures (Fonts, etc.)
        private readonly Dictionary<IntPtr, ImGuiManagedTexture> _managedTextures = new Dictionary<IntPtr, ImGuiManagedTexture>();
        private IntPtr _fontTextureId = (IntPtr)(-1);
        private struct ImGuiManagedTexture
        {
            public Texture Texture;
            public TextureView View;
            public ResourceSet ResourceSet;
        }

        private bool _frameBegun;

        public ImGuiRenderer(GraphicsDevice gd, OutputDescription outputDescription, int width, int height)
            : this(gd, outputDescription, width, height, ColorSpaceHandling.Legacy) { }

        public ImGuiRenderer(GraphicsDevice gd, OutputDescription outputDescription, int width, int height, ColorSpaceHandling colorSpaceHandling)
        {
            _gd = gd;
            _assembly = typeof(ImGuiRenderer).GetTypeInfo().Assembly;
            _colorSpaceHandling = colorSpaceHandling;
            _windowWidth = width;
            _windowHeight = height;

            ImGuiContextPtr context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);

            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.AddFontDefault();
            io.Fonts.Flags |= ImFontAtlasFlags.NoBakedLines;

            // HEXA.NET PATTERN: Aktiviere Textur-Events und Vertex-Offsets
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;

            CreateDeviceResources(gd, outputDescription);

            SetPerFrameImGuiData(1f / 60f);

            ImGui.NewFrame();
            _frameBegun = true;
        }

        public void WindowResized(int width, int height)
        {
            _windowWidth = width;
            _windowHeight = height;
        }

        public void DestroyDeviceObjects()
        {
            Dispose();
        }

        public void CreateDeviceResources(GraphicsDevice gd, OutputDescription outputDescription)
            => CreateDeviceResources(gd, outputDescription, _colorSpaceHandling);

        public void CreateDeviceResources(GraphicsDevice gd, OutputDescription outputDescription, ColorSpaceHandling colorSpaceHandling)
        {
            _gd = gd;
            _colorSpaceHandling = colorSpaceHandling;
            ResourceFactory factory = gd.ResourceFactory;
            _vertexBuffer = factory.CreateBuffer(new BufferDescription(10000, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _vertexBuffer.Name = "ImGui.NET Vertex Buffer";
            _indexBuffer = factory.CreateBuffer(new BufferDescription(2000, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            _indexBuffer.Name = "ImGui.NET Index Buffer";

            _projMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _projMatrixBuffer.Name = "ImGui.NET Projection Buffer";

            byte[] vertexShaderBytes = LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-vertex", ShaderStages.Vertex, _colorSpaceHandling);
            byte[] fragmentShaderBytes = LoadEmbeddedShaderCode(gd.ResourceFactory, "imgui-frag", ShaderStages.Fragment, _colorSpaceHandling);
            _vertexShader = factory.CreateShader(new ShaderDescription(ShaderStages.Vertex, vertexShaderBytes, _gd.BackendType == GraphicsBackend.Vulkan ? "main" : "VS"));
            _vertexShader.Name = "ImGui.NET Vertex Shader";
            _fragmentShader = factory.CreateShader(new ShaderDescription(ShaderStages.Fragment, fragmentShaderBytes, _gd.BackendType == GraphicsBackend.Vulkan ? "main" : "FS"));
            _fragmentShader.Name = "ImGui.NET Fragment Shader";

            VertexLayoutDescription[] vertexLayouts = new VertexLayoutDescription[]
            {
                new VertexLayoutDescription(
                    new VertexElementDescription("in_position", VertexElementSemantic.Position, VertexElementFormat.Float2),
                    new VertexElementDescription("in_texCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                    new VertexElementDescription("in_color", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm))
            };

            _layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ProjectionMatrixBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
            _layout.Name = "ImGui.NET Resource Layout";
            _textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));
            _textureLayout.Name = "ImGui.NET Texture Layout";

            GraphicsPipelineDescription pd = new GraphicsPipelineDescription(
                BlendStateDescription.SingleAlphaBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, true),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(
                    vertexLayouts,
                    new[] { _vertexShader, _fragmentShader },
                    new[]
                    {
                        new SpecializationConstant(0, gd.IsClipSpaceYInverted),
                        new SpecializationConstant(1, _colorSpaceHandling == ColorSpaceHandling.Legacy),
                    }),
                new ResourceLayout[] { _layout, _textureLayout },
                outputDescription,
                ResourceBindingModel.Default);
            _pipeline = factory.CreateGraphicsPipeline(ref pd);
            _pipeline.Name = "ImGui.NET Pipeline";

            _mainResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_layout,
                _projMatrixBuffer,
                gd.PointSampler));
            _mainResourceSet.Name = "ImGui.NET Main Resource Set";

            // HINWEIS: Font-Textur-Erstellung wurde hier entfernt.
            // Wird jetzt automatisch via ProcessTextureUpdates() durch ImGui getriggert.
        }

        // ---------------------------------------------------------------------
        // HEXA.NET TEXTURE MANAGEMENT PATTERN (Aus XNA übernommen)
        // ---------------------------------------------------------------------
        private void ProcessTextureUpdates(ImDrawDataPtr drawData)
        {
            if (drawData.Textures.Data == null) return;

            for (int i = 0; i < drawData.Textures.Size; i++)
            {
                ImTextureDataPtr textureData = drawData.Textures.Data[i];
                UpdateTexture(textureData);
            }
        }

        private void UpdateTexture(ImTextureDataPtr textureData)
        {
            switch (textureData.Status)
            {
                case ImTextureStatus.WantCreate:
                    CreateManagedTexture(textureData);
                    break;
                case ImTextureStatus.WantUpdates:
                    UpdateManagedTextureData(textureData);
                    break;
                case ImTextureStatus.WantDestroy:
                    DestroyManagedTexture(textureData);
                    break;
                case ImTextureStatus.Ok:
                    break;
            }
        }

        private void CreateManagedTexture(ImTextureDataPtr textureData)
        {
            PixelFormat format = textureData.Format == ImTextureFormat.Rgba32 ? PixelFormat.R8_G8_B8_A8_UNorm : PixelFormat.R8_UNorm;
            Texture texture = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                (uint)textureData.Width, (uint)textureData.Height, 1, 1, format, TextureUsage.Sampled));
            texture.Name = "ImGui Managed Texture";

            if (textureData.Pixels != null)
            {
                uint bytesPerPixel = textureData.Format == ImTextureFormat.Rgba32 ? 4u : 1u;
                _gd.UpdateTexture(
                    texture,
                    (IntPtr)textureData.Pixels,
                    (uint)(bytesPerPixel * textureData.Width * textureData.Height),
                    0, 0, 0,
                    (uint)textureData.Width, (uint)textureData.Height, 1,
                    0, 0);
            }

            TextureView view = _gd.ResourceFactory.CreateTextureView(texture);
            ResourceSet resourceSet = _gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(_textureLayout, view));

            IntPtr texId = (IntPtr)textureData.GetTexID().Handle;
            if (_fontTextureId == (IntPtr)(-1)) _fontTextureId = texId;
            _managedTextures[texId] = new ImGuiManagedTexture { Texture = texture, View = view, ResourceSet = resourceSet };

            textureData.SetStatus(ImTextureStatus.Ok);
        }

        private void UpdateManagedTextureData(ImTextureDataPtr textureData)
        {
            IntPtr texId = (IntPtr)textureData.GetTexID().Handle;
            if (!_managedTextures.TryGetValue(texId, out ImGuiManagedTexture managed)) return;

            Texture texture = managed.Texture;
            PixelFormat newFormat = textureData.Format == ImTextureFormat.Rgba32 ? PixelFormat.R8_G8_B8_A8_UNorm : PixelFormat.R8_UNorm;

            if (texture.Width != textureData.Width || texture.Height != textureData.Height || texture.Format != newFormat)
            {
                managed.ResourceSet.Dispose();
                managed.View.Dispose();
                managed.Texture.Dispose();

                texture = _gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                    (uint)textureData.Width, (uint)textureData.Height, 1, 1, newFormat, TextureUsage.Sampled));
                texture.Name = "ImGui Managed Texture";

                managed.Texture = texture;
                managed.View = _gd.ResourceFactory.CreateTextureView(texture);
                managed.ResourceSet = _gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(_textureLayout, managed.View));

                _managedTextures[texId] = managed;
            }

            if (textureData.Pixels != null)
            {
                uint bytesPerPixel = textureData.Format == ImTextureFormat.Rgba32 ? 4u : 1u;
                _gd.UpdateTexture(
                    texture,
                    (IntPtr)textureData.Pixels,
                    (uint)(bytesPerPixel * textureData.Width * textureData.Height),
                    0, 0, 0,
                    (uint)textureData.Width, (uint)textureData.Height, 1,
                    0, 0);
            }

            textureData.SetStatus(ImTextureStatus.Ok);
        }

        private void DestroyManagedTexture(ImTextureDataPtr textureData)
        {
            IntPtr texId = (IntPtr)textureData.GetTexID().Handle;
            if (_managedTextures.TryGetValue(texId, out ImGuiManagedTexture managed))
            {
                managed.ResourceSet.Dispose();
                managed.View.Dispose();
                managed.Texture.Dispose();
                _managedTextures.Remove(texId);
            }
            textureData.SetStatus(ImTextureStatus.Ok);
        }
        // ---------------------------------------------------------------------

        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, TextureView textureView)
        {
            if (!_setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
            {
                ResourceSet resourceSet = factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, textureView));
                resourceSet.Name = $"ImGui.NET {textureView.Name} Resource Set";
                rsi = new ResourceSetInfo(GetNextImGuiBindingID(), resourceSet);

                _setsByView.Add(textureView, rsi);
                _viewsById.Add(rsi.ImGuiBinding, rsi);
                _ownedResources.Add(resourceSet);
            }

            return rsi.ImGuiBinding;
        }

        public void RemoveImGuiBinding(TextureView textureView)
        {
            if (_setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
            {
                _setsByView.Remove(textureView);
                _viewsById.Remove(rsi.ImGuiBinding);
                _ownedResources.Remove(rsi.ResourceSet);
                rsi.ResourceSet.Dispose();
            }
        }

        private IntPtr GetNextImGuiBindingID()
        {
            int newID = _lastAssignedID++;
            return (IntPtr)newID;
        }

        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, Texture texture)
        {
            if (!_autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
            {
                textureView = factory.CreateTextureView(texture);
                textureView.Name = $"ImGui.NET {texture.Name} View";
                _autoViewsByTexture.Add(texture, textureView);
                _ownedResources.Add(textureView);
            }

            return GetOrCreateImGuiBinding(factory, textureView);
        }

        public void RemoveImGuiBinding(Texture texture)
        {
            if (_autoViewsByTexture.TryGetValue(texture, out TextureView textureView))
            {
                _autoViewsByTexture.Remove(texture);
                _ownedResources.Remove(textureView);
                textureView.Dispose();
                RemoveImGuiBinding(textureView);
            }
        }

        public ResourceSet GetImageResourceSet(IntPtr imGuiBinding)
        {
            if (!_viewsById.TryGetValue(imGuiBinding, out ResourceSetInfo rsi))
            {
                // Fallback auf die intern von ImGui erzeugten Texturen (inkl. Font-Atlas)
                if (_managedTextures.TryGetValue(imGuiBinding, out ImGuiManagedTexture managed))
                {
                    return managed.ResourceSet;
                }
                throw new InvalidOperationException("No registered ImGui binding with id " + imGuiBinding.ToString());
            }

            return rsi.ResourceSet;
        }

        public void ClearCachedImageResources()
        {
            foreach (IDisposable resource in _ownedResources)
            {
                resource.Dispose();
            }

            _ownedResources.Clear();
            _setsByView.Clear();
            _viewsById.Clear();
            _autoViewsByTexture.Clear();
            _lastAssignedID = 100;
        }

        private byte[] LoadEmbeddedShaderCode(ResourceFactory factory, string name, ShaderStages stage, ColorSpaceHandling colorSpaceHandling)
        {
            switch (factory.BackendType)
            {
                case GraphicsBackend.Direct3D11:
                    if (stage == ShaderStages.Vertex && colorSpaceHandling == ColorSpaceHandling.Legacy) { name += "-legacy"; }
                    return GetEmbeddedResourceBytes(name + ".hlsl.bytes");
                case GraphicsBackend.OpenGL:
                    if (stage == ShaderStages.Vertex && colorSpaceHandling == ColorSpaceHandling.Legacy) { name += "-legacy"; }
                    return GetEmbeddedResourceBytes(name + ".glsl");
                case GraphicsBackend.OpenGLES:
                    if (stage == ShaderStages.Vertex && colorSpaceHandling == ColorSpaceHandling.Legacy) { name += "-legacy"; }
                    return GetEmbeddedResourceBytes(name + ".glsles");
                case GraphicsBackend.Vulkan:
                    return GetEmbeddedResourceBytes(name + ".spv");
                case GraphicsBackend.Metal:
                    return GetEmbeddedResourceBytes(name + ".metallib");
                default:
                    throw new NotImplementedException();
            }
        }

        private byte[] GetEmbeddedResourceBytes(string resourceName)
        {
            using (Stream s = _assembly.GetManifestResourceStream(resourceName))
            {
                byte[] ret = new byte[s.Length];
                s.Read(ret, 0, (int)s.Length);
                return ret;
            }
        }

        public void Render(GraphicsDevice gd, CommandList cl)
        {
            if (_frameBegun)
            {
                _frameBegun = false;
                ImGui.Render();
                RenderImDrawData(ImGui.GetDrawData(), gd, cl);
            }
        }

        public void Update(float deltaSeconds, InputSnapshot snapshot)
        {
            BeginUpdate(deltaSeconds);
            UpdateImGuiInput(snapshot);
            EndUpdate();
        }

        protected void BeginUpdate(float deltaSeconds)
        {
            if (_frameBegun) ImGui.Render();
            SetPerFrameImGuiData(deltaSeconds);
        }

        protected void EndUpdate()
        {
            _frameBegun = true;
            ImGui.NewFrame();
        }

        private void SetPerFrameImGuiData(float deltaSeconds)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new Vector2(
                _windowWidth / _scaleFactor.X,
                _windowHeight / _scaleFactor.Y);
            io.DisplayFramebufferScale = _scaleFactor;
            io.DeltaTime = deltaSeconds;
        }

        private bool TryMapKey(Key key, out ImGuiKey result)
        {
            ImGuiKey keyToImGuiKeyShortcut(Key keyToConvert, Key startKey1, ImGuiKey startKey2)
            {
                int changeFromStart1 = (int)keyToConvert - (int)startKey1;
                return (ImGuiKey)((int)startKey2 + changeFromStart1);
            }

            if (key >= Key.F1 && key <= Key.F12)
            {
                result = keyToImGuiKeyShortcut(key, Key.F1, ImGuiKey.F1);
                return true;
            }
            else if (key >= Key.Keypad0 && key <= Key.Keypad9)
            {
                result = keyToImGuiKeyShortcut(key, Key.Keypad0, ImGuiKey.Keypad0);
                return true;
            }
            else if (key >= Key.A && key <= Key.Z)
            {
                result = keyToImGuiKeyShortcut(key, Key.A, ImGuiKey.A);
                return true;
            }
            else if (key >= Key.Number0 && key <= Key.Number9)
            {
                // HINWEIS: ImGui._0 wurde in Hexa zu ImGui.Key0
                result = keyToImGuiKeyShortcut(key, Key.Number0, ImGuiKey.Key0);
                return true;
            }

            switch (key)
            {
                case Key.ShiftLeft:
                case Key.ShiftRight: result = ImGuiKey.ModShift; return true;
                case Key.ControlLeft:
                case Key.ControlRight: result = ImGuiKey.ModCtrl; return true;
                case Key.AltLeft:
                case Key.AltRight: result = ImGuiKey.ModAlt; return true;
                case Key.WinLeft:
                case Key.WinRight: result = ImGuiKey.ModSuper; return true;
                case Key.Menu: result = ImGuiKey.Menu; return true;
                case Key.Up: result = ImGuiKey.UpArrow; return true;
                case Key.Down: result = ImGuiKey.DownArrow; return true;
                case Key.Left: result = ImGuiKey.LeftArrow; return true;
                case Key.Right: result = ImGuiKey.RightArrow; return true;
                case Key.Enter: result = ImGuiKey.Enter; return true;
                case Key.Escape: result = ImGuiKey.Escape; return true;
                case Key.Space: result = ImGuiKey.Space; return true;
                case Key.Tab: result = ImGuiKey.Tab; return true;
                case Key.BackSpace: result = ImGuiKey.Backspace; return true;
                case Key.Insert: result = ImGuiKey.Insert; return true;
                case Key.Delete: result = ImGuiKey.Delete; return true;
                case Key.PageUp: result = ImGuiKey.PageUp; return true;
                case Key.PageDown: result = ImGuiKey.PageDown; return true;
                case Key.Home: result = ImGuiKey.Home; return true;
                case Key.End: result = ImGuiKey.End; return true;
                case Key.CapsLock: result = ImGuiKey.CapsLock; return true;
                case Key.ScrollLock: result = ImGuiKey.ScrollLock; return true;
                case Key.PrintScreen: result = ImGuiKey.PrintScreen; return true;
                case Key.Pause: result = ImGuiKey.Pause; return true;
                case Key.NumLock: result = ImGuiKey.NumLock; return true;
                case Key.KeypadDivide: result = ImGuiKey.KeypadDivide; return true;
                case Key.KeypadMultiply: result = ImGuiKey.KeypadMultiply; return true;
                case Key.KeypadSubtract: result = ImGuiKey.KeypadSubtract; return true;
                case Key.KeypadAdd: result = ImGuiKey.KeypadAdd; return true;
                case Key.KeypadDecimal: result = ImGuiKey.KeypadDecimal; return true;
                case Key.KeypadEnter: result = ImGuiKey.KeypadEnter; return true;
                case Key.Tilde: result = ImGuiKey.GraveAccent; return true;
                case Key.Minus: result = ImGuiKey.Minus; return true;
                case Key.Plus: result = ImGuiKey.Equal; return true;
                case Key.BracketLeft: result = ImGuiKey.LeftBracket; return true;
                case Key.BracketRight: result = ImGuiKey.RightBracket; return true;
                case Key.Semicolon: result = ImGuiKey.Semicolon; return true;
                case Key.Quote: result = ImGuiKey.Apostrophe; return true;
                case Key.Comma: result = ImGuiKey.Comma; return true;
                case Key.Period: result = ImGuiKey.Period; return true;
                case Key.Slash: result = ImGuiKey.Slash; return true;
                case Key.BackSlash:
                case Key.NonUSBackSlash: result = ImGuiKey.Backslash; return true;
                default: result = ImGuiKey.GamepadBack; return false;
            }
        }

        private void UpdateImGuiInput(InputSnapshot snapshot)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.AddMousePosEvent(snapshot.MousePosition.X, snapshot.MousePosition.Y);
            io.AddMouseButtonEvent(0, snapshot.IsMouseDown(MouseButton.Left));
            io.AddMouseButtonEvent(1, snapshot.IsMouseDown(MouseButton.Right));
            io.AddMouseButtonEvent(2, snapshot.IsMouseDown(MouseButton.Middle));
            io.AddMouseButtonEvent(3, snapshot.IsMouseDown(MouseButton.Button1));
            io.AddMouseButtonEvent(4, snapshot.IsMouseDown(MouseButton.Button2));
            io.AddMouseWheelEvent(0f, snapshot.WheelDelta);

            for (int i = 0; i < snapshot.KeyCharPresses.Count; i++)
            {
                io.AddInputCharacter(snapshot.KeyCharPresses[i]);
            }

            for (int i = 0; i < snapshot.KeyEvents.Count; i++)
            {
                KeyEvent keyEvent = snapshot.KeyEvents[i];
                if (TryMapKey(keyEvent.Key, out ImGuiKey imguikey))
                {
                    io.AddKeyEvent(imguikey, keyEvent.Down);
                }
            }
        }

        private void RenderImDrawData(ImDrawDataPtr draw_data, GraphicsDevice gd, CommandList cl)
        {
            if (draw_data.CmdListsCount == 0) return;

            // HEXA.NET PATTERN: Hier werden die Font- und interne Texturen synchronisiert!
            ProcessTextureUpdates(draw_data);

            uint vertexOffsetInVertices = 0;
            uint indexOffsetInElements = 0;

            uint totalVBSize = (uint)(draw_data.TotalVtxCount * sizeof(ImDrawVert));
            if (totalVBSize > _vertexBuffer.SizeInBytes)
            {
                gd.DisposeWhenIdle(_vertexBuffer);
                _vertexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalVBSize * 1.5f), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _vertexBuffer.Name = $"ImGui.NET Vertex Buffer";
            }

            uint totalIBSize = (uint)(draw_data.TotalIdxCount * sizeof(ushort));
            if (totalIBSize > _indexBuffer.SizeInBytes)
            {
                gd.DisposeWhenIdle(_indexBuffer);
                _indexBuffer = gd.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalIBSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
                _indexBuffer.Name = $"ImGui.NET Index Buffer";
            }

            for (int i = 0; i < draw_data.CmdListsCount; i++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdLists[i];

                cl.UpdateBuffer(
                    _vertexBuffer,
                    vertexOffsetInVertices * (uint)sizeof(ImDrawVert),
                    (IntPtr)cmd_list.VtxBuffer.Data,
                    (uint)(cmd_list.VtxBuffer.Size * sizeof(ImDrawVert)));

                cl.UpdateBuffer(
                    _indexBuffer,
                    indexOffsetInElements * sizeof(ushort),
                    (IntPtr)cmd_list.IdxBuffer.Data,
                    (uint)(cmd_list.IdxBuffer.Size * sizeof(ushort)));

                vertexOffsetInVertices += (uint)cmd_list.VtxBuffer.Size;
                indexOffsetInElements += (uint)cmd_list.IdxBuffer.Size;
            }

            Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(
                0f, ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y, 0.0f, -1.0f, 1.0f);
            _gd.UpdateBuffer(_projMatrixBuffer, 0, ref mvp);

            cl.SetVertexBuffer(0, _vertexBuffer);
            cl.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _mainResourceSet);

            draw_data.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

            int vtx_offset = 0;
            int idx_offset = 0;
            for (int n = 0; n < draw_data.CmdListsCount; n++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdLists[n];
                for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
                {
                    ImDrawCmd* pcmd = &cmd_list.CmdBuffer.Data[cmd_i];
                    if (pcmd->UserCallback != null)
                    {
                        throw new NotImplementedException();
                    }
                    else
                    {
                        // HEXA.NET PATTERN: Nutze pcmd->TexRef anstatt pcmd->TextureId
                        ImTextureRef textureRef = pcmd->TexRef;
                        ImTextureID texId = textureRef.GetTexID();

                        IntPtr handle = (IntPtr)texId.Handle;
                        if (handle == IntPtr.Zero && _fontTextureId != (IntPtr)(-1))
                        {
                            handle = _fontTextureId;
                        }

                        cl.SetGraphicsResourceSet(1, GetImageResourceSet(handle));

                        uint clipX = (uint)Math.Max(0, pcmd->ClipRect.X);
                        uint clipY = (uint)Math.Max(0, pcmd->ClipRect.Y);
                        uint clipWidth = (uint)Math.Max(0, Math.Min(_windowWidth, pcmd->ClipRect.Z) - clipX);
                        uint clipHeight = (uint)Math.Max(0, Math.Min(_windowHeight, pcmd->ClipRect.W) - clipY);

                        if (clipWidth > 0 && clipHeight > 0)
                        {
                            cl.SetScissorRect(0, clipX, clipY, clipWidth, clipHeight);
                            cl.DrawIndexed(pcmd->ElemCount, 1, pcmd->IdxOffset + (uint)idx_offset, (int)(pcmd->VtxOffset + vtx_offset), 0);
                        }
                    }
                }
                idx_offset += cmd_list.IdxBuffer.Size;
                vtx_offset += cmd_list.VtxBuffer.Size;
            }
        }

        public void Dispose()
        {
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _projMatrixBuffer?.Dispose();
            _vertexShader?.Dispose();
            _fragmentShader?.Dispose();
            _layout?.Dispose();
            _textureLayout?.Dispose();
            _pipeline?.Dispose();
            _mainResourceSet?.Dispose();

            foreach (IDisposable resource in _ownedResources)
            {
                resource.Dispose();
            }

            // Cleanup für durch ImGui-generierte Texturen
            foreach (var managed in _managedTextures.Values)
            {
                managed.ResourceSet.Dispose();
                managed.View.Dispose();
                managed.Texture.Dispose();
            }
            _managedTextures.Clear();

            ImGui.DestroyContext();
        }

        private struct ResourceSetInfo
        {
            public readonly IntPtr ImGuiBinding;
            public readonly ResourceSet ResourceSet;

            public ResourceSetInfo(IntPtr imGuiBinding, ResourceSet resourceSet)
            {
                ImGuiBinding = imGuiBinding;
                ResourceSet = resourceSet;
            }
        }
    }
}
