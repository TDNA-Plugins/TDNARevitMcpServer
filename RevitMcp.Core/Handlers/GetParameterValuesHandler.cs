using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;

namespace RevitMcp.Core.Handlers;

/// <summary>
/// Handles the <see cref="CommandNames.GetParameterValues"/> command.
/// Reads one or more parameter values from many elements at once, across every design
/// option and phase in the model. Read-only: never opens a Transaction.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>parameterNames</c> (string[], required) – Parameter names as shown in Revit properties.</item>
///   <item><c>categories</c> (string[], optional) – Category names (e.g. "Furniture", "Specialty Equipment"). Omit for all model categories.</item>
///   <item><c>elementIds</c> (long[], optional) – Only these elements. Overrides <c>categories</c>.</item>
///   <item><c>designOption</c> (string, optional, default "all") – "all", "main" (Main Model only), "primary"
///   (Main Model plus primary options), "secondary" (non-primary options only), or text matched against
///   "Option Set : Option" (case-insensitive).</item>
///   <item><c>familyContains</c> (string, optional) – Only elements whose family or type name contains this text.</item>
///   <item><c>maxElements</c> (int, optional, default 2000) – Cap on element rows returned.</item>
/// </list>
/// For each parameter the instance is checked first, then the type. When both exist, the one
/// with a value wins. Each value reports Value (display string), Source ("Instance", "Type" or
/// "Missing") and IsBlank. Every parameter with the given name is checked, so a filled duplicate is
/// found even when the first same-named parameter is empty; SameNamedParameters reports such duplicates.
/// </remarks>
public sealed class GetParameterValuesHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.GetParameterValues;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var payload = request.Payload;
            if (payload is not { ValueKind: JsonValueKind.Object })
                return new BridgeResponse(Success: false, Error: "Missing payload.");

            var paramNames = ReadStrings(payload.Value, "parameterNames");
            if (paramNames.Count == 0)
                return new BridgeResponse(Success: false, Error: "Missing required parameter: parameterNames");

            var categoryNames = ReadStrings(payload.Value, "categories");
            var ids = new List<ElementId>();
            if (payload.Value.TryGetProperty("elementIds", out var idp) && idp.ValueKind == JsonValueKind.Array)
                foreach (var v in idp.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.Number) ids.Add(new ElementId(v.GetInt64()));

            var optionFilter = ReadString(payload.Value, "designOption") ?? "all";
            var familyContains = ReadString(payload.Value, "familyContains");
            var maxElements = payload.Value.TryGetProperty("maxElements", out var mp) && mp.ValueKind == JsonValueKind.Number
                ? Math.Max(0, mp.GetInt32())
                : 2000;

            // ---- Collect candidate elements (document-wide: all design options and phases) ----
            IEnumerable<Element> source;
            var unknownCategories = new List<string>();
            if (ids.Count > 0)
            {
                source = ids.Select(doc.GetElement).Where(e => e is not null)!;
            }
            else
            {
                var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                if (categoryNames.Count > 0)
                {
                    var catIds = new List<ElementId>();
                    var all = doc.Settings.Categories.Cast<Category>().ToList();
                    foreach (var name in categoryNames)
                    {
                        var c = all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (c is null) unknownCategories.Add(name);
                        else catIds.Add(c.Id);
                    }
                    if (catIds.Count == 0)
                        return new BridgeResponse(Success: false,
                            Error: "None of the categories were found: " + string.Join(", ", categoryNames));
                    collector = collector.WherePasses(new ElementMulticategoryFilter(catIds));
                }
                source = collector.Where(e =>
                    e.Category is { CategoryType: CategoryType.Model } &&
                    !e.ViewSpecific &&
                    e.GetTypeId() != ElementId.InvalidElementId);
            }

            var typeCache = new Dictionary<ElementId, Element?>();
            var optionCache = new Dictionary<ElementId, DesignOptionInfo.Info>();
            var rows = new List<Dictionary<string, object?>>();
            var matched = 0;
            var blankCounts = paramNames.ToDictionary(n => n, _ => 0, StringComparer.OrdinalIgnoreCase);
            var missingCounts = paramNames.ToDictionary(n => n, _ => 0, StringComparer.OrdinalIgnoreCase);
            var duplicateCounts = paramNames.ToDictionary(n => n, _ => 0, StringComparer.OrdinalIgnoreCase);

            foreach (var el in source)
            {
                var info = DesignOptionInfo.Get(doc, el, optionCache);
                if (!DesignOptionInfo.Matches(info, optionFilter)) continue;

                var typeId = el.GetTypeId();
                if (!typeCache.TryGetValue(typeId, out var type))
                {
                    type = typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId);
                    typeCache[typeId] = type;
                }

                var family = (type as ElementType)?.FamilyName;
                var typeName = SafeName(type);
                if (!string.IsNullOrEmpty(familyContains) &&
                    !(family ?? "").Contains(familyContains!, StringComparison.OrdinalIgnoreCase) &&
                    !(typeName ?? "").Contains(familyContains!, StringComparison.OrdinalIgnoreCase))
                    continue;

                matched++;
                var values = new Dictionary<string, object?>();
                foreach (var name in paramNames)
                {
                    var (value, src, blank, count) = Read(el, type, name);
                    if (src == "Missing") missingCounts[name]++;
                    else if (blank) blankCounts[name]++;
                    if (count > 1) duplicateCounts[name]++;
                    var v = new Dictionary<string, object?> { ["Value"] = value, ["Source"] = src, ["IsBlank"] = blank };
                    if (count > 1) v["SameNamedParameters"] = count;
                    values[name] = v;
                }

                if (rows.Count < maxElements)
                {
                    rows.Add(new()
                    {
                        ["Id"] = el.Id.Value,
                        ["Category"] = el.Category?.Name,
                        ["Family"] = family,
                        ["Type"] = typeName,
                        ["OptionSet"] = info.Set,
                        ["Option"] = info.Option,
                        ["IsPrimary"] = info.IsPrimary,
                        ["PhaseCreated"] = PhaseName(doc, el),
                        ["Values"] = values
                    });
                }
            }

            var result = new Dictionary<string, object?>
            {
                ["ElementsMatched"] = matched,
                ["ElementsReturned"] = rows.Count,
                ["Truncated"] = matched > rows.Count,
                ["UnknownCategories"] = unknownCategories,
                ["Summary"] = paramNames.Select(n => new Dictionary<string, object?>
                {
                    ["Parameter"] = n,
                    ["Blank"] = blankCounts[n],
                    ["Missing"] = missingCounts[n],
                    ["Filled"] = matched - blankCounts[n] - missingCounts[n],
                    ["ElementsWithSameNamedDuplicates"] = duplicateCounts[n]
                }).ToList(),
                ["Elements"] = rows
            };
            return new BridgeResponse(Success: true, Data: JsonSerializer.SerializeToElement(result));
        }
        catch (Exception ex)
        {
            return new BridgeResponse(Success: false, Error: ex.Message);
        }
    }

    /// <summary>
    /// Reads a parameter from the instance, then the type. Every parameter with that name is
    /// checked (LookupParameter only returns the first, and a model can carry two same-named
    /// parameters where only one is filled); a filled value wins over an empty one.
    /// </summary>
    private static (string? Value, string Source, bool IsBlank, int Count) Read(Element el, Element? type, string name)
    {
        var inst = AllNamed(el, name);
        var typ = AllNamed(type, name);
        var count = inst.Count + typ.Count;

        var p = inst.FirstOrDefault(HasRealValue);
        if (p is not null) return (Display(p), "Instance", false, count);
        p = typ.FirstOrDefault(HasRealValue);
        if (p is not null) return (Display(p), "Type", false, count);
        if (inst.Count > 0) return (Display(inst[0]), "Instance", true, count);
        if (typ.Count > 0) return (Display(typ[0]), "Type", true, count);
        return (null, "Missing", true, 0);
    }

    private static List<Parameter> AllNamed(Element? e, string name)
    {
        if (e is null) return new List<Parameter>();
        try { return e.GetParameters(name).Where(x => x is not null).ToList(); }
        catch
        {
            try { var one = e.LookupParameter(name); return one is null ? new List<Parameter>() : new List<Parameter> { one }; }
            catch { return new List<Parameter>(); }
        }
    }

    private static bool HasRealValue(Parameter p)
    {
        if (!p.HasValue) return false;
        return p.StorageType switch
        {
            StorageType.String => !string.IsNullOrWhiteSpace(p.AsString()),
            StorageType.ElementId => p.AsElementId() != ElementId.InvalidElementId,
            _ => true
        };
    }

    private static string? Display(Parameter p)
    {
        try
        {
            var s = p.AsValueString();
            if (!string.IsNullOrEmpty(s)) return s;
            return p.StorageType switch
            {
                StorageType.String => p.AsString(),
                StorageType.Integer => p.AsInteger().ToString(),
                StorageType.Double => p.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                StorageType.ElementId => p.AsElementId().Value.ToString(),
                _ => null
            };
        }
        catch { return null; }
    }

    private static List<string> ReadStrings(JsonElement payload, string name)
    {
        var list = new List<string>();
        if (payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                    list.Add(x.GetString()!.Trim());
        return list;
    }

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static string? SafeName(Element? e)
    {
        if (e is null) return null;
        try { return e.Name; } catch { return null; }
    }

    private static string? PhaseName(Document doc, Element el)
    {
        try
        {
            var id = el.CreatedPhaseId;
            return id == ElementId.InvalidElementId ? null : SafeName(doc.GetElement(id));
        }
        catch { return null; }
    }
}

/// <summary>Design option lookup shared by the read-only audit tools.</summary>
internal static class DesignOptionInfo
{
    /// <summary>Design option of an element. Main Model elements have Set and Option null and IsPrimary true.</summary>
    internal sealed record Info(string? Set, string? Option, bool IsPrimary)
    {
        public string Label => Set is null ? "Main Model" : $"{Set} : {Option}";
    }

    private static readonly Info Main = new(null, null, true);

    /// <summary>Gets (and caches) the option set, option name and primary flag for an element.</summary>
    public static Info Get(Document doc, Element el, Dictionary<ElementId, Info> cache)
    {
        DesignOption? opt = null;
        try { opt = el.DesignOption; } catch { }
        if (opt is null) return Main;
        if (cache.TryGetValue(opt.Id, out var info)) return info;

        string? setName = null;
        try
        {
            var setId = opt.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();
            if (setId is not null && setId != ElementId.InvalidElementId)
                setName = doc.GetElement(setId)?.Name;
        }
        catch { }

        string optName;
        try { optName = opt.Name; } catch { optName = opt.Id.Value.ToString(); }
        info = new Info(setName ?? "(unknown set)", optName, opt.IsPrimary);
        cache[opt.Id] = info;
        return info;
    }

    /// <summary>Applies the designOption filter: all, main, primary, secondary, or label text.</summary>
    public static bool Matches(Info info, string filter)
    {
        switch (filter.Trim().ToLowerInvariant())
        {
            case "":
            case "all": return true;
            case "main": return info.Set is null;
            case "primary": return info.IsPrimary;
            case "secondary": return !info.IsPrimary;
            default: return info.Label.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }
}
