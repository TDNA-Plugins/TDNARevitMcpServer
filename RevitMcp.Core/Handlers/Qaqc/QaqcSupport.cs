using System.Text.Json;
using Autodesk.Revit.DB;
using RevitMcp.Core.Messages;

namespace RevitMcp.Core.Handlers.Qaqc;

/// <summary>
/// Shared plumbing for the QAQC handlers: payload parsing, response building,
/// and a few Revit lookups several audits need.
/// </summary>
/// <remarks>
/// Payload readers treat an explicit JSON <c>null</c> the same as a missing
/// property. The server tools serialize optional arguments as anonymous
/// objects, so an omitted argument arrives as <c>"name": null</c>, not absent.
/// </remarks>
internal static class QaqcSupport
{
    /// <summary>Wraps a result object in a successful response.</summary>
    public static BridgeResponse Ok(object result) =>
        new(Success: true, Data: JsonSerializer.SerializeToElement(result));

    /// <summary>Builds a failed response with a message for the caller.</summary>
    public static BridgeResponse Fail(string error) => new(Success: false, Error: error);

    private static bool TryGet(BridgeRequest request, string name, out JsonElement value)
    {
        value = default;
        if (request.Payload is not { ValueKind: JsonValueKind.Object } payload) return false;
        if (!payload.TryGetProperty(name, out value)) return false;
        return value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined;
    }

    /// <summary>Reads a string property, or null when absent or blank.</summary>
    public static string? GetString(BridgeRequest request, string name)
    {
        if (!TryGet(request, name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Reads an element id property, or null when absent.</summary>
    public static ElementId? GetElementId(BridgeRequest request, string name) =>
        TryGet(request, name, out var v) && v.ValueKind == JsonValueKind.Number
            ? new ElementId(v.GetInt64())
            : null;

    /// <summary>Reads an integer property, or the default when absent.</summary>
    public static int GetInt(BridgeRequest request, string name, int defaultValue) =>
        TryGet(request, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : defaultValue;

    /// <summary>Reads a number property, or the default when absent.</summary>
    public static double GetDouble(BridgeRequest request, string name, double defaultValue) =>
        TryGet(request, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : defaultValue;

    /// <summary>Reads a boolean property, or the default when absent.</summary>
    public static bool GetBool(BridgeRequest request, string name, bool defaultValue)
    {
        if (!TryGet(request, name, out var v)) return defaultValue;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
    }

    /// <summary>Reads a string array property. Returns an empty list when absent.</summary>
    public static List<string> GetStringList(BridgeRequest request, string name)
    {
        var list = new List<string>();
        if (!TryGet(request, name, out var v) || v.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                list.Add(item.GetString()!.Trim());
        }
        return list;
    }

    /// <summary>Element.Name can throw for some element types.</summary>
    public static string SafeName(Element? e)
    {
        if (e is null) return "";
        try { return e.Name ?? ""; }
        catch { return ""; }
    }

    /// <summary>Resolves the view to audit: the given id, or the active view when none is given.</summary>
    public static View? ResolveView(Document doc, View? activeView, ElementId? viewId) =>
        viewId is null ? activeView : doc.GetElement(viewId) as View;

    /// <summary>
    /// Maps each view placed on a sheet to the sheet(s) it is on, as "number - name".
    /// Legends and schedules can be on several sheets, so each view maps to a list.
    /// </summary>
    public static Dictionary<ElementId, List<string>> BuildViewToSheetMap(Document doc)
    {
        var map = new Dictionary<ElementId, List<string>>();

        foreach (var sheet in new FilteredElementCollector(doc)
                     .OfClass(typeof(ViewSheet))
                     .Cast<ViewSheet>())
        {
            string label = $"{sheet.SheetNumber} - {sheet.Name}";
            foreach (var vpId in sheet.GetAllViewports())
            {
                if (doc.GetElement(vpId) is not Viewport vp) continue;
                if (!map.TryGetValue(vp.ViewId, out var sheets))
                    map[vp.ViewId] = sheets = new List<string>();
                sheets.Add(label);
            }
        }

        return map;
    }

    /// <summary>Takes up to <paramref name="max"/> items and reports whether anything was cut.</summary>
    public static List<T> Cap<T>(IEnumerable<T> items, int max, out bool truncated)
    {
        var all = items.ToList();
        truncated = all.Count > max;
        return truncated ? all.Take(max).ToList() : all;
    }
}
