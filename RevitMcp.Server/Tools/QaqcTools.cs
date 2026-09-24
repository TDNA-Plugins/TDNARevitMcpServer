using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;
using RevitMcp.Server.Bridge;

namespace RevitMcp.Server.Tools;

/// <summary>
/// MCP tools for model quality assurance / quality control. Each one is a port
/// of a TheatreDNA Launchpad QAQC script. All are read-only unless a tool's
/// fix/pin argument is set to true.
/// </summary>
[McpServerToolType]
public sealed class QaqcTools
{
    private static async Task<string> Send(RevitBridgeClient bridgeClient, string command, object? payload, CancellationToken ct)
    {
        var request = new BridgeRequest(
            Command: command,
            Payload: payload is null ? null : JsonSerializer.SerializeToElement(payload));

        var response = await bridgeClient.SendAsync(request, ct);

        if (!response.Success)
            return $"Error: {response.Error}";

        return response.Data?.GetRawText() ?? "No data returned.";
    }

    /// <summary>Diagnoses why an element is or isn't visible in a view.</summary>
    [McpServerTool(Name = "check_element_visibility"), Description(
        "Diagnose why a Revit element is or isn't visible in a view. Works for host elements and for elements " +
        "inside a linked model. Checks Revit's own visible-element set, temporary hide/isolate, Hide in View, " +
        "view-specific ownership, category and subcategory visibility, worksets, design options, phase and phase " +
        "filter, every view filter that matches, graphic overrides, geometry generated per detail level, Yes/No " +
        "visibility parameters, plan view range, crop region / far clip, and 3D section box. For linked elements " +
        "it also checks the link instance and whether the link displays By Host View, By Linked View or Custom. " +
        "Returns a Verdict, LikelyCauses (the blockers), Warnings, and every section's findings with severity " +
        "Blocker / Warning / Info / Ok. Read-only. Use get_selected_elements first to get the element id; for a " +
        "linked element pass its id inside the link plus linkInstanceId.")]
    public static Task<string> CheckElementVisibility(
        RevitBridgeClient bridgeClient,
        [Description("Element ID to diagnose. For a linked element, its ID inside the linked model.")]
        long elementId,
        [Description("Element ID of the RevitLinkInstance the element lives in. Omit for host-model elements.")]
        long? linkInstanceId = null,
        [Description("Element ID of the view to check against. Omit to use the active view.")]
        long? viewId = null,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.CheckElementVisibility, new { elementId, linkInstanceId, viewId }, cancellationToken);

    /// <summary>Finds untagged family instances in the views on sheets.</summary>
    [McpServerTool(Name = "find_untagged_elements"), Description(
        "Find instances of the given families that are NOT tagged in the views placed on sheets. For every view on " +
        "each sheet, collects the family instances visible in that view and the elements tagged there, and lists " +
        "the untagged ones with element ID, family, type, sheet and view. Also reports sheets with no views and " +
        "any family or sheet names that were not found. Read-only. Use this for tag-completeness QAQC before an " +
        "issue; use open_view with the returned ViewId to go fix one.")]
    public static Task<string> FindUntaggedElements(
        RevitBridgeClient bridgeClient,
        [Description("Exact family names to check (case-insensitive), e.g. ['TDNA_Lighting Position', 'TDNA_Rigging Point'].")]
        string[] familyNames,
        [Description("Sheet numbers to check, e.g. ['TE-101', 'TE-102']. Omit to check every sheet.")]
        string[]? sheetNumbers = null,
        [Description("Maximum untagged rows to return. Defaults to 500.")]
        int maxResults = 500,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.FindUntaggedElements, new { familyNames, sheetNumbers, maxResults }, cancellationToken);

    /// <summary>Audits font usage in annotation.</summary>
    [McpServerTool(Name = "audit_fonts"), Description(
        "Audit which fonts are used by text notes, dimensions, spot dimensions and tags, in one view or the whole " +
        "project. Returns each font with its instance count, a breakdown by category, and the types that use it. " +
        "Pass allowedFonts (the office standard) to also get every off-standard instance with its view and sheet. " +
        "Tag fonts live inside tag families and are reported as '(set in tag family)'. Read-only.")]
    public static Task<string> AuditFonts(
        RevitBridgeClient bridgeClient,
        [Description("Element ID of a single view to audit. Omit to audit the entire project.")]
        long? viewId = null,
        [Description("Office-standard font names, e.g. ['Arial', 'Arial Narrow']. Instances in any other font are listed.")]
        string[]? allowedFonts = null,
        [Description("Maximum off-standard instances to list. Defaults to 300.")]
        int maxInstances = 300,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.AuditFonts, new { viewId, allowedFonts, maxInstances }, cancellationToken);

    /// <summary>Lists note blocks and their families.</summary>
    [McpServerTool(Name = "get_note_blocks"), Description(
        "List every note block (Generic Annotation schedule) in the project with the annotation family it " +
        "schedules, its row count, and the sheets it is placed on. Use this to find which family a note block " +
        "is built on, or to spot note blocks that are empty or not placed. Read-only.")]
    public static Task<string> GetNoteBlocks(
        RevitBridgeClient bridgeClient,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.GetNoteBlocks, null, cancellationToken);

    /// <summary>Runs a model-health audit.</summary>
    [McpServerTool(Name = "audit_project_health"), Description(
        "Run a model-health audit of the active project. Returns file size and workshared status; model element " +
        "counts split into system families, loadable families (with unique family count) and in-place families " +
        "(listed by name); annotation counts (text, detail components, symbols, tags by category); views by type, " +
        "templates, and views not placed on sheets; sheets on/off the sheet list and empty sheets; design option " +
        "sets; Revit links loaded vs total, CAD linked vs imported, point clouds; model/detail groups and unused " +
        "group types; warning count. Flags lists everything past a review threshold. Read-only. Use this as the " +
        "first step of a QAQC review; it is more health-focused than analyze_model_statistics.")]
    public static Task<string> AuditProjectHealth(
        RevitBridgeClient bridgeClient,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.AuditProjectHealth, null, cancellationToken);

    /// <summary>Audits Revit and CAD links.</summary>
    [McpServerTool(Name = "audit_linked_files"), Description(
        "Audit every Revit link and CAD link/import in the project. For Revit links returns load status, stored " +
        "path, whether it is broken or a cloud (ACC/BIM 360) link, nested, attachment type, and each placed " +
        "instance with its pinned state. For CAD returns each instance, whether it is LINKED or IMPORTED, its " +
        "path, whether the file is missing, whether it is view-specific (and in which view), and pinned state. " +
        "Flags summarises broken links, imports, unpinned links and link types with no instance. Read-only; " +
        "broken links must be fixed in Manage Links.")]
    public static Task<string> AuditLinkedFiles(
        RevitBridgeClient bridgeClient,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.AuditLinkedFiles, null, cancellationToken);

    /// <summary>Finds mirrored family instances.</summary>
    [McpServerTool(Name = "find_mirrored_instances"), Description(
        "Find family instances that are mirrored (their transform is reflected), which makes labels and text in " +
        "the family read backwards and can break door/fixture handing schedules. Optionally limited to a " +
        "category, a family, or the instances visible in one view. Returns counts grouped by family/type and each " +
        "instance's ID, level, Mark, and hand/facing flip state. Read-only.")]
    public static Task<string> FindMirroredInstances(
        RevitBridgeClient bridgeClient,
        [Description("Category name to limit to, as shown in Revit, e.g. 'Doors', 'Lighting Fixtures'.")]
        string? categoryName = null,
        [Description("Family name to limit to.")]
        string? familyName = null,
        [Description("Element ID of a view; only instances visible in that view are checked. Omit for the whole project.")]
        long? viewId = null,
        [Description("Maximum instances to list. Defaults to 500.")]
        int maxResults = 500,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.FindMirroredInstances, new { categoryName, familyName, viewId, maxResults }, cancellationToken);

    /// <summary>Lists design options.</summary>
    [McpServerTool(Name = "get_design_options"), Description(
        "List all design option sets and their options, which option is primary, and how many elements each " +
        "option contains. Also lists empty options. Read-only.")]
    public static Task<string> GetDesignOptions(
        RevitBridgeClient bridgeClient,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.GetDesignOptions, null, cancellationToken);

    /// <summary>Audits type names against a naming convention.</summary>
    [McpServerTool(Name = "audit_type_names"), Description(
        "Audit the names of every element type that is actually placed in the model against a delimiter-based " +
        "naming convention. Each type name is split into fields on the delimiter; returns category, family, type, " +
        "Description parameter, the fields, and issues: wrong field count (when expectedFieldCount is given), " +
        "empty fields from doubled or trailing delimiters, stray whitespace, and missing Description. " +
        "Read-only. Useful for checking a project naming standard (e.g. HOPA codes) or building a type decoder list.")]
    public static Task<string> AuditTypeNames(
        RevitBridgeClient bridgeClient,
        [Description("Field separator in type names. Defaults to '_'.")]
        string? delimiter = null,
        [Description("Number of fields a compliant type name has. Omit to skip the field-count check.")]
        int? expectedFieldCount = null,
        [Description("Category names to limit to, e.g. ['Lighting Fixtures', 'Specialty Equipment'].")]
        string[]? categoryNames = null,
        [Description("Include annotation types (tags, symbols). Defaults to false.")]
        bool includeAnnotation = false,
        [Description("Maximum type rows to return. Defaults to 1000.")]
        int maxResults = 1000,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.AuditTypeNames,
            new { delimiter, expectedFieldCount, categoryNames, includeAnnotation, maxResults }, cancellationToken);

    /// <summary>Audits and optionally fixes sheet list visibility.</summary>
    [McpServerTool(Name = "audit_sheet_list_visibility"), Description(
        "Check every sheet's 'Appears In Sheet List' against the TheatreDNA convention: sheets whose number starts " +
        "with the hidden prefix (default 'x') should be hidden from the sheet list, all others shown. Returns the " +
        "sheets that don't match. With fix=true it also corrects them in one transaction ('MCP: Fix Sheet List " +
        "Visibility', undoable with Ctrl+Z) - only set fix=true after the user has confirmed.")]
    public static Task<string> AuditSheetListVisibility(
        RevitBridgeClient bridgeClient,
        [Description("Sheet number prefix that marks a sheet as hidden. Defaults to 'x'.")]
        string? hiddenPrefix = null,
        [Description("When true, modifies the model to fix the mismatches. Defaults to false (report only).")]
        bool fix = false,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.AuditSheetListVisibility, new { hiddenPrefix, fix }, cancellationToken);

    /// <summary>Finds and optionally pins unpinned elements.</summary>
    [McpServerTool(Name = "find_unpinned_elements"), Description(
        "Find unpinned elements in groups that should normally be pinned. Groups: Grids, Levels, ScopeBoxes, " +
        "ReferencePlanes, RevitLinks, CadLinks, TitleBlocks, Viewports (the default set), plus ModelElements and " +
        "Annotations on request. Returns the unpinned count and element IDs per group. With pin=true it pins " +
        "everything found in one transaction ('MCP: Pin Elements', undoable with Ctrl+Z) - only set pin=true " +
        "after the user has confirmed.")]
    public static Task<string> FindUnpinnedElements(
        RevitBridgeClient bridgeClient,
        [Description("Groups to check. Omit for Grids, Levels, ScopeBoxes, ReferencePlanes, RevitLinks, CadLinks, TitleBlocks, Viewports.")]
        string[]? groups = null,
        [Description("When true, modifies the model to pin every element found. Defaults to false (report only).")]
        bool pin = false,
        [Description("Maximum element IDs listed per group. Defaults to 100.")]
        int maxIdsPerGroup = 100,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.FindUnpinnedElements, new { groups, pin, maxIdsPerGroup }, cancellationToken);

    /// <summary>Saves a model snapshot.</summary>
    [McpServerTool(Name = "save_model_snapshot"), Description(
        "Save a snapshot of the active model for change auditing: sheets (title block, placed views and " +
        "schedules), views, schedules, every placed model family instance with its location and all parameter " +
        "values, annotation, and design options. Written as JSON under %APPDATA%\\TheatreDNA\\RevitMcp\\Snapshots\\" +
        "<model>. Returns the file path and counts. Does not modify the model. Take one at each issue/milestone, " +
        "then use compare_model_snapshot to see what changed since.")]
    public static Task<string> SaveModelSnapshot(
        RevitBridgeClient bridgeClient,
        [Description("Optional label added to the file name, e.g. 'SD Issue' or '50% CD'.")]
        string? label = null,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.SaveModelSnapshot, new { label }, cancellationToken);

    /// <summary>Lists saved model snapshots.</summary>
    [McpServerTool(Name = "list_model_snapshots"), Description(
        "List the snapshots saved for the active model, newest first, with path, save time and size. Use the " +
        "path with compare_model_snapshot to compare against a specific milestone rather than the latest. Read-only.")]
    public static Task<string> ListModelSnapshots(
        RevitBridgeClient bridgeClient,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.ListModelSnapshots, null, cancellationToken);

    /// <summary>Compares a snapshot to the current model.</summary>
    [McpServerTool(Name = "compare_model_snapshot"), Description(
        "Compare a saved snapshot against the model as it is now. Reports sheets added/deleted/renumbered, title " +
        "block changes, views and schedules added to or removed from sheets; views and schedules added or deleted; " +
        "model family instances added, deleted, moved (beyond a threshold), changed type, or with changed parameter " +
        "values; annotation added or deleted; design options added or deleted. Returns a Summary of counts per " +
        "change type and capped detail rows per section. Read-only. Defaults to the most recent snapshot.")]
    public static Task<string> CompareModelSnapshot(
        RevitBridgeClient bridgeClient,
        [Description("Full path of the baseline snapshot JSON. Omit to use the most recent snapshot of this model.")]
        string? snapshotPath = null,
        [Description("Minimum movement in decimal feet to report an element as moved. Defaults to 1.0.")]
        double moveThresholdFeet = 1.0,
        [Description("Parameter names to leave out of the diff, e.g. noisy computed values.")]
        string[]? ignoreParameters = null,
        [Description("Maximum detail rows returned per section. Defaults to 200.")]
        int maxRowsPerSection = 200,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.CompareModelSnapshot,
            new { snapshotPath, moveThresholdFeet, ignoreParameters, maxRowsPerSection }, cancellationToken);

    /// <summary>Compares the open family to another .rfa.</summary>
    [McpServerTool(Name = "compare_family_to_file"), Description(
        "Compare the family open in the Family Editor against another version of it on disk (.rfa). Reports " +
        "types only in one file, parameters only in one file, parameters whose definition changed (type vs " +
        "instance, shared GUID, formula), and type parameter values that differ per type. Requires the active " +
        "document to be a family (check with is_family_document). The other file is opened in the background and " +
        "closed without saving. Read-only.")]
    public static Task<string> CompareFamilyToFile(
        RevitBridgeClient bridgeClient,
        [Description("Full path of the .rfa to compare against, e.g. 'C:\\\\Families\\\\TDNA_Seat_v2.rfa'.")]
        string filePath,
        [Description("Maximum type value differences listed. Defaults to 500.")]
        int maxValueDifferences = 500,
        CancellationToken cancellationToken = default) =>
        Send(bridgeClient, CommandNames.CompareFamilyToFile, new { filePath, maxValueDifferences }, cancellationToken);
}
