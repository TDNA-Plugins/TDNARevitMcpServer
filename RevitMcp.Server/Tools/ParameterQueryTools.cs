using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;
using RevitMcp.Server.Bridge;

namespace RevitMcp.Server.Tools;

/// <summary>
/// MCP tool for reading parameter values from many elements at once.
/// </summary>
[McpServerToolType]
public sealed class ParameterQueryTools
{
    /// <summary>Reads parameter values from many elements across all design options.</summary>
    [McpServerTool(Name = "get_parameter_values"), Description(
        "Read-only: reads one or more parameter values from many elements at once, across every design option " +
        "(primary and secondary) and phase in the model. Filter by category names, element IDs, design option " +
        "('all', 'main', 'primary' = Main Model plus primary options, 'secondary', or text matched against " +
        "'Option Set : Option'), and family/type name text. For each parameter the instance is checked first, then " +
        "the type; every parameter with that name is checked and a filled value wins over an empty one, so same-named " +
        "duplicate parameters are handled (counted in ElementsWithSameNamedDuplicates). Returns Summary (Filled/Blank/Missing count per parameter) " +
        "and Elements (Id, Category, Family, Type, OptionSet, Option, IsPrimary, PhaseCreated, and Values with " +
        "Value, Source = Instance/Type/Missing, IsBlank). Rows are capped by maxElements with Truncated=true when " +
        "more match. Use this to audit or compare parameter values; it never modifies the model.")]
    public static async Task<string> GetParameterValues(
        RevitBridgeClient bridgeClient,
        [Description("Parameter names exactly as shown in Revit properties, e.g. ['POMI Code', 'MasterFormat Code'].")]
        string[] parameterNames,
        [Description("Category names to include, e.g. ['Furniture', 'Specialty Equipment']. Omit for all model categories.")]
        string[]? categories = null,
        [Description("Only these element IDs. Overrides categories.")]
        long[]? elementIds = null,
        [Description("'all' (default), 'main', 'primary', 'secondary', or text matched against 'Option Set : Option'.")]
        string designOption = "all",
        [Description("Only elements whose family or type name contains this text (case-insensitive).")]
        string? familyContains = null,
        [Description("Maximum element rows to return. Summary counts are always complete. Defaults to 2000.")]
        int maxElements = 2000,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            parameterNames,
            categories,
            elementIds,
            designOption,
            familyContains,
            maxElements
        });

        var response = await bridgeClient.SendAsync(
            new BridgeRequest(Command: CommandNames.GetParameterValues, Payload: payload),
            cancellationToken);

        if (!response.Success)
            return $"Error: {response.Error}";

        return response.Data?.GetRawText() ?? "No data returned.";
    }
}
