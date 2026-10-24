using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;
using System.Buffers.Binary;
using System.IO.Compression;
using WinSight.Infrastructure.Media;

namespace WinSight.InferenceHost;

internal sealed record PresentationSlide(int Number, int Width, int Height, bool Hidden, string Artifact);
internal sealed record PresentationResult(int Protocol, string InputPath, string State, string Reason,
    string OfficeVersion, int SlideCount, string RenderPolicy, IReadOnlyList<PresentationSlide> Slides);

internal static partial class NativeMediaCommand
{
    internal static Task<PresentationResult> RenderPresentationRequestAsync(string requestPath) => StartPresentationThread(requestPath,false);
    internal static Task<PresentationResult> RenderPresentationPageRequestAsync(string requestPath) => StartPresentationThread(requestPath,true);
    private static Task<PresentationResult> StartPresentationThread(string requestPath,bool singlePage)
    {
        var done = new TaskCompletionSource<PresentationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { done.SetResult(RenderPresentation(requestPath,singlePage)); } catch(Exception e) { done.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start(); return done.Task;
    }
    private static void CanonicalSlide(string raw,string output,int width,int height,CancellationToken token)
    {
        using var pin=OpenPinnedFile(raw,32L*1024*1024);var rgb=MediaWic.Read(raw);
        if(rgb.Width!=width || rgb.Height!=height)throw new InvalidDataException("PowerPoint pixel dimensions mismatch.");
        WriteCanonicalRgb(rgb,output,token);
    }
    private static void WriteCanonicalRgb(MediaRgb rgb,string output,CancellationToken token)
    {
        int width=rgb.Width,height=rgb.Height;
        using var compressed=new MemoryStream();
        using(var z=new ZLibStream(compressed,CompressionLevel.Fastest,leaveOpen:true))
            for(int row=0;row<height;row++){token.ThrowIfCancellationRequested();z.WriteByte(0);z.Write(rgb.Data,row*width*3,width*3);}
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        file.Write(new byte[]{137,80,78,71,13,10,26,10});
        void Chunk(string name,byte[] data)
        {
            var header=new byte[8];BinaryPrimitives.WriteUInt32BigEndian(header,(uint)data.Length);System.Text.Encoding.ASCII.GetBytes(name,header.AsSpan(4));
            file.Write(header);file.Write(data);uint crc=0xffffffff;
            foreach(byte b in header.AsSpan(4)){crc^=b;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}
            foreach(byte b in data){crc^=b;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}
            Span<byte> sum=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(sum,~crc);file.Write(sum);
        }
        var ihdr=new byte[13];BinaryPrimitives.WriteInt32BigEndian(ihdr,width);BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4),height);ihdr[8]=8;ihdr[9]=2;
        Chunk("IHDR",ihdr);Chunk("IDAT",compressed.ToArray());Chunk("IEND",[]);
    }
    private sealed class OfficeObject(object value,List<object> references)
    {
        internal object Value {get;}=value;
        private object? Invoke(string name,BindingFlags flags,object?[]? args)
        {
            try{return Value.GetType().InvokeMember(name,flags|BindingFlags.OptionalParamBinding,null,Value,args,System.Globalization.CultureInfo.InvariantCulture);}
            catch(TargetInvocationException e)when(e.InnerException is not null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        internal T Get<T>(string name,params object?[] args)=>(T)Invoke(name,BindingFlags.GetProperty,args)!;
        internal void Set(string name,object value)=>Invoke(name,BindingFlags.SetProperty,[value]);
        internal void Call(string name,params object?[] args)=>Invoke(name,BindingFlags.InvokeMethod,args);
        internal OfficeObject NamedObject(string name,string[] names,object?[] args)
        {
            object result;
            try{result=Value.GetType().InvokeMember(name,BindingFlags.InvokeMethod|BindingFlags.OptionalParamBinding,null,Value,args,null,System.Globalization.CultureInfo.InvariantCulture,names)!;}
            catch(TargetInvocationException e)when(e.InnerException is not null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
            references.Add(result);return new(result,references);
        }
        internal OfficeObject Object(string name,bool method=false,params object?[] args)
        {
            var result=Invoke(name,method?BindingFlags.InvokeMethod:BindingFlags.GetProperty,args)!;
            references.Add(result);return new(result,references);
        }
    }
    private static PresentationResult RenderPresentation(string requestPath,bool singlePage)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(50)); var token = limit.Token;
        using var request = OpenPinnedFile(ValidateLocalPath(requestPath,GetDriveType),MaxRequestBytes);
        var bytes = new byte[checked((int)request.Length)];request.ReadExactly(bytes);
        using var json = JsonDocument.Parse(bytes);var root=json.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)throw new JsonException("Presentation request must be an object.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var prop in root.EnumerateObject())
            if(!(prop.Name is "protocol" or "inputPath" or "outputDirectory" or "maxEdge" || prop.Name==(singlePage?"pageNumber":"maxSlides")) || !names.Add(prop.Name))throw new JsonException("Unknown/duplicate presentation request member.");
        if(names.Count!=5 || root.GetProperty("protocol").GetInt32()!=1)throw new JsonException("Unsupported presentation request.");
        int first=singlePage?root.GetProperty("pageNumber").GetInt32():1;
        int maxSlides=singlePage?1:root.GetProperty("maxSlides").GetInt32(),edge=root.GetProperty("maxEdge").GetInt32();
        if(first is <1 or >5000 || maxSlides is <1 or >32 || edge is <256 or >1600)throw new ArgumentOutOfRangeException(nameof(maxSlides));
        var input=ValidateLocalPath(root.GetProperty("inputPath").GetString()!,GetDriveType);
        var output=ValidateLocalPath(root.GetProperty("outputDirectory").GetString()!,GetDriveType);
        if(!Path.GetExtension(input).Equals(".pptx",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Only PPTX is rendered.");
        using var source=OpenPinnedFile(input,MaxInputBytes);
        string version=PowerPointPolicy.Identity();
        PresentationResult Deferred(string reason) => new(1,input,"deferred",reason,version,0,PowerPointPolicy.RenderPolicy,[]);
        using var mutex=new Mutex(false,PowerPointPolicy.AutomationMutex);bool acquired;
        try { acquired=mutex.WaitOne(0); }catch(AbandonedMutexException){acquired=true;}
        if(!acquired)return Deferred("renderer-busy");
        try
        {
            if(PowerPointPolicy.Busy)return Deferred("powerpoint-busy");
            var executable=PowerPointPolicy.InstalledPath();if(executable is null || !File.Exists(executable))return Deferred("powerpoint-missing");
            try{PowerPointPolicy.ValidatePackage(source,token);}
            catch(NotSupportedException){return Deferred("unsupported-content");}
            using var executablePin=OpenPinnedFile(executable,128L*1024*1024);
            PowerPointPolicy.VerifyPublisher(executable);
            ValidateAncestors(output,includeLeaf:false);
            if(!CreateDirectory(output,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Presentation output must be new.");
            using var directoryPin=CreateFile(output,0x80,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            if(directoryPin.IsInvalid)throw new IOException("Cannot pin presentation output directory.");
            ValidateAncestors(output,includeLeaf:true);
            var copy=Path.Combine(output,"input.pptx");source.Position=0;
            using(var writer=new FileStream(copy,FileMode.CreateNew,FileAccess.Write,FileShare.None)) source.CopyTo(writer);
            using var copyPin=OpenPinnedFile(copy,MaxInputBytes);
            token.ThrowIfCancellationRequested();if(PowerPointPolicy.Busy)return Deferred("powerpoint-busy");
            WindowsNativeMediaProcess? worker=null;Task<(string Output,string Error,uint ExitCode)>? monitor=null;
            var com=new List<object>();
            try
            {
                worker=WindowsNativeMediaProcess.Start(executable,"",Path.GetDirectoryName(output)!,TimeSpan.FromSeconds(5),false,NativeMediaOperation.PowerPointServer);
                monitor=worker.RunAsync(65536,TimeSpan.FromSeconds(5),token);
                // No retrying a COM activation against an unknown server: one bounded readiness interval.
                if(token.WaitHandle.WaitOne(1500))token.ThrowIfCancellationRequested();
                var activated=Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application",true)!)!;com.Add(activated);var app=new OfficeObject(activated,com);
                void CheckOwner()
                {
                    var processes=Process.GetProcessesByName("POWERPNT");
                    try { if(processes.Length!=1 || processes[0].Id!=worker.ProcessId)throw new IOException("PowerPoint process ownership is ambiguous."); }
                    finally{foreach(var p in processes)p.Dispose();}
                    if(!string.Equals(Path.TrimEndingDirectorySeparator(app.Get<string>("Path")),Path.GetDirectoryName(executable),StringComparison.OrdinalIgnoreCase))
                        throw new IOException("PowerPoint installation identity mismatch.");
                }
                CheckOwner();var decks=app.Object("Presentations");
                if(decks.Get<int>("Count")!=0)throw new IOException("Unexpected presentation before owned open.");
                int security=app.Get<int>("AutomationSecurity");OfficeObject deck;
                try{app.Set("AutomationSecurity",3);deck=decks.Object("Open",true,copy,-1,-1,0);}
                finally{app.Set("AutomationSecurity",security);}
                var slides=deck.Object("Slides");int count=slides.Get<int>("Count");
                if(count is <1 or >5000 || first>count)throw new InvalidDataException("Unsupported presentation slide count/request.");
                var setup=deck.Object("PageSetup");double w=Convert.ToDouble(setup.Get<object>("SlideWidth")),h=Convert.ToDouble(setup.Get<object>("SlideHeight"));
                if(!double.IsFinite(w)||!double.IsFinite(h)||w<=0||h<=0)throw new InvalidDataException("Invalid slide dimensions.");
                double scale=edge/Math.Max(w,h);int width=Math.Max(1,(int)Math.Round(w*scale)),height=Math.Max(1,(int)Math.Round(h*scale));
                var exported=new List<PresentationSlide>();long total=0;string reason="";
                for(int number=first;number<=Math.Min(count,first+maxSlides-1);number++)
                {
                    token.ThrowIfCancellationRequested();CheckOwner();if(decks.Get<int>("Count")!=1)throw new IOException("Concurrent presentation opened during exclusive rendering.");
                    var slide=slides.Object("Item",true,number);var transition=slide.Object("SlideShowTransition");
                    string artifact=singlePage?"page.png":$"slide-{number:0000}.png",path=Path.Combine(output,artifact);
                    if(Path.Exists(path))throw new IOException("Slide output already exists.");
                    var raw=Path.Combine(output,"raw.png");if(Path.Exists(raw))throw new IOException("Raw slide output already exists.");
                    slide.Call("Export",raw,"PNG",width,height);
                    CanonicalSlide(raw,path,width,height,token);
                    File.Delete(raw);
                    using(var png=OpenPinnedFile(path,32L*1024*1024))total=checked(total+png.Length);
                    if(total>64L*1024*1024){reason="png-byte-limit";break;}
                    exported.Add(new(number,width,height,transition.Get<int>("Hidden")==-1,artifact));
                }
                if(!singlePage && reason.Length==0 && count>maxSlides)reason="slide-limit";
                deck.Call("Close");CheckOwner();if(decks.Get<int>("Count")!=0)throw new IOException("Unexpected joined presentation; no application Quit.");app.Call("Quit");
                for(int n=com.Count-1;n>=0;n--)if(Marshal.IsComObject(com[n]))Marshal.FinalReleaseComObject(com[n]);com.Clear();
                var end=monitor.GetAwaiter().GetResult();if(end.ExitCode!=0)throw new IOException("Owned PowerPoint exited unsuccessfully.");
                return new(1,input,reason.Length==0?"done":"partial",reason,version,count,PowerPointPolicy.RenderPolicy,exported);
            }
            catch(Exception)
            {
                limit.Cancel();
                if(monitor is not null)
                {
                    try{monitor.GetAwaiter().GetResult();}
                    catch(MediaTerminationException){throw;}
                    catch(Exception e) when(e is OperationCanceledException or IOException){ }
                }
                throw;
            }
            finally
            {
                for(int n=com.Count-1;n>=0;n--)if(Marshal.IsComObject(com[n]))Marshal.FinalReleaseComObject(com[n]);
                if(worker is {Settled:true})worker.Dispose();
                else if(worker is not null)MediaRetention.Keep(worker);
            }
        }
        finally{mutex.ReleaseMutex();}
    }
}
