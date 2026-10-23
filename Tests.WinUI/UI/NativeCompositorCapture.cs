using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Bezoro.AhkManager.WinUI.Tests.UI;

// Test-only capture of the explicitly supplied owned HWND. No picker, monitor or
// arbitrary desktop target is exposed. WGC includes native compositor/backdrop layers.
internal static class NativeWindowCapture
{
    public static async Task CaptureAsync(Window window,string path,bool requireDiagnosticMarker=false)
    {
        if(!GraphicsCaptureSession.IsSupported())throw new InvalidOperationException("Windows Graphics Capture unavailable");
        nint hwnd=WinRT.Interop.WindowNative.GetWindowHandle(window);
        if(hwnd==0||!IsWindowVisible(hwnd))throw new InvalidOperationException("Owned native HWND must be visible");
        GetWindowThreadProcessId(hwnd,out uint pid);
        if(pid!=Environment.ProcessId)throw new InvalidOperationException("Capture target must belong to this fixture process");
        await Task.Delay(200);
        var item=CreateItem(hwnd);
        nint d3d=0,context=0,dxgi=0,inspectable=0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0,1,0,0x20,0,0,7,out d3d,out _,out context));
            var dxgiId=new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d,in dxgiId,out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi,out inspectable));
            using var device=WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            using var pool=Direct3D11CaptureFramePool.CreateFreeThreaded(device,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,item.Size);
            using var session=pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled=false;
            int frames=0,events=0,emptyReads=0;
            bool expectLight=window.Content is FrameworkElement content&&content.ActualTheme==ElementTheme.Light;
            var frameStats=new List<string>();
            Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool,object> handler=(_,_)=>Interlocked.Increment(ref events);
            pool.FrameArrived+=handler;
            try
            {
                session.StartCapture();
                var deadline=System.Diagnostics.Stopwatch.StartNew();SoftwareBitmap? accepted=null;
                // Pull serially: an event may arrive while a prior surface copy is in
                // flight. Never discard that notification without draining the pool.
                while(deadline.Elapsed<TimeSpan.FromSeconds(8))
                {
                    using var frame=pool.TryGetNextFrame();
                    if(frame is null){emptyReads++;await Task.Delay(16);continue;}
                    var pixels=await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().WaitAsync(TimeSpan.FromSeconds(8)-deadline.Elapsed);
                    var rawPixels=new Windows.Storage.Streams.Buffer((uint)(pixels.PixelWidth*pixels.PixelHeight*4));pixels.CopyToBuffer(rawPixels);byte[] rawBytes=rawPixels.ToArray();
                    int lit=0,bright=0,total=0,marker=0;
                    for(int y=64;y<pixels.PixelHeight-8;y++)for(int x=8;x<pixels.PixelWidth-8;x++)
                    {
                        int offset=(y*pixels.PixelWidth+x)*4;int maximum=Math.Max(rawBytes[offset],Math.Max(rawBytes[offset+1],rawBytes[offset+2]));
                        if(maximum>=24)lit++;if(maximum>=190)bright++;if(rawBytes[offset+2]>180&&rawBytes[offset+1]<60&&rawBytes[offset]<60)marker++;total++;
                    }
                    double litRatio=(double)lit/Math.Max(1,total),brightRatio=(double)bright/Math.Max(1,total);
                    bool currentFrame=litRatio>0.10&&(expectLight?brightRatio>0.35:brightRatio<0.45)&&(!requireDiagnosticMarker||marker>2000);
                    frameStats.Add($"frame={++frames};events={Volatile.Read(ref events)};litRatio={litRatio:F5};brightRatio={brightRatio:F5};markerPixels={marker};expectedLight={expectLight};accepted={currentFrame}");
                    if(currentFrame){accepted=pixels;break;}
                    pixels.Dispose();
                }
                using var bitmap=accepted??throw new TimeoutException("No native frame matched the expected scene within eight seconds.");
                var raw=new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth*bitmap.PixelHeight*4));bitmap.CopyToBuffer(raw);byte[] bytes=raw.ToArray();
                int rgb=0,alpha=0;for(int i=0;i<bytes.Length;i+=4){if(bytes[i]!=0||bytes[i+1]!=0||bytes[i+2]!=0)rgb++;if(bytes[i+3]!=0)alpha++;}
                File.WriteAllText(path+".pixels.txt",$"width={bitmap.PixelWidth}\nheight={bitmap.PixelHeight}\nrgbNonzero={rgb}\nalphaNonzero={alpha}\nalphaMode={bitmap.BitmapAlphaMode}\nrootLoaded={window.Content is FrameworkElement element&&element.IsLoaded}\nrootType={(window.Content as FrameworkElement)?.Name}\nvisible={window.AppWindow.IsVisible}\n");
                File.WriteAllBytes(path,[]);var file=await StorageFile.GetFileFromPathAsync(path);using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);encoder.SetSoftwareBitmap(bitmap);await encoder.FlushAsync();
            }
            finally{pool.FrameArrived-=handler;frameStats.Add($"totalEvents={events};drainedFrames={frames};emptyReads={emptyReads}");File.WriteAllLines(path+".frames.txt",frameStats);}
        }
        finally{if(inspectable!=0)Marshal.Release(inspectable);if(dxgi!=0)Marshal.Release(dxgi);if(context!=0)Marshal.Release(context);if(d3d!=0)Marshal.Release(d3d);}
    }
    static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        const string name="Windows.Graphics.Capture.GraphicsCaptureItem";
        nint text=0,factory=0,item=0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name,name.Length,out text));
            var interop=new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");Marshal.ThrowExceptionForHR(RoGetActivationFactory(text,ref interop,out factory));
            var create=Marshal.GetDelegateForFunctionPointer<CreateForWindow>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory),3*IntPtr.Size));
            var iid=new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");Marshal.ThrowExceptionForHR(create(factory,hwnd,in iid,out item));
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally{if(item!=0)Marshal.Release(item);if(factory!=0)Marshal.Release(factory);if(text!=0)WindowsDeleteString(text);}
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int CreateForWindow(nint self,nint hwnd,in Guid iid,out nint item);
    [DllImport("combase.dll",CharSet=CharSet.Unicode)]static extern int WindowsCreateString(string value,int length,out nint result);
    [DllImport("combase.dll")]static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")]static extern int RoGetActivationFactory(nint name,ref Guid iid,out nint result);
    [DllImport("d3d11.dll")]static extern int D3D11CreateDevice(nint adapter,uint driver,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out uint level,out nint context);
    [DllImport("d3d11.dll")]static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi,out nint device);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
}
