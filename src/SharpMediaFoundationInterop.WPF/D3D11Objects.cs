using SharpMediaFoundationInterop.Utils;
using Windows.Win32.Graphics.Direct3D11;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// Textures and views made on a device, each an object of its own - let go of with <see cref="ComObjects.Release"/>,
    /// its memory of the GPU then too. The generated interop's are objects cached of their pointer, let go of only as they
    /// are collected: a pool's textures, a frame's, held of the GPU till then.
    /// </summary>
    internal static unsafe class D3D11Objects
    {
        public static ID3D11Texture2D CreateTexture2D(ID3D11Device device, D3D11_TEXTURE2D_DESC desc, D3D11_SUBRESOURCE_DATA* initial = null)
        {
            ID3D11Texture2D_unmanaged* texture;
            device.CreateTexture2D(&desc, initial, &texture);
            return ComObjects.Take<ID3D11Texture2D>(texture);
        }

        public static ID3D11ShaderResourceView CreateShaderResourceView(ID3D11Device device, ID3D11Resource resource)
        {
            ID3D11ShaderResourceView_unmanaged* view;
            device.CreateShaderResourceView(resource, null, &view);
            return ComObjects.Take<ID3D11ShaderResourceView>(view);
        }

        public static ID3D11RenderTargetView CreateRenderTargetView(ID3D11Device device, ID3D11Resource resource)
        {
            ID3D11RenderTargetView_unmanaged* view;
            device.CreateRenderTargetView(resource, null, &view);
            return ComObjects.Take<ID3D11RenderTargetView>(view);
        }
    }
}
