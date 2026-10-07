using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;

namespace RevitMcp.Core.Handlers;

/// <summary>
/// Handles the <see cref="CommandNames.AuditScheduleBlanks"/> command.
/// Finds elements in a schedule that have blank values in any of the schedule's
/// parameter fields. Read-only: never opens a Transaction.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>scheduleName</c> (string, required) – Exact or partial schedule name (case-insensitive).</item>
///   <item><c>includeHiddenFields</c> (bool, optional, default false) – Also check fields hidden in the schedule.</item>
///   <item><c>fieldNames</c> (string[], optional) – Only check these field names (column headings or parameter names).</item>
///   <item><c>maxElements</c> (int, optional, default 200) – Cap on element rows returned. Counts are always complete.</item>
///   <item><c>scope</c> (string, optional, default "schedule") – "schedule" checks only the elements the schedule
///   shows. "project" checks every element of the schedule's categories in the whole model, ignoring the schedule's
///   filters, phase filter and design option visibility (all options in every option set are included).</item>
/// </list>
/// Every element row reports its design option, phase created, and whether the schedule shows it.
/// A value counts as blank when the parameter is missing on the element/type, has no value,
/// is an empty or whitespace string, or is an unset element reference ("None").
/// Numbers and Yes/No values only count as blank when never set.
/// </remarks>
public sealed class AuditScheduleBlanksHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.AuditScheduleBlanks;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var payload = request.Payload;

            if (payload?.TryGetProperty("scheduleName", out var nameProp) != true ||
                string.IsNullOrWhiteSpace(nameProp.GetString()))
                return new BridgeResponse(Success: false, Error: "Missing required parameter: scheduleName");

            var scheduleName = nameProp.GetString()!.Trim();
            var includeHidden = payload.Value.TryGetProperty("includeHiddenFields", out var hp) &&
                                hp.ValueKind == JsonValueKind.True;
            var maxElements = payload.Value.TryGetProperty("maxElements", out var mp) &&
                              mp.ValueKind == JsonValueKind.Number
                ? Math.Max(0, mp.GetInt32())
                : 200;
            var projectScope = payload.Value.TryGetProperty("scope", out var sp) &&
                               sp.ValueKind == JsonValueKind.String &&
                               string.Equals(sp.GetString(), "project", StringComparison.OrdinalIgnoreCase);

            HashSet<string>? fieldFilter = null;
            if (payload.Value.TryGetProperty("fieldNames", out var fp) && fp.ValueKind == JsonValueKind.Array)
            {
                fieldFilter = fp.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim())
                    .Where(s => s.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (fieldFilter.Count == 0) fieldFilter = null;
            }

            // ---- Find the schedule ------------------------------------------------
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate && !v.IsTitleblockRevisionSchedule && !v.IsInternalKeynoteSchedule)
                .ToList();

            var matches = schedules
                .Where(v => string.Equals(v.Name, scheduleName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                matches = schedules
                    .Where(v => v.Name.Contains(scheduleName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (matches.Count == 0)
                return new BridgeResponse(Success: false,
                    Error: $"No schedule matches '{scheduleName}'.");
            if (matches.Count > 1)
                return new BridgeResponse(Success: false,
                    Error: $"'{scheduleName}' matches {matches.Count} schedules; use the exact name. Matches: " +
                           string.Join(" | ", matches.Select(m => m.Name).OrderBy(n => n).Take(25)));

            var schedule = matches[0];
            var def = schedule.Definition;

            // ---- Pick the fields to check -----------------------------------------
            var fields = new List<(ScheduleField Field, string Heading, string ParamName)>();
            var skipped = new List<Dictionary<string, object?>>();

            for (var i = 0; i < def.GetFieldCount(); i++)
            {
                var f = def.GetField(i);
                var heading = SafeHeading(f);
                var paramName = SafeName(f);

                string? skipReason = null;
                if (f.IsHidden && !includeHidden) skipReason = "Hidden";
                else if (f.IsCalculatedField) skipReason = "Calculated value";
                else if (f.FieldType == ScheduleFieldType.Count) skipReason = "Count field";
                else if (f.FieldType != ScheduleFieldType.Instance &&
                         f.FieldType != ScheduleFieldType.ElementType) skipReason = $"Field type {f.FieldType} not supported";
                else if (f.ParameterId == ElementId.InvalidElementId) skipReason = "No parameter";
                else if (fieldFilter is not null &&
                         !fieldFilter.Contains(heading) && !fieldFilter.Contains(paramName)) skipReason = "Not in fieldNames";

                if (skipReason is not null)
                {
                    // Don't clutter the output with fields the caller filtered out on purpose.
                    if (skipReason != "Not in fieldNames" && skipReason != "Hidden")
                        skipped.Add(new() { ["Field"] = heading, ["Reason"] = skipReason });
                    continue;
                }

                // Column headings can repeat; keep result keys unique.
                var key = heading;
                for (var n = 2; fields.Any(x => x.Heading == key); n++) key = $"{heading} ({n})";
                fields.Add((f, key, paramName));
            }

            if (fields.Count == 0)
                return new BridgeResponse(Success: false,
                    Error: $"Schedule '{schedule.Name}' has no checkable parameter fields with the given options.");

            // ---- Collect the elements ---------------------------------------------
            var scheduled = new FilteredElementCollector(doc, schedule.Id)
                .WhereElementIsNotElementType()
                .ToElements();
            var scheduledIds = new HashSet<ElementId>(scheduled.Select(e => e.Id));

            IList<Element> elements = scheduled;
            var categoryNames = new List<string>();
            if (projectScope)
            {
                // The schedule's categories: its own category, or for a multi-category
                // schedule the categories of the elements it shows.
                var catIds = new HashSet<ElementId>();
                if (def.CategoryId != ElementId.InvalidElementId) catIds.Add(def.CategoryId);
                foreach (var e in scheduled)
                    if (e.Category is not null) catIds.Add(e.Category.Id);

                if (catIds.Count == 0)
                    return new BridgeResponse(Success: false,
                        Error: $"Could not work out the categories of schedule '{schedule.Name}' (it shows no elements).");

                // A document-wide collector has no design option or phase filtering,
                // so it returns elements from every option in every option set.
                elements = new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(catIds.ToList()))
                    .WhereElementIsNotElementType()
                    .Where(e => !e.ViewSpecific && e.GetTypeId() != ElementId.InvalidElementId)
                    .ToList();

                foreach (var id in catIds)
                {
                    var c = Category.GetCategory(doc, id);
                    if (c is not null) categoryNames.Add(c.Name);
                }
                categoryNames.Sort(StringComparer.OrdinalIgnoreCase);
            }

            var blankCounts = fields.ToDictionary(f => f.Heading, _ => 0);
            var rows = new List<Dictionary<string, object?>>();
            var elementsWithBlanks = 0;
            var typeCache = new Dictionary<ElementId, Element?>();
            var optionCache = new Dictionary<ElementId, string>();
            var byOption = new Dictionary<string, int>();
            var blanksNotInSchedule = 0;

            foreach (var el in elements)
            {
                var typeId = el.GetTypeId();
                if (!typeCache.TryGetValue(typeId, out var type))
                {
                    type = typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId);
                    typeCache[typeId] = type;
                }

                var blanks = new List<string>();
                foreach (var (field, heading, paramName) in fields)
                {
                    var reason = BlankReason(doc, el, type, field, paramName);
                    if (reason is null) continue;
                    blankCounts[heading]++;
                    blanks.Add(reason == "Missing" ? $"{heading} (missing)" : heading);
                }

                if (blanks.Count == 0) continue;
                elementsWithBlanks++;

                var option = DesignOptionLabel(doc, el, optionCache);
                byOption[option] = byOption.TryGetValue(option, out var n) ? n + 1 : 1;
                var inSchedule = scheduledIds.Contains(el.Id);
                if (!inSchedule) blanksNotInSchedule++;

                if (rows.Count < maxElements)
                {
                    rows.Add(new()
                    {
                        ["Id"] = el.Id.Value,
                        ["Category"] = el.Category?.Name,
                        ["Family"] = (type as ElementType)?.FamilyName,
                        ["Type"] = SafeElementName(type),
                        ["DesignOption"] = option,
                        ["PhaseCreated"] = PhaseName(doc, el),
                        ["InSchedule"] = inSchedule,
                        ["BlankFields"] = blanks
                    });
                }
            }

            var result = new Dictionary<string, object?>
            {
                ["Schedule"] = schedule.Name,
                ["ScheduleId"] = schedule.Id.Value,
                ["Scope"] = projectScope ? "project" : "schedule",
                ["Categories"] = projectScope ? categoryNames : null,
                ["ElementsInSchedule"] = scheduled.Count,
                ["ElementsChecked"] = elements.Count,
                ["FieldsChecked"] = fields.Count,
                ["ElementsWithBlanks"] = elementsWithBlanks,
                ["BlanksPerField"] = blankCounts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new Dictionary<string, object?> { ["Field"] = kv.Key, ["Blank"] = kv.Value })
                    .ToList(),
                ["BlankElementsNotInSchedule"] = blanksNotInSchedule,
                ["BlankElementsByDesignOption"] = byOption
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new Dictionary<string, object?> { ["DesignOption"] = kv.Key, ["Elements"] = kv.Value })
                    .ToList(),
                ["SkippedFields"] = skipped,
                ["ElementsReturned"] = rows.Count,
                ["Truncated"] = elementsWithBlanks > rows.Count,
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
    /// Returns null when the value is filled, "Missing" when the parameter isn't on the element
    /// or its type, otherwise "Blank".
    /// </summary>
    /// <remarks>
    /// Checks every candidate rather than stopping at the first parameter found: an instance can
    /// expose an empty parameter with the same id/GUID while the schedule actually displays the
    /// type's value (seen with type-bound shared parameters in multi-category schedules).
    /// A value only counts as blank when the instance and the type are both blank.
    /// </remarks>
    private static string? BlankReason(Document doc, Element el, Element? type, ScheduleField field, string paramName)
    {
        var candidates = new List<Parameter>();
        foreach (var e in new[] { el, type })
        {
            if (e is null) continue;
            var byId = FindParameter(doc, e, field.ParameterId);
            if (byId is not null) candidates.Add(byId);
            if (!string.IsNullOrEmpty(paramName))
            {
                Parameter? byName = null;
                try { byName = e.LookupParameter(paramName); } catch { }
                if (byName is not null) candidates.Add(byName);
            }
        }

        if (candidates.Count == 0) return "Missing";
        return candidates.Any(HasRealValue) ? null : "Blank";
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

    private static Parameter? FindParameter(Document doc, Element? e, ElementId paramId)
    {
        if (e is null) return null;

        if (paramId.Value < 0)
        {
            try { return e.get_Parameter((BuiltInParameter)(int)paramId.Value); }
            catch { return null; }
        }

        var pe = doc.GetElement(paramId);
        if (pe is SharedParameterElement spe)
            return e.get_Parameter(spe.GuidValue);
        if (pe is ParameterElement pel)
            return e.get_Parameter(pel.GetDefinition());

        foreach (Parameter p in e.Parameters)
            if (p.Id == paramId) return p;
        return null;
    }

    /// <summary>"Main Model", or "Option Set : Option" with "(primary)" for the primary option.</summary>
    private static string DesignOptionLabel(Document doc, Element el, Dictionary<ElementId, string> cache)
    {
        DesignOption? opt = null;
        try { opt = el.DesignOption; } catch { }
        if (opt is null) return "Main Model";
        if (cache.TryGetValue(opt.Id, out var label)) return label;

        string setName = "";
        try
        {
            var setId = opt.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();
            if (setId is not null && setId != ElementId.InvalidElementId)
                setName = SafeElementName(doc.GetElement(setId)) ?? "";
        }
        catch { }

        var optName = SafeElementName(opt) ?? opt.Id.Value.ToString();
        label = (setName.Length > 0 ? $"{setName} : {optName}" : optName) + (opt.IsPrimary ? " (primary)" : "");
        cache[opt.Id] = label;
        return label;
    }

    private static string? PhaseName(Document doc, Element el)
    {
        try
        {
            var id = el.CreatedPhaseId;
            return id == ElementId.InvalidElementId ? null : SafeElementName(doc.GetElement(id));
        }
        catch { return null; }
    }

    private static string SafeHeading(ScheduleField f)
    {
        try
        {
            var h = f.ColumnHeading;
            return string.IsNullOrWhiteSpace(h) ? f.GetName() : h;
        }
        catch { return f.GetName(); }
    }

    private static string SafeName(ScheduleField f)
    {
        try { return f.GetName(); } catch { return ""; }
    }

    private static string? SafeElementName(Element? e)
    {
        if (e is null) return null;
        try { return e.Name; } catch { return null; }
    }
}
