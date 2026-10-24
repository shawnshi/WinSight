using System.IO.Compression;
using System.Xml.Linq;

namespace WinSight.Infrastructure.Media;
internal static class OfficeNavigationPolicy
{
    // Candidate precise-open is deliberately narrower than ordinary manual Office open.
    // ReadOnly/AutomationSecurity do not make DDE, field evaluation or Excel formulas inert.
    internal static void Validate(Stream source,bool word,CancellationToken token)
    {
        PowerPointPolicy.ValidateOfficePackage(source,word?"word/document.xml":"xl/workbook.xml",token);
        try
        {
            using var zip=new ZipArchive(source,ZipArchiveMode.Read,leaveOpen:true);
            foreach(var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();string name=entry.FullName;
                if(!word&&(name.Equals("xl/connections.xml",StringComparison.OrdinalIgnoreCase)||name.StartsWith("xl/externalLinks/",StringComparison.OrdinalIgnoreCase)||name.StartsWith("xl/richData/",StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException("Precise-open candidate does not evaluate Excel external data or linked data types. Use ordinary manual open instead.");
                if(!name.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)||!name.StartsWith(word?"word/":"xl/",StringComparison.OrdinalIgnoreCase))continue;
                using var data=entry.Open();var xml=XDocument.Load(data);
                if(word?xml.Descendants().Any(x=>x.Name.LocalName is "instrText" or "fldSimple" or "fldChar")
                    :xml.Descendants().Any(x=>x.Name.LocalName is "f" or "definedName"||x.Name.LocalName.Contains("formula",StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException(word?"Precise-open candidate requires a field-free Word document; ordinary manual open remains available.":"Precise-open candidate requires a formula/defined-name-free workbook; cached formula values remain searchable but ordinary manual open is required.");
            }
        }
        finally{source.Position=0;}
    }
}
