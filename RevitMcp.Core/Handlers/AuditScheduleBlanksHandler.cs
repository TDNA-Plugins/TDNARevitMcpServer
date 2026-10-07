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
/// </list>
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

            // ---- Collect the schedule's elements ----------------------------------
            var elements = new FilteredElementCollector(doc, schedule.Id)
                .WhereElementIsNotElementType()
                .ToElements();

            var blankCounts = fields.ToDictionary(f => f.Heading, _ => 0);
            var rows = new List<Dictionary<string, object?>>();
            var elementsWithBlanks = 0;
            var typeCache = new Dictionary<ElementId, Element?>();

            foreach (var el in elements)
            {
                var typeId = el.GetTypeId();
                if (!typeCache.TryGetValue(typeId, out var type))
                {
                    type = typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId);
                    typeCache[typeId] = type;
                }

                var blanks = new List<string>();
                foreach (var (field, heading, _) in fields)
                {
                    var reason = BlankReason(doc, el, type, field);
                    if (reason is null) continue;
                    blankCounts[heading]++;
                    blanks.Add(reason == "Missing" ? $"{heading} (missing)" : heading);
                }

                if (blanks.Count == 0) continue;
                elementsWithBlanks++;

                if (rows.Count < maxElements)
                {
                    rows.Add(new()
                    {
                        ["Id"] = el.Id.Value,
                        ["Category"] = el.Category?.Name,
                        ["Family"] = (type as ElementType)?.FamilyName,
                        ["Type"] = SafeElementName(type),
                        ["BlankFields"] = blanks
                    });
                }
            }

            var result = new Dictionary<string, object?>
            {
                ["Schedule"] = schedule.Name,
                ["ScheduleId"] = schedule.Id.Value,
                ["ElementsChecked"] = elements.Count,
                ["FieldsChecked"] = fields.Count,
                ["ElementsWithBlanks"] = elementsWithBlanks,
                ["BlanksPerField"] = blankCounts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new Dictionary<string, object?> { ["Field"] = kv.Key, ["Blank"] = kv.Value })
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

    /// <summary>Returns null when the value is filled, "Missing" when the parameter isn't on the element, otherwise "Blank".</summary>
    private static string? BlankReason(Document doc, Element el, Element? type, ScheduleField field)
    {
        var primary = field.FieldType == ScheduleFieldType.ElementType ? type : el;
        var secondary = field.FieldType == ScheduleFieldType.ElementType ? el : type;

        var p = FindParameter(doc, primary, field.ParameterId) ?? FindParameter(doc, secondary, field.ParameterId);
        if (p is null) return "Missing";
        if (!p.HasValue) return "Blank";

        return p.StorageType switch
        {
            StorageType.String => string.IsNullOrWhiteSpace(p.AsString()) ? "Blank" : null,
            StorageType.ElementId => p.AsElementId() == ElementId.InvalidElementId ? "Blank" : null,
            _ => null
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
