using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Microsoft.Win32;

namespace WinSight.Infrastructure.Media;

internal static class PowerPointPolicy
{
    internal const string RenderPolicy = "powerpoint-slide-export-png-v1";
    internal const string AutomationMutex=@"Local\WinSight.PowerPoint.Render";
    internal static bool Busy
    {
        get
        {
            var processes = Process.GetProcessesByName("POWERPNT");
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    internal static string? InstalledPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\powerpnt.exe");
        if (key?.GetValue(null) is not string path) return null;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\",StringComparison.Ordinal) || path.Contains('"')
            || !string.Equals(Path.GetFileName(path),"POWERPNT.EXE",StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unsupported PowerPoint installation path.");
        return Path.GetFullPath(path);
    }
    internal static string Identity()
    {
        var path = InstalledPath();
        return path is null || !File.Exists(path) ? "missing" : FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
    }
    internal static void VerifyPublisher(string path)
    {
        // Cache-only WinTrust: no online revocation request or runtime dependency download.
        var info = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UI = 2, Union = 1, File = ptr, Action = 1, Flags = 0x1010 };
        try
        {
            Marshal.StructureToPtr(info,ptr,false);
            if (WinVerifyTrust(new IntPtr(-1),ref action,ref data) != 0) throw new IOException("PowerPoint executable signature validation failed.");
#pragma warning disable SYSLIB0057
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!signer.Subject.Split(',').Any(part => part.Trim().Equals("O=Microsoft Corporation",StringComparison.Ordinal)))
                throw new IOException("PowerPoint executable is not signed by Microsoft Corporation.");
        }
        finally
        {
            data.Action = 2; WinVerifyTrust(new IntPtr(-1),ref action,ref data);
            Marshal.DestroyStructure<TrustFile>(ptr); Marshal.FreeHGlobal(ptr);
        }
    }
    internal static void ValidatePackage(Stream source, CancellationToken token) => ValidateOfficePackage(source,"ppt/presentation.xml",token);
    internal static void ValidateOfficePackage(Stream source,string expectedMainPart,CancellationToken token)
    {
        long old = source.Position; source.Position = 0;
        try
        {
            using var zip = new ZipArchive(source,ZipArchiveMode.Read,leaveOpen:true);
            if (zip.Entries.Count > 10000) throw new InvalidDataException("PowerPoint package entry bound exceeded.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0; int nodes = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested(); var name=entry.FullName; var low=name.ToLowerInvariant();
                if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Contains('\0')
                    || name.Split('/').Any(p=>p is "." or "..") || !names.Add(name))
                    throw new InvalidDataException("Ambiguous PowerPoint package entry.");
                expanded=checked(expanded+entry.Length);
                if (expanded > 64L*1024*1024) throw new InvalidDataException("PowerPoint package expansion bound exceeded.");
                if (low.Contains("vba") || low.Contains("activex") || low.Contains("/embeddings/") || low.StartsWith("customui/") || low.EndsWith(".svg"))
                    throw new NotSupportedException("Active/embedded/SVG content is not supported by the safe PowerPoint render policy.");
                if (!low.EndsWith(".xml") && !low.EndsWith(".rels")) continue;
                if (entry.Length > 8L*1024*1024) throw new InvalidDataException("PowerPoint XML part bound exceeded.");
                using var input=entry.Open();
                using var reader=XmlReader.Create(input,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8L*1024*1024 });
                while(reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if(++nodes>1000000 || reader.Depth>128) throw new InvalidDataException("PowerPoint XML structure bound exceeded.");
                    if(reader.NodeType!=XmlNodeType.Element) continue;
                    if(reader.LocalName is "oleObj" or "control") throw new NotSupportedException("Active PowerPoint object is not rendered.");
                    if(reader.LocalName=="Relationship" && string.Equals(reader.GetAttribute("TargetMode"),"External",StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException("External PowerPoint dependencies are not opened for rendering.");
                    var content=reader.GetAttribute("ContentType");
                    if(content is not null && (content.Contains("macroEnabled",StringComparison.OrdinalIgnoreCase) || content.Contains("vbaProject",StringComparison.OrdinalIgnoreCase)))
                        throw new NotSupportedException("Macro-enabled PowerPoint package is not rendered.");
                }
            }
            if(!names.Contains(expectedMainPart) || !names.Contains("[Content_Types].xml")) throw new InvalidDataException("Office package main part is missing.");
        }
        finally { source.Position=old; }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct TrustFile { internal uint Size; [MarshalAs(UnmanagedType.LPWStr)] internal string Path; internal IntPtr Handle,Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        internal uint Size; internal IntPtr Callback,Sip; internal uint UI,Revocation,Union; internal IntPtr File; internal uint Action;
        internal IntPtr State,Url; internal uint Flags,Context; internal IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll",ExactSpelling=true,PreserveSig=true)] private static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);
}
