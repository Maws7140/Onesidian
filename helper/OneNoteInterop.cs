using System;
using System.Runtime.InteropServices;

namespace InkOverlay;

// OneNote's automation interface (from its type library, OneNote 2013+ / Microsoft 365).
// Declared directly: OneNote's IDispatch won't hand out type info, so late binding (dynamic) fails.
// The methods must stay in type-library order; only the ones we call need exact signatures.

[ComImport, Guid("DC67E480-C3CB-49F8-8232-60B0C2056C8E")]
class OneNoteApplicationClass { }

[ComImport, Guid("452AC71A-B655-4967-A208-A4CC39DD7949"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IOneNoteApplication
{
    void GetHierarchy([MarshalAs(UnmanagedType.BStr)] string startNodeId, int scope,
        [MarshalAs(UnmanagedType.BStr)] out string hierarchyXml, int schema);
    void UpdateHierarchy([MarshalAs(UnmanagedType.BStr)] string changesXml, int schema);
    void OpenHierarchy([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.BStr)] string relativeToObjectId,
        [MarshalAs(UnmanagedType.BStr)] out string objectId, int createFileType);
    void DeleteHierarchy([MarshalAs(UnmanagedType.BStr)] string objectId, DateTime expectedLastModified, bool deletePermanently);
    void CreateNewPage([MarshalAs(UnmanagedType.BStr)] string sectionId, [MarshalAs(UnmanagedType.BStr)] out string pageId, int newPageStyle);
    void CloseNotebook([MarshalAs(UnmanagedType.BStr)] string notebookId, bool force);
    void GetHierarchyParent([MarshalAs(UnmanagedType.BStr)] string objectId, [MarshalAs(UnmanagedType.BStr)] out string parentId);
    void GetPageContent([MarshalAs(UnmanagedType.BStr)] string pageId, [MarshalAs(UnmanagedType.BStr)] out string pageXml,
        int pageInfo, int schema);
    void UpdatePageContent([MarshalAs(UnmanagedType.BStr)] string pageChangesXml, DateTime expectedLastModified, int schema, bool force);
    void GetBinaryPageContent([MarshalAs(UnmanagedType.BStr)] string pageId, [MarshalAs(UnmanagedType.BStr)] string callbackId,
        [MarshalAs(UnmanagedType.BStr)] out string binaryB64);
    void DeletePageContent([MarshalAs(UnmanagedType.BStr)] string pageId, [MarshalAs(UnmanagedType.BStr)] string objectId,
        DateTime expectedLastModified, bool force);
    void NavigateTo([MarshalAs(UnmanagedType.BStr)] string hierarchyObjectId, [MarshalAs(UnmanagedType.BStr)] string objectId, bool newWindow);
    void NavigateToUrl([MarshalAs(UnmanagedType.BStr)] string url, bool newWindow);
    void Publish([MarshalAs(UnmanagedType.BStr)] string hierarchyId, [MarshalAs(UnmanagedType.BStr)] string targetFilePath,
        int publishFormat, [MarshalAs(UnmanagedType.BStr)] string clsidOfExporter);
    void OpenPackage([MarshalAs(UnmanagedType.BStr)] string pathPackage, [MarshalAs(UnmanagedType.BStr)] string pathDest,
        [MarshalAs(UnmanagedType.BStr)] out string pathOut);
    void GetHyperlinkToObject([MarshalAs(UnmanagedType.BStr)] string hierarchyId, [MarshalAs(UnmanagedType.BStr)] string pageContentObjectId,
        [MarshalAs(UnmanagedType.BStr)] out string hyperlink);
    void FindPages([MarshalAs(UnmanagedType.BStr)] string startNodeId, [MarshalAs(UnmanagedType.BStr)] string searchString,
        [MarshalAs(UnmanagedType.BStr)] out string hierarchyXml, bool includeUnindexedPages, bool display, int schema);
    void FindMeta([MarshalAs(UnmanagedType.BStr)] string startNodeId, [MarshalAs(UnmanagedType.BStr)] string searchStringName,
        [MarshalAs(UnmanagedType.BStr)] out string hierarchyXml, bool includeUnindexedPages, int schema);
    void GetSpecialLocation(int specialLocation, [MarshalAs(UnmanagedType.BStr)] out string path);
}
