using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;

namespace RevitMcp.Core.Handlers.Qaqc;

/// <summary>
/// Handles the <see cref="CommandNames.AuditSheetListVisibility"/> command.
/// Checks every sheet's "Appears In Sheet List" against the office convention:
/// sheets whose number starts with the hidden prefix (default "x") should be
/// hidden, all others shown. Optionally fixes the mismatches.
/// Ported from the AppearsInSheetList-QAQC Launchpad scripts.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>hiddenPrefix</c> (string, optional) – Sheet number prefix that marks a sheet as hidden. Defaults to "x".</item>
///   <item><c>fix</c> (bool, optional) – When true, sets the parameter on mismatched sheets. Defaults to false (report only).</item>
/// </list>
/// Only when <c>fix</c> is true does this open a transaction ("MCP: Fix Sheet List Visibility").
/// </remarks>
public sealed class AuditSheetListVisibilityHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.AuditSheetListVisibility;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            string prefix = QaqcSupport.GetString(request, "hiddenPrefix") ?? "x";
            bool fix = QaqcSupport.GetBool(request, "fix", false);

            var mismatches = new List<(ViewSheet Sheet, Parameter Param, bool ShouldAppear)>();
            int total = 0;

            foreach (var sheet in new FilteredElementCollector(doc)
                         .OfClass(typeof(ViewSheet))
                         .Cast<ViewSheet>()
                         .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase))
            {
                var p = sheet.get_Parameter(BuiltInParameter.SHEET_SCHEDULED);
                if (p is null || p.StorageType != StorageType.Integer) continue;
                total++;

                bool shouldAppear = !(sheet.SheetNumber ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                bool appears = p.AsInteger() == 1;
                if (shouldAppear != appears) mismatches.Add((sheet, p, shouldAppear));
            }

            var failures = new List<string>();
            if (fix && mismatches.Count > 0)
            {
                using var tx = new Transaction(doc, "MCP: Fix Sheet List Visibility");
                tx.Start();
                foreach (var m in mismatches)
                {
                    try
                    {
                        if (m.Param.IsReadOnly) failures.Add($"{m.Sheet.SheetNumber}: parameter is read-only");
                        else m.Param.Set(m.ShouldAppear ? 1 : 0);
                    }
                    catch (Exception ex) { failures.Add($"{m.Sheet.SheetNumber}: {ex.Message}"); }
                }
                tx.Commit();
            }

            return QaqcSupport.Ok(new
            {
                Convention = $"Sheets numbered '{prefix}…' are hidden from the sheet list; all others appear.",
                SheetsChecked = total,
                MismatchCount = mismatches.Count,
                Fixed = fix ? mismatches.Count - failures.Count : 0,
                Mismatches = mismatches.Select(m => new
                {
                    SheetId = m.Sheet.Id.Value,
                    m.Sheet.SheetNumber,
                    m.Sheet.Name,
                    CurrentlyAppears = !m.ShouldAppear,
                    ShouldAppear = m.ShouldAppear
                }),
                Failures = failures
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.FindUnpinnedElements"/> command.
/// Counts unpinned elements in the groups that should normally be pinned —
/// grids, levels, links, title blocks, viewports — and optionally pins them.
/// Ported from the PinAll Launchpad script.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>groups</c> (string[], optional) – Any of: Grids, Levels, ScopeBoxes, ReferencePlanes, RevitLinks,
///     CadLinks, TitleBlocks, Viewports, ModelElements, Annotations. Defaults to the first eight
///     (the datum/link/sheet groups offices normally pin).</item>
///   <item><c>pin</c> (bool, optional) – When true, pins everything found. Defaults to false (report only).</item>
///   <item><c>maxIdsPerGroup</c> (int, optional) – Cap on ids listed per group. Defaults to 100.</item>
/// </list>
/// Only when <c>pin</c> is true does this open a transaction ("MCP: Pin Elements").
/// </remarks>
public sealed class FindUnpinnedElementsHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.FindUnpinnedElements;

    private static readonly string[] DefaultGroups =
        { "Grids", "Levels", "ScopeBoxes", "ReferencePlanes", "RevitLinks", "CadLinks", "TitleBlocks", "Viewports" };

    private static readonly BuiltInCategory[] AnnotationCategories =
    {
        BuiltInCategory.OST_TextNotes, BuiltInCategory.OST_Dimensions, BuiltInCategory.OST_SpotElevations,
        BuiltInCategory.OST_SpotCoordinates, BuiltInCategory.OST_SpotSlopes, BuiltInCategory.OST_DetailComponents,
        BuiltInCategory.OST_Lines, BuiltInCategory.OST_KeynoteTags, BuiltInCategory.OST_MaterialTags,
        BuiltInCategory.OST_RoomTags, BuiltInCategory.OST_AreaTags, BuiltInCategory.OST_RevisionClouds,
        BuiltInCategory.OST_GenericAnnotation
    };

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var requested = QaqcSupport.GetStringList(request, "groups");
            var groups = requested.Count > 0 ? requested : DefaultGroups.ToList();
            bool pin = QaqcSupport.GetBool(request, "pin", false);
            int maxIds = QaqcSupport.GetInt(request, "maxIdsPerGroup", 100);

            var unknown = groups.Where(g => Collect(doc, g) is null).ToList();
            if (unknown.Count > 0)
                return QaqcSupport.Fail($"Unknown group(s): {string.Join(", ", unknown)}. Valid: Grids, Levels, ScopeBoxes, ReferencePlanes, RevitLinks, CadLinks, TitleBlocks, Viewports, ModelElements, Annotations.");

            var found = groups.ToDictionary(g => g, g => Collect(doc, g)!.Where(e => !e.Pinned).ToList());

            int pinned = 0;
            var failures = new List<string>();
            if (pin && found.Values.Any(l => l.Count > 0))
            {
                using var tx = new Transaction(doc, "MCP: Pin Elements");
                tx.Start();
                foreach (var e in found.Values.SelectMany(l => l))
                {
                    try { e.Pinned = true; pinned++; }
                    catch (Exception ex) { failures.Add($"{e.Id.Value}: {ex.Message}"); }
                }
                tx.Commit();
            }

            return QaqcSupport.Ok(new
            {
                TotalUnpinned = found.Values.Sum(l => l.Count),
                Pinned = pinned,
                Groups = found.Select(kv => new
                {
                    Group = kv.Key,
                    Unpinned = kv.Value.Count,
                    ElementIds = QaqcSupport.Cap(kv.Value.Select(e => e.Id.Value), maxIds, out bool truncated),
                    Truncated = truncated
                }),
                Failures = failures
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }

    /// <summary>Returns the elements in a named group, or null for an unknown group name.</summary>
    private static IEnumerable<Element>? Collect(Document doc, string group)
    {
        FilteredElementCollector All() => new FilteredElementCollector(doc).WhereElementIsNotElementType();

        switch (group.Replace(" ", "").ToLowerInvariant())
        {
            case "grids": return All().OfClass(typeof(Grid));
            case "levels": return All().OfClass(typeof(Level));
            case "scopeboxes": return All().OfCategory(BuiltInCategory.OST_VolumeOfInterest);
            case "referenceplanes": return All().OfClass(typeof(ReferencePlane)).Where(e => !e.ViewSpecific);
            case "revitlinks": return All().OfClass(typeof(RevitLinkInstance));
            case "cadlinks": return All().OfClass(typeof(ImportInstance));
            case "titleblocks": return All().OfCategory(BuiltInCategory.OST_TitleBlocks);
            case "viewports": return All().OfClass(typeof(Viewport));
            case "annotations":
                return All().WherePasses(new ElementMulticategoryFilter(AnnotationCategories.ToList()));
            case "modelelements":
                return All()
                    .Where(e => e.Category is { CategoryType: CategoryType.Model }
                                && !e.ViewSpecific
                                && e is not RevitLinkInstance and not ImportInstance and not Grid and not Level and not ReferencePlane
                                && e.Category.Id != new ElementId(BuiltInCategory.OST_VolumeOfInterest)
                                && e.Category.Id != new ElementId(BuiltInCategory.OST_TitleBlocks)
                                && e.Location is not null);
            default: return null;
        }
    }
}
