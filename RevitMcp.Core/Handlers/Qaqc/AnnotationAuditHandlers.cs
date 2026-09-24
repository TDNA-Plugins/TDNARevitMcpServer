using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;

namespace RevitMcp.Core.Handlers.Qaqc;

/// <summary>
/// Handles the <see cref="CommandNames.FindUntaggedElements"/> command.
/// Finds instances of the given families that are not tagged in the views
/// placed on the given sheets. Ported from the Check_TagsOnSheets Launchpad
/// script (the Tag Checker tool in the TheatreDNA QAQC Toolkit).
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>familyNames</c> (string[], required) – Family names to check, case-insensitive.</item>
///   <item><c>sheetNumbers</c> (string[], optional) – Sheets to check. Omit to check every sheet.</item>
///   <item><c>maxResults</c> (int, optional) – Cap on untagged rows returned. Defaults to 500.</item>
/// </list>
/// Read-only.
/// </remarks>
public sealed class FindUntaggedElementsHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.FindUntaggedElements;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var familyNames = new HashSet<string>(QaqcSupport.GetStringList(request, "familyNames"), StringComparer.OrdinalIgnoreCase);
            var sheetNumbers = new HashSet<string>(QaqcSupport.GetStringList(request, "sheetNumbers"), StringComparer.OrdinalIgnoreCase);
            int maxResults = QaqcSupport.GetInt(request, "maxResults", 500);

            if (familyNames.Count == 0)
                return QaqcSupport.Fail("Missing required parameter: familyNames (at least one family name).");

            var families = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => familyNames.Contains(f.Name))
                .ToDictionary(f => f.Id, f => f.Name);

            var unknownFamilies = familyNames
                .Where(n => !families.Values.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (families.Count == 0)
                return QaqcSupport.Fail($"None of the families were found in the model: {string.Join(", ", familyNames)}. Use find_elements_by_name to look up exact family names.");

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => sheetNumbers.Count == 0 || sheetNumbers.Contains(s.SheetNumber))
                .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var unknownSheets = sheetNumbers
                .Where(n => !sheets.Any(s => string.Equals(s.SheetNumber, n, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var missing = new List<object>();
            var sheetsWithoutViews = new List<string>();
            int viewsChecked = 0, instancesChecked = 0;

            foreach (var sheet in sheets)
            {
                var viewports = new FilteredElementCollector(doc, sheet.Id)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .ToList();

                if (viewports.Count == 0)
                {
                    sheetsWithoutViews.Add($"{sheet.SheetNumber} - {sheet.Name}");
                    continue;
                }

                foreach (var vp in viewports)
                {
                    if (doc.GetElement(vp.ViewId) is not View view) continue;
                    viewsChecked++;

                    // Every element tagged anywhere in this view, gathered once.
                    var taggedIds = new HashSet<ElementId>();
                    foreach (IndependentTag tag in new FilteredElementCollector(doc, view.Id)
                                 .OfClass(typeof(IndependentTag)))
                    {
                        foreach (var id in tag.GetTaggedLocalElementIds())
                            taggedIds.Add(id);
                    }

                    foreach (FamilyInstance fi in new FilteredElementCollector(doc, view.Id)
                                 .OfClass(typeof(FamilyInstance)))
                    {
                        ElementId? familyId = fi.Symbol?.Family?.Id;
                        if (familyId is null || !families.TryGetValue(familyId, out var familyName)) continue;

                        instancesChecked++;
                        if (taggedIds.Contains(fi.Id)) continue;

                        missing.Add(new
                        {
                            ElementId = fi.Id.Value,
                            FamilyName = familyName,
                            TypeName = fi.Symbol?.Name,
                            SheetNumber = sheet.SheetNumber,
                            SheetName = sheet.Name,
                            ViewId = view.Id.Value,
                            ViewName = view.Name
                        });
                    }
                }
            }

            var rows = QaqcSupport.Cap(missing, maxResults, out bool truncated);

            return QaqcSupport.Ok(new
            {
                SheetsChecked = sheets.Count,
                ViewsChecked = viewsChecked,
                InstancesChecked = instancesChecked,
                UntaggedCount = missing.Count,
                Truncated = truncated,
                Untagged = rows,
                SheetsWithoutViews = sheetsWithoutViews,
                FamiliesNotFound = unknownFamilies,
                SheetsNotFound = unknownSheets,
                Note = instancesChecked == 0
                    ? "None of the families are visible in any checked view, so nothing could be checked."
                    : null
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.AuditFonts"/> command.
/// Reports which fonts text notes, dimensions, spot dimensions and tags use.
/// Ported from the QAQC_FontAudit Launchpad script.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>viewId</c> (long, optional) – Audit one view only. Omit to audit the whole project.</item>
///   <item><c>allowedFonts</c> (string[], optional) – The office-standard fonts. Instances using any other font are listed.</item>
///   <item><c>maxInstances</c> (int, optional) – Cap on listed off-standard instances. Defaults to 300.</item>
/// </list>
/// Read-only.
/// </remarks>
public sealed class AuditFontsHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.AuditFonts;

    private sealed record FontRow(string Category, string TypeName, string Font, long ElementId, ElementId OwnerViewId);

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var viewId = QaqcSupport.GetElementId(request, "viewId");
            var allowed = new HashSet<string>(QaqcSupport.GetStringList(request, "allowedFonts"), StringComparer.OrdinalIgnoreCase);
            int maxInstances = QaqcSupport.GetInt(request, "maxInstances", 300);

            View? scopeView = null;
            if (viewId is not null)
            {
                scopeView = doc.GetElement(viewId) as View;
                if (scopeView is null) return QaqcSupport.Fail($"View not found for ID: {viewId.Value}");
            }

            FilteredElementCollector Collector() => scopeView is null
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, scopeView.Id);

            var typeFontCache = new Dictionary<ElementId, (string TypeName, string Font)>();
            (string TypeName, string Font) TypeInfo(ElementId typeId)
            {
                if (typeFontCache.TryGetValue(typeId, out var cached)) return cached;
                var type = doc.GetElement(typeId);
                string font = "(set in tag family)";
                try
                {
                    var p = type?.get_Parameter(BuiltInParameter.TEXT_FONT);
                    if (p is not null && p.HasValue && !string.IsNullOrEmpty(p.AsString())) font = p.AsString();
                }
                catch { }
                string typeName = type is FamilySymbol fs ? $"{fs.FamilyName} : {fs.Name}" : QaqcSupport.SafeName(type);
                return typeFontCache[typeId] = (typeName, font);
            }

            var rows = new List<FontRow>();
            void Collect(Type cls, Func<Element, string> category)
            {
                foreach (var e in Collector().OfClass(cls).WhereElementIsNotElementType())
                {
                    try
                    {
                        var (typeName, font) = TypeInfo(e.GetTypeId());
                        rows.Add(new FontRow(category(e), typeName, font, e.Id.Value, e.OwnerViewId));
                    }
                    catch { }
                }
            }

            Collect(typeof(TextNote), _ => "Text Note");
            Collect(typeof(Dimension), e => e is SpotDimension ? $"Spot ({e.Category?.Name})" : "Dimension");
            Collect(typeof(IndependentTag), e => $"Tag ({e.Category?.Name})");

            var sheetMap = QaqcSupport.BuildViewToSheetMap(doc);
            var fonts = rows
                .GroupBy(r => r.Font)
                .OrderByDescending(g => g.Count())
                .Select(g => new
                {
                    Font = g.Key,
                    Instances = g.Count(),
                    OffStandard = allowed.Count > 0 && !g.Key.StartsWith("(") && !allowed.Contains(g.Key),
                    ByCategory = g.GroupBy(r => r.Category).OrderBy(c => c.Key)
                        .ToDictionary(c => c.Key, c => c.Count()),
                    Types = g.Select(r => r.TypeName).Distinct().OrderBy(t => t).ToList()
                })
                .ToList();

            object? offStandard = null;
            if (allowed.Count > 0)
            {
                var off = rows.Where(r => !r.Font.StartsWith("(") && !allowed.Contains(r.Font))
                    .Select(r => new
                    {
                        r.ElementId,
                        r.Category,
                        r.TypeName,
                        r.Font,
                        View = QaqcSupport.SafeName(doc.GetElement(r.OwnerViewId)),
                        Sheets = sheetMap.TryGetValue(r.OwnerViewId, out var s) ? s : null
                    });
                var capped = QaqcSupport.Cap(off, maxInstances, out bool truncated);
                offStandard = new { Count = rows.Count(r => !r.Font.StartsWith("(") && !allowed.Contains(r.Font)), Truncated = truncated, Instances = capped };
            }

            return QaqcSupport.Ok(new
            {
                Scope = scopeView is null ? "Entire project" : $"View: {scopeView.Name}",
                TotalInstances = rows.Count,
                Fonts = fonts,
                OffStandardInstances = offStandard,
                Note = "Tag fonts are defined inside each tag family's labels and are not readable from the project; those are reported as '(set in tag family)'."
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.GetNoteBlocks"/> command.
/// Lists note block schedules and the generic annotation family each one is for.
/// Ported from the Check_NoteBlockFamily Launchpad script.
/// </summary>
/// <remarks>No payload. Read-only.</remarks>
public sealed class GetNoteBlocksHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.GetNoteBlocks;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var genericAnnotationId = new ElementId(BuiltInCategory.OST_GenericAnnotation);
            var sheetInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .ToList();

            var noteBlocks = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => !vs.IsTemplate && vs.Definition is not null && vs.Definition.CategoryId == genericAnnotationId)
                .OrderBy(vs => vs.Name)
                .Select(vs =>
                {
                    string? family = null;
                    foreach (var filter in vs.Definition.GetFilters())
                    {
                        try
                        {
                            if (doc.GetElement(filter.GetElementIdValue()) is Family f) { family = f.Name; break; }
                        }
                        catch { /* not an element-valued filter */ }
                    }

                    var instances = new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType().ToElements();
                    family ??= instances.OfType<FamilyInstance>().Select(fi => fi.Symbol?.Family?.Name).FirstOrDefault(n => n is not null);

                    var sheets = sheetInstances
                        .Where(si => si.ScheduleId == vs.Id)
                        .Select(si => doc.GetElement(si.OwnerViewId) as ViewSheet)
                        .Where(s => s is not null)
                        .Select(s => $"{s!.SheetNumber} - {s.Name}")
                        .Distinct()
                        .ToList();

                    return new
                    {
                        ScheduleId = vs.Id.Value,
                        vs.Name,
                        Family = family ?? "(could not be determined)",
                        RowCount = instances.Count,
                        PlacedOnSheets = sheets
                    };
                })
                .ToList();

            return QaqcSupport.Ok(new { Count = noteBlocks.Count, NoteBlocks = noteBlocks });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}
