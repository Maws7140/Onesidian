using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Color = System.Windows.Media.Color;
using Rect = System.Windows.Rect;

namespace InkOverlay;

/// <summary>Windows' own ink renderer (Windows.UI.Input.Inking), the one Windows Ink apps draw with.</summary>
[ComImport, Guid("407fb1de-f85a-4150-97cf-b7fb274fb4f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IInkD2DRenderer
{
    void Draw(IntPtr deviceContext, IntPtr strokeIterable, [MarshalAs(UnmanagedType.Bool)] bool highContrast);
}

/// <summary>
/// OneNote's pencil is Windows' pencil (Windows Ink's pencil kind: graphite grain that fills in where you
/// press harder). WPF can't draw it, so a pencil stroke is drawn by Windows' own ink renderer into a bitmap
/// (Direct2D), which the stroke then shows. Null when Windows can't (no Direct3D device, older Windows):
/// the pencil falls back to the approximation in Pens.
/// </summary>
public static class WindowsInk
{
    static readonly Guid RendererClsid = new("4044e60c-7b01-4671-a97c-04e0210a07a5");
    const int MaxSide = 4096; // px; bigger strokes are drawn at a lower scale

    static ID3D11Device? d3d;
    static ID2D1DeviceContext? ctx;
    static IInkD2DRenderer? renderer;
    static bool unavailable;

    /// <summary>A stroke drawn by Windows, and where it goes (ink units).</summary>
    public sealed record Rendered(BitmapSource Image, Rect Bounds);

    /// <summary>
    /// The pencil stroke through these points (ink units, pressure 0..1) drawn by Windows at `pxPerUnit`
    /// device pixels per ink unit, in `color` (premultiplied over transparent).
    /// </summary>
    public static Rendered? Pencil(System.Windows.Input.StylusPointCollection points, double width, double height, Color color, bool ignorePressure, double pxPerUnit)
    {
        if (unavailable || points.Count == 0 || !(pxPerUnit > 0)) return null;
        try
        {
            Init();
            var builder = new Windows.UI.Input.Inking.InkStrokeBuilder();
            var attrs = Windows.UI.Input.Inking.InkDrawingAttributes.CreateForPencil();
            attrs.Color = Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);
            attrs.Size = new Windows.Foundation.Size(Math.Max(0.1, width), Math.Max(0.1, height));
            attrs.PencilProperties.Opacity = Math.Clamp(color.A / 255.0, 0.01, 1);
            attrs.IgnorePressure = ignorePressure;
            builder.SetDefaultDrawingAttributes(attrs);
            var inkPoints = points.Select(p => new Windows.UI.Input.Inking.InkPoint(new Windows.Foundation.Point(p.X, p.Y), ignorePressure ? 0.5f : p.PressureFactor)).ToList();
            if (inkPoints.Count == 1) inkPoints.Add(inkPoints[0]);
            var stroke = builder.CreateStrokeFromInkPoints(inkPoints, System.Numerics.Matrix3x2.Identity);
            stroke.DrawingAttributes = attrs;
            var container = new Windows.UI.Input.Inking.InkStrokeContainer();
            container.AddStroke(stroke);

            var r = stroke.BoundingRect;
            var bounds = new Rect(r.X, r.Y, r.Width, r.Height);
            bounds.Inflate(Math.Max(width, height) * 0.5 + 1, Math.Max(width, height) * 0.5 + 1);
            double scale = Math.Min(pxPerUnit, MaxSide / Math.Max(bounds.Width, bounds.Height));
            int w = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));

            var pixel = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            using var target = ctx!.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(pixel, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
            using var readback = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(pixel, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
            ctx.Target = target;
            ctx.BeginDraw();
            ctx.Clear(new Color4(0, 0, 0, 0));
            ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation((float)-bounds.X, (float)-bounds.Y) * System.Numerics.Matrix3x2.CreateScale((float)scale);
            var iterable = WinRT.MarshalInspectable<object>.FromManaged(container.GetStrokes());
            try { renderer!.Draw(ctx.NativePointer, iterable, false); }
            finally { Marshal.Release(iterable); }
            ctx.EndDraw();
            ctx.Target = null;
            readback.CopyFromBitmap(target);
            var map = readback.Map(MapOptions.Read);
            BitmapSource image;
            try { image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, map.Bits, (int)map.Pitch * h, (int)map.Pitch); }
            finally { readback.Unmap(); }
            image.Freeze();
            return new Rendered(image, bounds);
        }
        catch (Exception e)
        {
            // A lost device comes back on the next stroke; anything else means this Windows can't.
            Protocol.Log("Windows ink renderer: " + e.Message);
            Reset();
            if (e is COMException or TypeLoadException or DllNotFoundException or InvalidCastException && renderer == null) unavailable = true;
            return null;
        }
    }

    static void Init()
    {
        if (ctx != null) return;
        try { d3d = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, Vortice.Direct3D.FeatureLevel.Level_11_0); }
        catch { d3d = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, Vortice.Direct3D.FeatureLevel.Level_11_0); }
        using var dxgi = d3d.QueryInterface<IDXGIDevice>();
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        using var device = factory.CreateDevice(dxgi);
        ctx = device.CreateDeviceContext(DeviceContextOptions.None);
        renderer ??= (IInkD2DRenderer)Activator.CreateInstance(Type.GetTypeFromCLSID(RendererClsid, true)!)!;
    }

    static void Reset()
    {
        ctx?.Dispose(); ctx = null;
        d3d?.Dispose(); d3d = null;
    }

    /// <summary>The image as PNG, base64 (for the note's in-page copy of the ink).</summary>
    public static string Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
}
