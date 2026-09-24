using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;

namespace RevitMcp.Core.Handlers.Qaqc;

/// <summary>
/// Handles the <see cref="CommandNames.AuditProjectHealth"/> command.
/// A model-health audit: element composition (system / loadable / in-place),
/// annotation, views, sheets, design options, links and imports, groups and
/// warnings, with flags for anything past a review threshold.
/// Ported from the Project_AuditReport Launchpad script.
/// </summary>
/// <remarks>No payload. Read-only. All counts are mutually exclusive.</remarks>
public sealed class AuditProjectHealthHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.AuditProjectHealth;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var flags = new List<string>();
            var errors = new List<string>();

            // -- File -------------------------------------------------------
            double? fileSizeMb = null;
            try
            {
                if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName))
                    fileSizeMb = Math.Round(new FileInfo(doc.PathName).Length / 1_048_576.0, 1);
            }
            catch (Exception ex) { errors.Add($"FileInfo: {ex.Message}"); }

            // -- Model and annotation elements ------------------------------
            var annotationCategoryIds = new HashSet<ElementId>();
            foreach (Category cat in doc.Settings.Categories)
                if (cat.CategoryType == CategoryType.Annotation) annotationCategoryIds.Add(cat.Id);

            var systemByCategory = new Dictionary<string, int>();
            var loadableByCategory = new Dictionary<string, List<string>>();
            var inPlaceByCategory = new Dictionary<string, int>();
            var inPlaceFamilies = new HashSet<string>();
            int textNotes = 0, detailItems = 0, annotationSymbols = 0;
            var detailComponentsId = new ElementId(BuiltInCategory.OST_DetailComponents);

            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (e.Category is null) continue;

                if (annotationCategoryIds.Contains(e.Category.Id))
                {
                    if (e is TextNote) textNotes++;
                    else if (e.Category.Id == detailComponentsId) detailItems++;
                    else annotationSymbols++;
                    continue;
                }

                // Counted in their own sections
                if (e is RevitLinkInstance or ImportInstance or PointCloudInstance or View) continue;
                if (e.Category.CategoryType != CategoryType.Model) continue;
                if (e is IndependentTag or SpatialElementTag) continue;

                string catName = e.Category.Name;
                if (e is FamilyInstance fi && fi.Symbol?.Family is Family fam)
                {
                    if (fam.IsInPlace)
                    {
                        Increment(inPlaceByCategory, catName);
                        inPlaceFamilies.Add(fam.Name);
                    }
                    else
                    {
                        if (!loadableByCategory.TryGetValue(catName, out var names))
                            loadableByCategory[catName] = names = new List<string>();
                        names.Add(fam.Name);
                    }
                    continue;
                }

                Increment(systemByCategory, catName);
            }

            // Tags have CategoryType.Model in the API despite being 2D, so count them by class.
            var tagsByCategory = new Dictionary<string, int>();
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).WhereElementIsNotElementType())
                if (t.Category is not null) Increment(tagsByCategory, t.Category.Name);
            foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(SpatialElementTag)).WhereElementIsNotElementType())
                if (t.Category is not null) Increment(tagsByCategory, t.Category.Name);

            int systemTotal = systemByCategory.Values.Sum();
            int loadableTotal = loadableByCategory.Values.Sum(l => l.Count);
            int inPlaceTotal = inPlaceByCategory.Values.Sum();
            int uniqueLoadableFamilies = loadableByCategory.Values.SelectMany(l => l).Distinct().Count();
            int tagTotal = tagsByCategory.Values.Sum();

            if (uniqueLoadableFamilies > 300) flags.Add($"{uniqueLoadableFamilies} loadable families in use - high; review and purge.");
            if (inPlaceTotal > 0) flags.Add($"{inPlaceTotal} in-place family instance(s) ({inPlaceFamilies.Count} families) - review; in-place families impact performance.");

            // -- Views and sheets -------------------------------------------
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v is not ViewSheet && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.Internal && v.ViewType != ViewType.Undefined)
                .ToList();
            var placedViewIds = new HashSet<ElementId>(QaqcSupport.BuildViewToSheetMap(doc).Keys);
            foreach (var si in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                placedViewIds.Add(si.ScheduleId);

            int viewsNotOnSheets = views.Count(v => !placedViewIds.Contains(v.Id) && v is not ViewSchedule);
            int templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => v.IsTemplate);
            if (views.Count > 500) flags.Add($"{views.Count} views - high; consider purging unused views.");

            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            int onSheetList = sheets.Count(s => s.get_Parameter(BuiltInParameter.SHEET_SCHEDULED)?.AsInteger() == 1);
            int emptySheets = sheets.Count(s => s.GetAllViewports().Count == 0
                && !new FilteredElementCollector(doc, s.Id).OfClass(typeof(ScheduleSheetInstance)).Any());

            // -- Design options, links, groups, warnings --------------------
            int optionSets = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DesignOptionSets).WhereElementIsNotElementType().GetElementCount();
            int options = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).GetElementCount();

            var rvtLinkTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            int rvtLoaded = rvtLinkTypes.Count(t => SafeStatus(t) == LinkedFileStatus.Loaded);
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            int cadLinked = imports.Count(i => i.IsLinked);
            int cadImported = imports.Count - cadLinked;
            int pointClouds = new FilteredElementCollector(doc).OfClass(typeof(PointCloudInstance)).GetElementCount();
            if (cadImported > 0) flags.Add($"{cadImported} imported (not linked) CAD instance(s) - imports bloat the file; run audit_linked_files.");
            if (rvtLoaded < rvtLinkTypes.Count) flags.Add($"{rvtLinkTypes.Count - rvtLoaded} Revit link(s) not loaded; run audit_linked_files.");

            var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).WhereElementIsNotElementType().Cast<Group>().ToList();
            var modelGroupsId = new ElementId(BuiltInCategory.OST_IOSModelGroups);
            var detailGroupsId = new ElementId(BuiltInCategory.OST_IOSDetailGroups);
            int modelGroups = groups.Count(g => g.Category?.Id == modelGroupsId);
            int detailGroups = groups.Count(g => g.Category?.Id == detailGroupsId);
            var usedGroupTypeIds = new HashSet<ElementId>(groups.Select(g => g.GetTypeId()));
            int unusedGroupTypes = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).ToElementIds().Count(id => !usedGroupTypeIds.Contains(id));
            if (unusedGroupTypes > 0) flags.Add($"{unusedGroupTypes} unused group type(s) - purge candidates.");

            int warnings = doc.GetWarnings().Count;
            if (warnings > 100) flags.Add($"{warnings} warnings - high; review with get_warnings.");

            return QaqcSupport.Ok(new
            {
                File = new
                {
                    doc.Title,
                    doc.PathName,
                    FileSizeMb = fileSizeMb,
                    doc.IsWorkshared
                },
                Flags = flags,
                ModelElements = new
                {
                    Total = systemTotal + loadableTotal + inPlaceTotal,
                    SystemFamilyInstances = new { Total = systemTotal, ByCategory = SortDesc(systemByCategory) },
                    LoadableFamilyInstances = new
                    {
                        Total = loadableTotal,
                        UniqueFamilies = uniqueLoadableFamilies,
                        ByCategory = loadableByCategory
                            .OrderByDescending(kv => kv.Value.Count)
                            .Select(kv => new { Category = kv.Key, Instances = kv.Value.Count, Families = kv.Value.Distinct().Count() })
                    },
                    InPlaceFamilyInstances = new { Total = inPlaceTotal, ByCategory = SortDesc(inPlaceByCategory), Families = inPlaceFamilies.OrderBy(n => n) }
                },
                Annotation = new
                {
                    Total = textNotes + detailItems + annotationSymbols + tagTotal,
                    TextNotes = textNotes,
                    DetailComponents = detailItems,
                    AnnotationSymbols = annotationSymbols,
                    Tags = new { Total = tagTotal, ByCategory = SortDesc(tagsByCategory) }
                },
                Views = new
                {
                    Total = views.Count,
                    Templates = templates,
                    NotOnSheets = viewsNotOnSheets,
                    ByType = views.GroupBy(v => v.ViewType.ToString()).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count())
                },
                Sheets = new { Total = sheets.Count, OnSheetList = onSheetList, NotOnSheetList = sheets.Count - onSheetList, Empty = emptySheets },
                DesignOptions = new { OptionSets = optionSets, Options = options },
                Links = new
                {
                    RevitLinks = rvtLinkTypes.Count,
                    RevitLinksLoaded = rvtLoaded,
                    CadLinked = cadLinked,
                    CadImported = cadImported,
                    PointClouds = pointClouds
                },
                Groups = new { ModelGroups = modelGroups, DetailGroups = detailGroups, UnusedGroupTypes = unusedGroupTypes },
                Warnings = warnings,
                Errors = errors
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }

    private static void Increment(Dictionary<string, int> map, string key) =>
        map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;

    private static Dictionary<string, int> SortDesc(Dictionary<string, int> map) =>
        map.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static LinkedFileStatus? SafeStatus(RevitLinkType t)
    {
        try { return t.GetLinkedFileStatus(); }
        catch { return null; }
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.AuditLinkedFiles"/> command.
/// Reports every Revit and CAD link: load status, stored path, whether it is
/// broken, cloud-hosted, pinned, nested, and whether CAD is linked or imported.
/// Ported from the Audit_LinkedFiles Launchpad script (report half only; the
/// script's delete step is left to the user in Manage Links).
/// </summary>
/// <remarks>No payload. Read-only.</remarks>
public sealed class AuditLinkedFilesHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.AuditLinkedFiles;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var flags = new List<string>();

            var rvtInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            var revitLinks = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .OrderBy(t => t.Name)
                .Select(t =>
                {
                    string path = GetPath(t);
                    string status;
                    try { status = t.GetLinkedFileStatus().ToString(); }
                    catch { status = "Unknown"; }

                    bool broken = status is "NotFound" or "Invalid" or "Unknown"
                        || (status == "Unloaded" && path.Length == 0);
                    bool cloud = path.Length == 0 && status is "Invalid" or "NotFound" or "Unloaded";

                    var instances = rvtInstances.Where(i => i.GetTypeId() == t.Id).Select(i => new
                    {
                        InstanceId = i.Id.Value,
                        i.Name,
                        i.Pinned
                    }).ToList();

                    if (broken) flags.Add($"Revit link '{t.Name}' is broken ({status}).");
                    if (instances.Any(i => !i.Pinned)) flags.Add($"Revit link '{t.Name}' has unpinned instance(s).");
                    if (instances.Count == 0 && !t.IsNestedLink) flags.Add($"Revit link '{t.Name}' is loaded as a type but has no placed instance.");

                    return new
                    {
                        TypeId = t.Id.Value,
                        t.Name,
                        Status = status,
                        Path = cloud ? "(cloud / ACC link)" : path.Length == 0 ? "(no path stored)" : path,
                        IsBroken = broken,
                        IsCloud = cloud,
                        t.IsNestedLink,
                        Attachment = SafeAttachment(t),
                        Instances = instances
                    };
                })
                .ToList();

            var cadInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .Select(ii =>
                {
                    var type = doc.GetElement(ii.GetTypeId());
                    string path = type is CADLinkType clt ? GetPath(clt) : "";
                    bool broken = ii.IsLinked && (path.Length == 0 || !SafeExists(path));
                    string name = QaqcSupport.SafeName(type);
                    if (string.IsNullOrEmpty(name)) name = ii.Category?.Name ?? "(CAD)";

                    if (!ii.IsLinked) flags.Add($"CAD '{name}' is IMPORTED, not linked (instance {ii.Id.Value}).");
                    if (broken) flags.Add($"CAD link '{name}' is broken - file not found.");
                    if (!ii.Pinned) flags.Add($"CAD '{name}' instance {ii.Id.Value} is unpinned.");

                    return new
                    {
                        InstanceId = ii.Id.Value,
                        Name = name,
                        ii.IsLinked,
                        Path = path.Length == 0 ? null : path,
                        IsBroken = broken,
                        ViewSpecific = ii.ViewSpecific,
                        OwnerView = ii.ViewSpecific ? QaqcSupport.SafeName(doc.GetElement(ii.OwnerViewId)) : null,
                        ii.Pinned
                    };
                })
                .OrderBy(c => c.Name)
                .ToList();

            return QaqcSupport.Ok(new
            {
                Flags = flags,
                RevitLinks = revitLinks,
                CadInstances = cadInstances,
                Note = "Broken or cloud links can only be re-pathed or removed in Manage Links."
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }

    // GetExternalFileReference throws for cloud (ACC/BIM 360) links.
    private static string GetPath(ElementType type)
    {
        try
        {
            var mp = type.GetExternalFileReference()?.GetAbsolutePath();
            return mp is null ? "" : ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
        }
        catch
        {
            return "";
        }
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static string? SafeAttachment(RevitLinkType t)
    {
        try { return t.AttachmentType.ToString(); }
        catch { return null; }
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.FindMirroredInstances"/> command.
/// Lists family instances that are mirrored — mirrored doors, fixtures and
/// labelled families are a common QAQC catch because their text reads
/// backwards. Ported from the detection half of the Element_MirrorInPlace
/// Launchpad script.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>categoryName</c> (string, optional) – Limit to one category, e.g. "Doors".</item>
///   <item><c>familyName</c> (string, optional) – Limit to one family.</item>
///   <item><c>viewId</c> (long, optional) – Limit to instances visible in a view.</item>
///   <item><c>maxResults</c> (int, optional) – Defaults to 500.</item>
/// </list>
/// Read-only.
/// </remarks>
public sealed class FindMirroredInstancesHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.FindMirroredInstances;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var categoryName = QaqcSupport.GetString(request, "categoryName");
            var familyName = QaqcSupport.GetString(request, "familyName");
            var viewId = QaqcSupport.GetElementId(request, "viewId");
            int maxResults = QaqcSupport.GetInt(request, "maxResults", 500);

            var collector = viewId is null
                ? new FilteredElementCollector(doc)
                : new FilteredElementCollector(doc, viewId);

            var mirrored = collector
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Mirrored)
                .Where(fi => categoryName is null || string.Equals(fi.Category?.Name, categoryName, StringComparison.OrdinalIgnoreCase))
                .Where(fi => familyName is null || string.Equals(fi.Symbol?.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var rows = QaqcSupport.Cap(mirrored.Select(fi => new
            {
                ElementId = fi.Id.Value,
                Category = fi.Category?.Name,
                Family = fi.Symbol?.FamilyName,
                Type = fi.Symbol?.Name,
                Level = QaqcSupport.SafeName(doc.GetElement(fi.LevelId)),
                Mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                fi.HandFlipped,
                fi.FacingFlipped
            }), maxResults, out bool truncated);

            return QaqcSupport.Ok(new
            {
                Scope = viewId is null ? "Entire project" : $"View {viewId.Value}",
                MirroredCount = mirrored.Count,
                ByFamily = mirrored
                    .GroupBy(fi => $"{fi.Category?.Name} | {fi.Symbol?.FamilyName} : {fi.Symbol?.Name}")
                    .OrderByDescending(g => g.Count())
                    .ToDictionary(g => g.Key, g => g.Count()),
                Truncated = truncated,
                Instances = rows,
                Note = "Mirrored means the instance transform is reflected (an odd number of mirror operations). Flip controls (HandFlipped/FacingFlipped) are reported separately and do not make an instance mirrored."
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.GetDesignOptions"/> command.
/// Lists design option sets and their options, which is primary, and how many
/// elements each option holds. Ported from the Export_DesignOptions Launchpad script.
/// </summary>
/// <remarks>No payload. Read-only.</remarks>
public sealed class GetDesignOptionsHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.GetDesignOptions;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;

            var options = new FilteredElementCollector(doc)
                .OfClass(typeof(DesignOption))
                .Cast<DesignOption>()
                .Select(o =>
                {
                    var setId = o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId() ?? ElementId.InvalidElementId;
                    int count = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .WherePasses(new ElementDesignOptionFilter(o.Id))
                        .GetElementCount();
                    return new
                    {
                        SetId = setId,
                        SetName = QaqcSupport.SafeName(doc.GetElement(setId)),
                        OptionId = o.Id.Value,
                        Name = o.Name,
                        o.IsPrimary,
                        ElementCount = count
                    };
                })
                .ToList();

            var sets = options
                .GroupBy(o => o.SetId)
                .OrderBy(g => g.First().SetName)
                .Select(g => new
                {
                    SetId = g.Key.Value,
                    SetName = g.First().SetName,
                    Options = g.OrderByDescending(o => o.IsPrimary).ThenBy(o => o.Name)
                        .Select(o => new { o.OptionId, o.Name, o.IsPrimary, o.ElementCount })
                })
                .ToList();

            var emptyOptions = options.Where(o => o.ElementCount == 0).Select(o => $"{o.SetName} : {o.Name}").ToList();

            return QaqcSupport.Ok(new
            {
                OptionSets = sets.Count,
                Options = options.Count,
                EmptyOptions = emptyOptions,
                Sets = sets
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.AuditTypeNames"/> command.
/// Lists every element type that is actually placed, split into fields on a
/// naming-convention delimiter, and flags types that break the convention or
/// have no Description. Generalised from the HOPA_NameDecoder Launchpad script.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>delimiter</c> (string, optional) – Field separator. Defaults to "_".</item>
///   <item><c>expectedFieldCount</c> (int, optional) – Flag type names with a different number of fields.</item>
///   <item><c>categoryNames</c> (string[], optional) – Limit to these categories.</item>
///   <item><c>includeAnnotation</c> (bool, optional) – Include annotation types. Defaults to false.</item>
///   <item><c>maxResults</c> (int, optional) – Cap on type rows returned. Defaults to 1000.</item>
/// </list>
/// Read-only.
/// </remarks>
public sealed class AuditTypeNamesHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.AuditTypeNames;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            string delimiter = QaqcSupport.GetString(request, "delimiter") ?? "_";
            int expectedFields = QaqcSupport.GetInt(request, "expectedFieldCount", 0);
            var categories = new HashSet<string>(QaqcSupport.GetStringList(request, "categoryNames"), StringComparer.OrdinalIgnoreCase);
            bool includeAnnotation = QaqcSupport.GetBool(request, "includeAnnotation", false);
            int maxResults = QaqcSupport.GetInt(request, "maxResults", 1000);

            // Only types that are actually placed
            var usedTypeIds = new HashSet<ElementId>();
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var typeId = e.GetTypeId();
                if (typeId != ElementId.InvalidElementId) usedTypeIds.Add(typeId);
            }

            var rows = usedTypeIds
                .Select(id => doc.GetElement(id) as ElementType)
                .Where(t => t?.Category is not null)
                .Where(t => includeAnnotation || t!.Category.CategoryType == CategoryType.Model)
                .Where(t => categories.Count == 0 || categories.Contains(t!.Category.Name))
                .Select(t =>
                {
                    string typeName = QaqcSupport.SafeName(t);
                    var fields = typeName.Split(new[] { delimiter }, StringSplitOptions.None).Select(f => f.Trim()).ToList();
                    string description = t!.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() ?? "";

                    var issues = new List<string>();
                    if (expectedFields > 0 && fields.Count != expectedFields)
                        issues.Add($"{fields.Count} field(s), expected {expectedFields}");
                    if (fields.Count > 1 && fields.Any(f => f.Length == 0))
                        issues.Add("empty field (doubled or trailing delimiter)");
                    if (typeName != typeName.Trim() || typeName.Contains("  "))
                        issues.Add("stray whitespace");
                    if (string.IsNullOrWhiteSpace(description))
                        issues.Add("no Description");

                    return new
                    {
                        TypeId = t.Id.Value,
                        Category = t.Category.Name,
                        Family = t.FamilyName,
                        Type = typeName,
                        Description = description,
                        Fields = fields,
                        Issues = issues
                    };
                })
                .OrderBy(r => r.Category).ThenBy(r => r.Family).ThenBy(r => r.Type)
                .ToList();

            var withIssues = rows.Where(r => r.Issues.Count > 0).ToList();
            var capped = QaqcSupport.Cap(rows, maxResults, out bool truncated);

            return QaqcSupport.Ok(new
            {
                Delimiter = delimiter,
                TypesInUse = rows.Count,
                TypesWithIssues = withIssues.Count,
                IssueCounts = withIssues.SelectMany(r => r.Issues)
                    .GroupBy(i => i.Contains("expected") ? "wrong field count" : i)
                    .ToDictionary(g => g.Key, g => g.Count()),
                Truncated = truncated,
                Types = capped
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}
