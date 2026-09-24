using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;

namespace RevitMcp.Core.Handlers.Qaqc;

/// <summary>
/// Handles the <see cref="CommandNames.CompareFamilyToFile"/> command.
/// Compares the family open in the Family Editor against another .rfa on disk:
/// types added or missing, parameters added or missing (with shared/instance
/// status), and type parameter values that differ.
/// Ported from the comparison half of the QAQC_Compare Revit Families Launchpad
/// script; the adopt-changes half stays in the script.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>filePath</c> (string, required) – Full path of the .rfa to compare against.</item>
///   <item><c>maxValueDifferences</c> (int, optional) – Cap on listed value differences. Defaults to 500.</item>
/// </list>
/// Read-only. The other file is opened in the background and closed without saving.
/// </remarks>
public sealed class CompareFamilyToFileHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.CompareFamilyToFile;

    private sealed record ParamInfo(string Name, bool IsInstance, bool IsShared, string? Guid, string? Formula);

    private sealed class FamilyData
    {
        public string Name { get; set; } = "";
        public Dictionary<string, ParamInfo> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>type name → parameter name → value</summary>
        public Dictionary<string, Dictionary<string, string>> Types { get; } = new(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        var doc = uiDoc.Document;
        if (!doc.IsFamilyDocument)
            return QaqcSupport.Fail("Active document is not a family document. Open the original family (.rfa) in the Family Editor first.");

        string? path = QaqcSupport.GetString(request, "filePath");
        if (path is null) return QaqcSupport.Fail("Missing required parameter: filePath");
        if (!File.Exists(path)) return QaqcSupport.Fail($"File not found: {path}");
        if (!string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
            return QaqcSupport.Fail("filePath must be a .rfa family file.");
        if (!string.IsNullOrEmpty(doc.PathName) && string.Equals(Path.GetFullPath(doc.PathName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            return QaqcSupport.Fail("filePath is the family that is already open. Choose the other version to compare against.");

        int maxDiffs = QaqcSupport.GetInt(request, "maxValueDifferences", 500);

        Document? other = null;
        bool openedHere = false;
        try
        {
            // Reuse the file if the user already has it open, and then leave it open.
            foreach (Document d in uiDoc.Application.Application.Documents)
            {
                if (!string.IsNullOrEmpty(d.PathName)
                    && string.Equals(Path.GetFullPath(d.PathName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    other = d;
                    break;
                }
            }

            if (other is null)
            {
                other = uiDoc.Application.Application.OpenDocumentFile(path);
                openedHere = true;
            }
            if (!other.IsFamilyDocument)
                return QaqcSupport.Fail("The file opened, but it is not a family document.");

            var a = Extract(doc, string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileNameWithoutExtension(doc.PathName));
            var b = Extract(other, Path.GetFileNameWithoutExtension(path));

            var paramsOnlyInB = b.Parameters.Keys.Except(a.Parameters.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            var paramsOnlyInA = a.Parameters.Keys.Except(b.Parameters.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();

            var paramChanges = a.Parameters.Keys.Intersect(b.Parameters.Keys, StringComparer.OrdinalIgnoreCase)
                .Select(n => (A: a.Parameters[n], B: b.Parameters[n]))
                .Where(p => p.A.IsInstance != p.B.IsInstance || p.A.IsShared != p.B.IsShared || p.A.Guid != p.B.Guid || (p.A.Formula ?? "") != (p.B.Formula ?? ""))
                .Select(p => new
                {
                    Parameter = p.A.Name,
                    Active = Describe(p.A),
                    Other = Describe(p.B)
                })
                .ToList();

            var valueDiffs = new List<object>();
            foreach (var typeName in a.Types.Keys.Intersect(b.Types.Keys))
            {
                var va = a.Types[typeName];
                var vb = b.Types[typeName];
                foreach (var pName in va.Keys.Intersect(vb.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    if (a.Parameters.TryGetValue(pName, out var info) && info.IsInstance) continue;
                    if (!string.Equals(va[pName], vb[pName], StringComparison.Ordinal))
                        valueDiffs.Add(new { Type = typeName, Parameter = pName, Active = va[pName], Other = vb[pName] });
                }
            }

            var diffs = QaqcSupport.Cap(valueDiffs, maxDiffs, out bool truncated);

            return QaqcSupport.Ok(new
            {
                ActiveFamily = a.Name,
                OtherFamily = b.Name,
                OtherPath = path,
                Identical = paramsOnlyInA.Count + paramsOnlyInB.Count + paramChanges.Count + valueDiffs.Count == 0
                    && a.Types.Keys.OrderBy(k => k).SequenceEqual(b.Types.Keys.OrderBy(k => k)),
                Types = new
                {
                    OnlyInOther = b.Types.Keys.Except(a.Types.Keys).OrderBy(n => n),
                    OnlyInActive = a.Types.Keys.Except(b.Types.Keys).OrderBy(n => n),
                    InBoth = a.Types.Keys.Intersect(b.Types.Keys).Count()
                },
                Parameters = new
                {
                    OnlyInOther = paramsOnlyInB.Select(n => new { Name = n, Definition = Describe(b.Parameters[n]) }),
                    OnlyInActive = paramsOnlyInA.Select(n => new { Name = n, Definition = Describe(a.Parameters[n]) }),
                    DefinitionChanged = paramChanges
                },
                TypeValueDifferences = new { Count = valueDiffs.Count, Truncated = truncated, Rows = diffs },
                Note = "To pull changes into the active family, use the QAQC_Compare Revit Families Launchpad script."
            });
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
        finally
        {
            if (openedHere)
            {
                try { other?.Close(false); } catch { }
            }
        }
    }

    private static string Describe(ParamInfo p) =>
        $"{(p.IsInstance ? "Instance" : "Type")}, {(p.IsShared ? $"Shared {p.Guid}" : "Family")}" +
        (string.IsNullOrEmpty(p.Formula) ? "" : $", formula = {p.Formula}");

    private static FamilyData Extract(Document famDoc, string name)
    {
        var data = new FamilyData { Name = name };
        var fm = famDoc.FamilyManager;

        var userParams = new List<FamilyParameter>();
        foreach (FamilyParameter fp in fm.Parameters)
        {
            // Built-in parameters (Width on doors, etc.) are the same in both files; compare user ones.
            if (fp.Definition is InternalDefinition def && def.BuiltInParameter != BuiltInParameter.INVALID) continue;
            string pName = fp.Definition.Name;
            if (data.Parameters.ContainsKey(pName)) continue;

            data.Parameters[pName] = new ParamInfo(pName, fp.IsInstance, fp.IsShared, fp.IsShared ? fp.GUID.ToString() : null,
                string.IsNullOrEmpty(fp.Formula) ? null : fp.Formula);
            userParams.Add(fp);
        }

        foreach (FamilyType ft in fm.Types)
        {
            string typeName = ft.Name;
            if (string.IsNullOrWhiteSpace(typeName) || data.Types.ContainsKey(typeName)) continue;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fp in userParams)
            {
                try
                {
                    if (!ft.HasValue(fp)) { values[fp.Definition.Name] = ""; continue; }
                    values[fp.Definition.Name] = fp.StorageType switch
                    {
                        StorageType.String => ft.AsString(fp) ?? "",
                        StorageType.ElementId => QaqcSupport.SafeName(famDoc.GetElement(ft.AsElementId(fp))),
                        _ => ft.AsValueString(fp) ?? ""
                    };
                }
                catch { values[fp.Definition.Name] = "(unreadable)"; }
            }
            data.Types[typeName] = values;
        }

        return data;
    }
}
