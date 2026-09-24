using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMcp.Server.Prompts;

/// <summary>
/// MCP prompts that chain the QAQC tools into repeatable review workflows.
/// Clients show these as slash commands (e.g. /mcp__revit-mcp__qaqc_model_review).
/// </summary>
[McpServerPromptType]
public sealed class QaqcPrompts
{
    /// <summary>Full model-health QAQC review.</summary>
    [McpServerPrompt(Name = "qaqc_model_review"), Description(
        "Run a full QAQC review of the open Revit model and write up the findings.")]
    public static string QaqcModelReview(
        [Description("Optional area to emphasise, e.g. 'links', 'annotation', 'sheets'.")]
        string? focus = null)
    {
        return
            "Run a QAQC review of the Revit model that is open right now, using the revit-mcp tools. " +
            "Everything in this review is read-only: do NOT pass fix=true or pin=true to any tool unless I " +
            "explicitly ask afterwards.\n\n" +
            "1. audit_project_health - model composition, in-place families, views, sheets, links, groups, warnings.\n" +
            "2. get_warnings - group the warnings by description and count them; list the top offenders.\n" +
            "3. audit_linked_files - broken, cloud, imported (not linked) and unpinned links.\n" +
            "4. find_unpinned_elements with the default groups (report only).\n" +
            "5. audit_sheet_list_visibility (report only).\n" +
            "6. find_mirrored_instances for the whole project.\n" +
            "7. audit_fonts for the whole project. If you can see an obvious office standard (the font used by the " +
            "large majority), call it out and list the off-standard fonts.\n" +
            "8. audit_type_names with the default '_' delimiter; summarise the issue counts rather than every row.\n" +
            "9. get_design_options - note empty options.\n\n" +
            (string.IsNullOrWhiteSpace(focus) ? "" : $"Give extra attention to: {focus}.\n\n") +
            "Then write the report: a one-paragraph overall verdict, then a table of issues with columns " +
            "Severity (High / Medium / Low), Area, Finding, Count, Suggested fix. Put anything that would be " +
            "embarrassing on an issued set (broken links, untagged or mirrored elements on sheets, sheets on the " +
            "wrong list, off-standard fonts) at High. Finish by listing which fixes you can apply for me " +
            "(sheet list visibility, pinning) and ask before applying any of them.";
    }

    /// <summary>Checks a sheet set before issue.</summary>
    [McpServerPrompt(Name = "qaqc_pre_issue_check"), Description(
        "Check a set of sheets before issue: tags, fonts, sheet list, and what changed since the last snapshot.")]
    public static string QaqcPreIssueCheck(
        [Description("Comma-separated sheet numbers to check. Leave blank for every sheet.")]
        string? sheetNumbers = null,
        [Description("Comma-separated family names that must be tagged on those sheets.")]
        string? familiesThatMustBeTagged = null,
        [Description("Label for the snapshot taken at the end, e.g. '50% CD'.")]
        string? issueLabel = null)
    {
        string sheets = string.IsNullOrWhiteSpace(sheetNumbers) ? "every sheet" : $"sheets {sheetNumbers}";
        return
            $"Run a pre-issue QAQC check of {sheets} in the open Revit model, using the revit-mcp tools. " +
            "Do not change the model unless I say so.\n\n" +
            "1. list_model_snapshots, then compare_model_snapshot against the most recent one (if any) and " +
            "summarise what changed since the last issue: sheets, views on sheets, and model families added, " +
            "deleted, moved or re-typed. Skip parameter-only changes unless there are fewer than 50.\n" +
            (string.IsNullOrWhiteSpace(familiesThatMustBeTagged)
                ? "2. Ask me which families must be tagged on these sheets, then run find_untagged_elements.\n"
                : $"2. find_untagged_elements for families [{familiesThatMustBeTagged}] on {sheets}.\n") +
            "3. audit_fonts for each view on those sheets that has annotation (use get_views_on_sheet to find them) - " +
            "or once for the whole project if there are more than 20 views - and flag off-standard fonts.\n" +
            "4. audit_sheet_list_visibility (report only).\n" +
            "5. find_mirrored_instances limited to the views on those sheets.\n\n" +
            "Report the results per sheet as a checklist (pass / fail with counts). For every failure give the " +
            "element or view IDs so I can jump to them. " +
            (string.IsNullOrWhiteSpace(issueLabel)
                ? "Finally, ask whether to save a snapshot for this issue with save_model_snapshot."
                : $"Finally, once I confirm the set is ready, save a snapshot with save_model_snapshot label '{issueLabel}'.");
    }
}
