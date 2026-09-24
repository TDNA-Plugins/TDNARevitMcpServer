using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;

namespace RevitMcp.Core.Handlers.Qaqc;

/// <summary>
/// A point-in-time record of a model, for change auditing between issues.
/// Same shape as the Project_SnapshotV2 Launchpad script's JSON, so snapshots
/// saved by the script can be compared by the MCP tool and vice versa.
/// </summary>
internal sealed class ModelSnapshot
{
    public string CreatedUtc { get; set; } = "";
    public string DocumentTitle { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string? Label { get; set; }
    public List<SheetRecord> Sheets { get; set; } = new();
    public List<ViewRecord> Views { get; set; } = new();
    public List<ScheduleRecord> Schedules { get; set; } = new();
    public List<ElementRecord> Elements { get; set; } = new();
    public List<AnnotationRecord> Annotations { get; set; } = new();
    public List<DesignOptionRecord> DesignOptions { get; set; } = new();
}

internal sealed class SheetRecord
{
    public string UniqueId { get; set; } = "";
    public string SheetNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string TitleBlockType { get; set; } = "";
    public List<string> PlacedViewUniqueIds { get; set; } = new();
    public List<string> PlacedScheduleUniqueIds { get; set; } = new();
}

internal sealed class ViewRecord
{
    public string UniqueId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ViewType { get; set; } = "";
    public bool IsTemplate { get; set; }
    public List<string> PlacedOnSheetUniqueIds { get; set; } = new();
}

internal sealed class ScheduleRecord
{
    public string UniqueId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ScheduleCategory { get; set; } = "";
    public List<string> PlacedOnSheetUniqueIds { get; set; } = new();
}

internal sealed class ElementRecord
{
    public string UniqueId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Family { get; set; } = "";
    public string TypeName { get; set; } = "";
    public XyzRecord? Location { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class AnnotationRecord
{
    public string UniqueId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerViewUniqueId { get; set; } = "";
    public string OwnerViewName { get; set; } = "";
}

internal sealed class DesignOptionRecord
{
    public string UniqueId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OptionSetName { get; set; } = "";
}

internal sealed class XyzRecord
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

/// <summary>Builds, stores and finds <see cref="ModelSnapshot"/> files.</summary>
internal static class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>%APPDATA%\TheatreDNA\RevitMcp\Snapshots\&lt;model&gt;</summary>
    public static string FolderFor(Document doc)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TheatreDNA", "RevitMcp", "Snapshots");
        return Path.Combine(root, Sanitize(ModelKey(doc)));
    }

    /// <summary>
    /// Workshared local copies carry the user's name in their title, so key on
    /// the central model's file name to keep one history per project.
    /// </summary>
    private static string ModelKey(Document doc)
    {
        try
        {
            if (doc.IsWorkshared)
            {
                var central = doc.GetWorksharingCentralModelPath();
                if (central is not null)
                {
                    string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                    string name = Path.GetFileNameWithoutExtension(path.Replace('/', '\\'));
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }
        catch { }
        return string.IsNullOrWhiteSpace(doc.Title) ? "Untitled" : Path.GetFileNameWithoutExtension(doc.Title);
    }

    public static string Sanitize(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var sb = new StringBuilder();
        foreach (char c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }

    public static List<FileInfo> List(Document doc)
    {
        var dir = new DirectoryInfo(FolderFor(doc));
        return dir.Exists
            ? dir.GetFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
            : new List<FileInfo>();
    }

    public static string Save(Document doc, ModelSnapshot snap, string? label)
    {
        string folder = FolderFor(doc);
        Directory.CreateDirectory(folder);
        string suffix = string.IsNullOrWhiteSpace(label) ? "" : "_" + Sanitize(label!).Replace(' ', '-');
        string path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}{suffix}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snap, JsonOptions), Encoding.UTF8);
        return path;
    }

    public static ModelSnapshot Load(string path) =>
        JsonSerializer.Deserialize<ModelSnapshot>(File.ReadAllText(path, Encoding.UTF8))
        ?? throw new InvalidDataException("Snapshot file is empty.");

    public static ModelSnapshot Build(Document doc, string? label)
    {
        var snap = new ModelSnapshot
        {
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            DocumentTitle = doc.Title,
            ModelPath = doc.PathName ?? "",
            Label = label
        };

        var viewToSheets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var scheduleToSheets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var viewports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList();
        var scheduleInstances = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().ToList();

        foreach (var sh in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().OrderBy(s => s.SheetNumber))
        {
            var rec = new SheetRecord
            {
                UniqueId = sh.UniqueId,
                SheetNumber = sh.SheetNumber ?? "",
                Name = sh.Name ?? "",
                TitleBlockType = TitleBlockType(doc, sh.Id)
            };

            foreach (var vp in viewports.Where(vp => vp.SheetId == sh.Id))
            {
                if (doc.GetElement(vp.ViewId) is not View v) continue;
                rec.PlacedViewUniqueIds.Add(v.UniqueId);
                AddTo(viewToSheets, v.UniqueId, sh.UniqueId);
            }

            foreach (var si in scheduleInstances.Where(si => si.OwnerViewId == sh.Id))
            {
                if (doc.GetElement(si.ScheduleId) is not ViewSchedule vs) continue;
                rec.PlacedScheduleUniqueIds.Add(vs.UniqueId);
                AddTo(scheduleToSheets, vs.UniqueId, sh.UniqueId);
            }

            rec.PlacedViewUniqueIds = rec.PlacedViewUniqueIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            rec.PlacedScheduleUniqueIds = rec.PlacedScheduleUniqueIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            snap.Sheets.Add(rec);
        }

        foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            if (v is ViewSheet || v is ViewSchedule) continue;
            snap.Views.Add(new ViewRecord
            {
                UniqueId = v.UniqueId,
                Name = v.Name ?? "",
                ViewType = v.ViewType.ToString(),
                IsTemplate = v.IsTemplate,
                PlacedOnSheetUniqueIds = viewToSheets.TryGetValue(v.UniqueId, out var s) ? s.ToList() : new List<string>()
            });
        }

        foreach (var vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            string cat = "";
            try
            {
                var catId = vs.Definition?.CategoryId ?? ElementId.InvalidElementId;
                if (catId != ElementId.InvalidElementId) cat = Category.GetCategory(doc, catId)?.Name ?? "";
            }
            catch { }

            snap.Schedules.Add(new ScheduleRecord
            {
                UniqueId = vs.UniqueId,
                Name = vs.Name ?? "",
                ScheduleCategory = cat,
                PlacedOnSheetUniqueIds = scheduleToSheets.TryGetValue(vs.UniqueId, out var s) ? s.ToList() : new List<string>()
            });
        }

        foreach (var fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            if (fi.ViewSpecific || fi.Category is not { CategoryType: CategoryType.Model }) continue;
            snap.Elements.Add(new ElementRecord
            {
                UniqueId = fi.UniqueId,
                Category = fi.Category.Name,
                Family = fi.Symbol?.FamilyName ?? "",
                TypeName = fi.Symbol?.Name ?? "",
                Location = LocationOf(fi),
                Parameters = ParametersOf(doc, fi)
            });
        }

        foreach (var a in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if (!a.ViewSpecific || a.Category is not { CategoryType: CategoryType.Annotation }) continue;
            var owner = doc.GetElement(a.OwnerViewId) as View;
            snap.Annotations.Add(new AnnotationRecord
            {
                UniqueId = a.UniqueId,
                Category = a.Category.Name,
                Name = QaqcSupport.SafeName(a),
                OwnerViewUniqueId = owner?.UniqueId ?? "",
                OwnerViewName = owner?.Name ?? ""
            });
        }

        foreach (var o in new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>())
        {
            var setId = o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId() ?? ElementId.InvalidElementId;
            snap.DesignOptions.Add(new DesignOptionRecord
            {
                UniqueId = o.UniqueId,
                Name = o.Name ?? "",
                OptionSetName = QaqcSupport.SafeName(doc.GetElement(setId))
            });
        }

        return snap;
    }

    private static void AddTo(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(value);
    }

    private static string TitleBlockType(Document doc, ElementId sheetId)
    {
        try
        {
            return string.Join(" | ", new FilteredElementCollector(doc, sheetId)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Select(tb => $"{tb.Symbol?.FamilyName}:{tb.Symbol?.Name}")
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch { return ""; }
    }

    private static XyzRecord? LocationOf(Element e)
    {
        try
        {
            XYZ? p = e.Location switch
            {
                LocationPoint lp => lp.Point,
                LocationCurve lc => lc.Curve?.Evaluate(0.5, true),
                _ => null
            };
            return p is null ? null : new XyzRecord { X = p.X, Y = p.Y, Z = p.Z };
        }
        catch { return null; }
    }

    private static Dictionary<string, string> ParametersOf(Document doc, Element e)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Parameter p in e.Parameters)
        {
            string name = p?.Definition?.Name ?? "";
            if (name.Length == 0 || dict.ContainsKey(name)) continue;
            try
            {
                dict[name] = p!.StorageType switch
                {
                    StorageType.String => p.AsString() ?? "",
                    StorageType.Integer => p.AsValueString() ?? p.AsInteger().ToString(),
                    StorageType.Double => p.AsValueString() ?? p.AsDouble().ToString("R"),
                    StorageType.ElementId => IdText(doc, p.AsElementId()),
                    _ => p.AsValueString() ?? ""
                };
            }
            catch { dict[name] = ""; }
        }
        return dict;
    }

    private static string IdText(Document doc, ElementId id)
    {
        if (id == ElementId.InvalidElementId) return "";
        var el = doc.GetElement(id);
        return el is null ? id.Value.ToString() : $"{QaqcSupport.SafeName(el)} (Id:{id.Value})";
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.SaveModelSnapshot"/> command.
/// Captures sheets, views, schedules, placed model families (with location and
/// every parameter value), annotation and design options to a JSON file.
/// Ported from the Project_SnapshotV2 Launchpad script.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>label</c> (string, optional) – Appended to the file name, e.g. "SD Issue".</item>
/// </list>
/// Read-only for the model; writes a file under %APPDATA%\TheatreDNA\RevitMcp\Snapshots.
/// </remarks>
public sealed class SaveModelSnapshotHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.SaveModelSnapshot;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            string? label = QaqcSupport.GetString(request, "label");
            var snap = SnapshotStore.Build(doc, label);
            string path = SnapshotStore.Save(doc, snap, label);

            return QaqcSupport.Ok(new
            {
                SnapshotPath = path,
                snap.Label,
                Counts = new
                {
                    Sheets = snap.Sheets.Count,
                    Views = snap.Views.Count,
                    Schedules = snap.Schedules.Count,
                    ModelFamilyInstances = snap.Elements.Count,
                    Annotations = snap.Annotations.Count,
                    DesignOptions = snap.DesignOptions.Count
                }
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.ListModelSnapshots"/> command.
/// Lists the saved snapshots for the active model, newest first.
/// </summary>
/// <remarks>No payload. Read-only.</remarks>
public sealed class ListModelSnapshotsHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.ListModelSnapshots;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            var files = SnapshotStore.List(doc);
            return QaqcSupport.Ok(new
            {
                Folder = SnapshotStore.FolderFor(doc),
                Count = files.Count,
                Snapshots = files.Select(f => new
                {
                    Path = f.FullName,
                    f.Name,
                    SavedLocal = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    SizeKb = f.Length / 1024
                })
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Handles the <see cref="CommandNames.CompareModelSnapshot"/> command.
/// Compares a saved snapshot against the model as it is now: sheets added or
/// removed, title block and viewport changes, views and schedules added or
/// deleted, model families added, deleted, moved or with changed parameters,
/// annotation added or deleted, and design options.
/// Ported from the Project_CompareV2 Launchpad script.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>snapshotPath</c> (string, optional) – Baseline file. Defaults to the most recent snapshot of this model.</item>
///   <item><c>moveThresholdFeet</c> (double, optional) – Minimum move to report. Defaults to 1.0.</item>
///   <item><c>ignoreParameters</c> (string[], optional) – Parameter names to leave out of the diff.</item>
///   <item><c>maxRowsPerSection</c> (int, optional) – Cap on detail rows per section. Defaults to 200.</item>
/// </list>
/// Read-only.
/// </remarks>
public sealed class CompareModelSnapshotHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.CompareModelSnapshot;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;
            string? path = QaqcSupport.GetString(request, "snapshotPath");
            double threshold = QaqcSupport.GetDouble(request, "moveThresholdFeet", 1.0);
            var ignore = new HashSet<string>(QaqcSupport.GetStringList(request, "ignoreParameters"), StringComparer.OrdinalIgnoreCase);
            int maxRows = QaqcSupport.GetInt(request, "maxRowsPerSection", 200);

            if (path is null)
            {
                var latest = SnapshotStore.List(doc).FirstOrDefault();
                if (latest is null)
                    return QaqcSupport.Fail($"No snapshots saved for this model yet (looked in {SnapshotStore.FolderFor(doc)}). Call save_model_snapshot first.");
                path = latest.FullName;
            }
            else if (!File.Exists(path))
            {
                return QaqcSupport.Fail($"Snapshot file not found: {path}");
            }

            var oldSnap = SnapshotStore.Load(path);
            var newSnap = SnapshotStore.Build(doc, null);

            // Names for the uniqueIds sheet rows refer to, from whichever side still has them
            var viewNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in oldSnap.Views.Concat(newSnap.Views)) viewNames[v.UniqueId] = v.Name;
            foreach (var s in oldSnap.Schedules.Concat(newSnap.Schedules)) viewNames[s.UniqueId] = s.Name;
            string NameOf(string uid) => viewNames.TryGetValue(uid, out var n) ? n : uid;

            var sections = new List<(string Name, List<Change> Rows)>
            {
                ("Sheets", CompareSheets(oldSnap, newSnap, NameOf)),
                ("Views", AddedDeleted(oldSnap.Views, newSnap.Views, v => v.UniqueId, "View",
                    v => $"{v.Name} ({v.ViewType}{(v.IsTemplate ? ", template" : "")})")),
                ("Schedules", AddedDeleted(oldSnap.Schedules, newSnap.Schedules, s => s.UniqueId, "Schedule",
                    s => $"{s.Name} ({s.ScheduleCategory})")),
                ("Elements", CompareElements(oldSnap, newSnap, threshold, ignore)),
                ("Annotations", AddedDeleted(oldSnap.Annotations, newSnap.Annotations, a => a.UniqueId, "Annotation",
                    a => $"{a.Category}: {a.Name} in '{a.OwnerViewName}'")),
                ("DesignOptions", AddedDeleted(oldSnap.DesignOptions, newSnap.DesignOptions, d => d.UniqueId, "DesignOption",
                    d => $"{d.OptionSetName} : {d.Name}"))
            };

            return QaqcSupport.Ok(new
            {
                Baseline = new { Path = path, oldSnap.CreatedUtc, oldSnap.Label, oldSnap.DocumentTitle },
                TotalChanges = sections.Sum(s => s.Rows.Count),
                Summary = sections.ToDictionary(
                    s => s.Name,
                    s => s.Rows.GroupBy(r => r.ChangeType).ToDictionary(g => g.Key, g => g.Count())),
                Sections = sections.Select(s => new
                {
                    Section = s.Name,
                    Count = s.Rows.Count,
                    Rows = QaqcSupport.Cap(s.Rows, maxRows, out bool truncated),
                    Truncated = truncated
                })
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }

    private sealed record Change(string ChangeType, string Item, string? Detail = null, string? OldValue = null, string? NewValue = null);

    private static List<Change> AddedDeleted<T>(List<T> before, List<T> after, Func<T, string> key, string noun, Func<T, string> describe)
    {
        var oldByUid = before.GroupBy(key).ToDictionary(g => g.Key, g => g.First());
        var newByUid = after.GroupBy(key).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<Change>();
        foreach (var uid in newByUid.Keys.Except(oldByUid.Keys)) rows.Add(new Change($"Added{noun}", describe(newByUid[uid])));
        foreach (var uid in oldByUid.Keys.Except(newByUid.Keys)) rows.Add(new Change($"Deleted{noun}", describe(oldByUid[uid])));
        return rows;
    }

    private static List<Change> CompareSheets(ModelSnapshot oldSnap, ModelSnapshot newSnap, Func<string, string> nameOf)
    {
        var rows = AddedDeleted(oldSnap.Sheets, newSnap.Sheets, s => s.UniqueId, "Sheet", s => $"{s.SheetNumber} - {s.Name}");
        var oldByUid = oldSnap.Sheets.GroupBy(s => s.UniqueId).ToDictionary(g => g.Key, g => g.First());

        foreach (var n in newSnap.Sheets)
        {
            if (!oldByUid.TryGetValue(n.UniqueId, out var o)) continue;
            string sheet = $"{n.SheetNumber} - {n.Name}";

            if (o.SheetNumber != n.SheetNumber || o.Name != n.Name)
                rows.Add(new Change("SheetRenumberedOrRenamed", sheet, "Number - Name", $"{o.SheetNumber} - {o.Name}", sheet));
            if (!string.Equals(o.TitleBlockType, n.TitleBlockType, StringComparison.Ordinal))
                rows.Add(new Change("TitleBlockChanged", sheet, "TitleBlockType", o.TitleBlockType, n.TitleBlockType));

            foreach (var v in n.PlacedViewUniqueIds.Except(o.PlacedViewUniqueIds, StringComparer.OrdinalIgnoreCase))
                rows.Add(new Change("ViewAddedToSheet", sheet, nameOf(v)));
            foreach (var v in o.PlacedViewUniqueIds.Except(n.PlacedViewUniqueIds, StringComparer.OrdinalIgnoreCase))
                rows.Add(new Change("ViewRemovedFromSheet", sheet, nameOf(v)));
            foreach (var s in n.PlacedScheduleUniqueIds.Except(o.PlacedScheduleUniqueIds, StringComparer.OrdinalIgnoreCase))
                rows.Add(new Change("ScheduleAddedToSheet", sheet, nameOf(s)));
            foreach (var s in o.PlacedScheduleUniqueIds.Except(n.PlacedScheduleUniqueIds, StringComparer.OrdinalIgnoreCase))
                rows.Add(new Change("ScheduleRemovedFromSheet", sheet, nameOf(s)));
        }

        return rows;
    }

    private static List<Change> CompareElements(ModelSnapshot oldSnap, ModelSnapshot newSnap, double threshold, HashSet<string> ignore)
    {
        static string Describe(ElementRecord e) => $"{e.Category} | {e.Family} : {e.TypeName} [{e.UniqueId}]";

        var rows = AddedDeleted(oldSnap.Elements, newSnap.Elements, e => e.UniqueId, "Element", Describe);
        var oldByUid = oldSnap.Elements.GroupBy(e => e.UniqueId).ToDictionary(g => g.Key, g => g.First());

        foreach (var n in newSnap.Elements)
        {
            if (!oldByUid.TryGetValue(n.UniqueId, out var o)) continue;

            if (o.Location is not null && n.Location is not null)
            {
                double dx = o.Location.X - n.Location.X, dy = o.Location.Y - n.Location.Y, dz = o.Location.Z - n.Location.Z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dist > threshold)
                    rows.Add(new Change("MovedElement", Describe(n), $"Moved {dist:F2} ft",
                        $"{o.Location.X:F3},{o.Location.Y:F3},{o.Location.Z:F3}",
                        $"{n.Location.X:F3},{n.Location.Y:F3},{n.Location.Z:F3}"));
            }

            if (o.TypeName != n.TypeName || o.Family != n.Family)
                rows.Add(new Change("TypeChanged", Describe(n), "Family : Type", $"{o.Family} : {o.TypeName}", $"{n.Family} : {n.TypeName}"));

            var keys = new HashSet<string>(o.Parameters.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(n.Parameters.Keys);
            foreach (var k in keys)
            {
                if (ignore.Contains(k)) continue;
                o.Parameters.TryGetValue(k, out var ov);
                n.Parameters.TryGetValue(k, out var nv);
                if (!string.Equals(ov ?? "", nv ?? "", StringComparison.Ordinal))
                    rows.Add(new Change("ParameterChanged", Describe(n), k, ov ?? "", nv ?? ""));
            }
        }

        return rows;
    }
}
