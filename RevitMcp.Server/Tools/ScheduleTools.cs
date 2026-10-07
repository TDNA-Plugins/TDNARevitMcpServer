using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;
using RevitMcp.Server.Bridge;

namespace RevitMcp.Server.Tools;

/// <summary>
/// MCP tools for checking Revit schedules.
/// </summary>
[McpServerToolType]
public sealed class ScheduleTools
{
    /// <summary>
    /// Finds elements in a schedule that have blank values in the schedule's parameter fields.
    /// </summary>
    [McpServerTool(Name = "audit_schedule_blanks"), Description(
        "Read-only QA check: finds every element included in a Revit schedule that has a blank value in any of " +
        "the schedule's visible parameter fields. Uses the schedule's own filters and field list, so it checks " +
        "exactly what the schedule shows. A value is blank when the parameter is missing on the element/type, " +
        "has no value, is an empty string, or is an unset element reference ('None'); numbers and Yes/No only " +
        "count as blank when never set. Calculated, Count, hidden, and Room/Material/Project Info fields are skipped " +
        "and listed under SkippedFields. Returns the schedule name, ElementsChecked, ElementsWithBlanks, " +
        "BlanksPerField (blank count per column, highest first), and Elements (Id, Category, Family, Type, " +
        "BlankFields), capped by maxElements with Truncated=true when more exist. Never modifies the model.")]
    public static async Task<string> AuditScheduleBlanks(
        RevitBridgeClient bridgeClient,
        [Description("Schedule name, exact or a unique partial match (case-insensitive), e.g. '00-Everything Schedule'.")]
        string scheduleName,
        [Description("Only check these fields, by column heading or parameter name. Omit to check all visible fields.")]
        string[]? fieldNames = null,
        [Description("Also check fields that are hidden in the schedule. Defaults to false.")]
        bool includeHiddenFields = false,
        [Description("Maximum element rows to return. Totals and per-field counts are always complete. Defaults to 200.")]
        int maxElements = 200,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            scheduleName,
            fieldNames,
            includeHiddenFields,
            maxElements
        });

        var request = new BridgeRequest(
            Command: CommandNames.AuditScheduleBlanks,
            Payload: payload);

        var response = await bridgeClient.SendAsync(request, cancellationToken);

        if (!response.Success)
            return $"Error: {response.Error}";

        return response.Data?.GetRawText() ?? "No data returned.";
    }
}
