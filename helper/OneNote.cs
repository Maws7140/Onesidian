using System;
using System.Linq;
using System.Xml.Linq;

namespace InkOverlay;

/// <summary>Talks to OneNote desktop through its COM automation interface (OneNote.Application).</summary>
public static class OneNote
{
    public static readonly XNamespace Ns = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    // OneNote enums (Microsoft.Office.Interop.OneNote).
    internal const int HsNotebooks = 2, HsSections = 3, HsPages = 4;
    internal const int Xs2013 = 2;

    internal static IOneNoteApplication Connect() => (IOneNoteApplication)new OneNoteApplicationClass();

    internal static XDocument Hierarchy(IOneNoteApplication app, string startId = "", int scope = HsSections)
    {
        app.GetHierarchy(startId, scope, out var xml, Xs2013);
        return XDocument.Parse(xml);
    }

    /// <summary>`InkOverlay --onenote-probe`: read-only check that we can reach OneNote; lists notebooks.</summary>
    public static int Probe()
    {
        try
        {
            var app = Connect();
            var doc = Hierarchy(app, "", HsSections);
            foreach (var nb in doc.Root!.Elements(Ns + "Notebook"))
            {
                Console.WriteLine($"notebook: {nb.Attribute("name")?.Value}  (path {nb.Attribute("path")?.Value})");
                foreach (var s in nb.Descendants(Ns + "Section"))
                    Console.WriteLine($"   section: {s.Attribute("name")?.Value}");
            }
            app.GetSpecialLocation(2 /* slDefaultNotebookFolder */, out var defaultPath);
            Console.WriteLine($"default notebook folder: {defaultPath}");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED: " + e);
            return 1;
        }
    }
}
