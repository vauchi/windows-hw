// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Vauchi.WindowsHw.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Imaging;

namespace Vauchi.WindowsHw.Preview;

/// <summary>
/// The display shell's neutral surface for one preview id, as the app
/// hands it over: it takes a composition swap chain and says how large it
/// is. On WinUI it is a SwapChainPanel; the app's wiring implements this.
/// </summary>
public interface ISwapChainHost
{
    /// <summary>Takes the swap chain (an IDXGISwapChain1 pointer) to show. Called once per swap chain.</summary>
    void Attach(IntPtr swapChain);

    /// <summary>The surface's size, in the units its swap chain is laid out in.</summary>
    (double Width, double Height) Size { get; }
}

public enum PreviewDevice
{
    /// <summary>The graphics card.</summary>
    Hardware,

    /// <summary>WARP, Windows' software rasterizer: for a machine without a graphics card.</summary>
    Software,
}

/// <summary>
/// The camera preview through Direct3D (ADR-077 amendment 2026-11-01): each
/// frame is copied into a composition swap chain the display shell's
/// surface holds, never through Core or the display shell. Frames arrive
/// on the camera's thread.
/// </summary>
public sealed class SwapChainPreview(ISwapChainHost host, PreviewDevice device = PreviewDevice.Hardware)
    : IPreviewOutput, IDisposable
{
    private const Format Pixels = Format.B8G8R8A8_UNorm;
    private const uint Buffers = 2;

    private readonly object _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _frame;
    private int _width;
    private int _height;
    private bool _disposed;

    public void Show(SoftwareBitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            throw new ArgumentException($"a preview frame is BGRA8, not {frame.BitmapPixelFormat}", nameof(frame));
        }
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        byte[] pixels = Bytes(frame, width, height);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Prepare(width, height);
            unsafe
            {
                fixed (byte* source = pixels)
                {
                    _context!.UpdateSubresource(_frame!, 0, null, (IntPtr)source, (uint)(width * 4), 0);
                }
            }
            using (ID3D11Texture2D back = _swapChain!.GetBuffer<ID3D11Texture2D>(0))
            {
                _context.CopyResource(back, _frame);
            }
            Fit(width, height);
            _swapChain.Present(1, PresentFlags.None);
        }
    }

    /// <summary>The last frame drawn, as BGRA8 rows without padding.</summary>
    internal (int Width, int Height, byte[] Pixels) ReadBack()
    {
        lock (_gate)
        {
            if (_frame is null)
            {
                return (0, 0, Array.Empty<byte>());
            }
            var description = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Pixels,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            };
            using ID3D11Texture2D staging = _device!.CreateTexture2D(description);
            _context!.CopyResource(staging, _frame);
            MappedSubresource mapped = _context.Map(staging, 0, MapMode.Read);
            try
            {
                var pixels = new byte[_width * _height * 4];
                for (int row = 0; row < _height; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        mapped.DataPointer + (nint)(row * mapped.RowPitch), pixels, row * _width * 4, _width * 4);
                }
                return (_width, _height, pixels);
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Release();
        }
    }

    private static byte[] Bytes(SoftwareBitmap frame, int width, int height)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)(width * height * 4));
        frame.CopyToBuffer(buffer);
        return buffer.ToArray();
    }

    private void Prepare(int width, int height)
    {
        if (_device is null)
        {
            Create(width, height);
            return;
        }
        if (width == _width && height == _height)
        {
            return;
        }
        _frame?.Dispose();
        _swapChain!.ResizeBuffers(Buffers, (uint)width, (uint)height, Pixels, SwapChainFlags.None).CheckError();
        Size(width, height);
    }

    private void Create(int width, int height)
    {
        DriverType driver = device == PreviewDevice.Software ? DriverType.Warp : DriverType.Hardware;
        D3D11.D3D11CreateDevice(
            null, driver, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out _device, out _context).CheckError();
        using IDXGIFactory2 factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _swapChain = factory.CreateSwapChainForComposition(_device, new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Pixels,
            BufferCount = Buffers,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Scaling = Scaling.Stretch,
        });
        Size(width, height);
        host.Attach(_swapChain.NativePointer);
    }

    private void Size(int width, int height)
    {
        _width = width;
        _height = height;
        _frame = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Pixels,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
    }

    private void Fit(int width, int height)
    {
        (double surfaceWidth, double surfaceHeight) = host.Size;
        PreviewFit fit = PreviewFit.Into(width, height, surfaceWidth, surfaceHeight);
        if (fit == PreviewFit.None)
        {
            return;
        }
        using IDXGISwapChain2 laidOut = _swapChain!.QueryInterface<IDXGISwapChain2>();
        laidOut.MatrixTransform = new Matrix3x2(
            (float)fit.Scale, 0, 0, (float)fit.Scale, (float)fit.Left, (float)fit.Top);
    }

    private void Release()
    {
        _frame?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _frame = null;
        _swapChain = null;
        _context = null;
        _device = null;
    }
}
