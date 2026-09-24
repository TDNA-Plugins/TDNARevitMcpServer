using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core.Commands;
using RevitMcp.Core.Messages;

namespace RevitMcp.Core.Handlers.Qaqc;

/// <summary>
/// Handles the <see cref="CommandNames.CheckElementVisibility"/> command.
/// Diagnoses why an element (host or linked) is or isn't visible in a view.
/// Ported from the Check_ElementsVisibility Launchpad script, which is also
/// the Check Visibility tool in the TheatreDNA QAQC Toolkit.
/// </summary>
/// <remarks>
/// Expected payload properties:
/// <list type="bullet">
///   <item><c>elementId</c> (long, required) – The element to diagnose. For a linked element, its id inside the link.</item>
///   <item><c>linkInstanceId</c> (long, optional) – The RevitLinkInstance the element lives in.</item>
///   <item><c>viewId</c> (long, optional) – The view to check against. Defaults to the active view.</item>
/// </list>
/// Read-only; opens no transaction.
/// </remarks>
public sealed class CheckElementVisibilityHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Command => CommandNames.CheckElementVisibility;

    /// <inheritdoc />
    public BridgeResponse Handle(BridgeRequest request, UIDocument uiDoc)
    {
        try
        {
            var doc = uiDoc.Document;

            var elementId = QaqcSupport.GetElementId(request, "elementId");
            if (elementId is null)
                return QaqcSupport.Fail("Missing required parameter: elementId");

            var view = QaqcSupport.ResolveView(doc, uiDoc.ActiveView, QaqcSupport.GetElementId(request, "viewId"));
            if (view is null)
                return QaqcSupport.Fail("View not found.");
            if (view.IsTemplate || view is TableView || view is ViewSheet)
                return QaqcSupport.Fail($"'{view.Name}' is a {(view.IsTemplate ? "view template" : view.ViewType.ToString())}. " +
                                        "Check visibility against a plan, section, elevation, 3D, drafting or legend view.");

            RevitLinkInstance? link = null;
            Element? element;
            var linkId = QaqcSupport.GetElementId(request, "linkInstanceId");
            if (linkId is not null)
            {
                link = doc.GetElement(linkId) as RevitLinkInstance;
                if (link is null)
                    return QaqcSupport.Fail($"No Revit link instance with id {linkId.Value}.");
                var linkDoc = link.GetLinkDocument();
                if (linkDoc is null)
                    return QaqcSupport.Fail($"Link '{link.Name}' is not loaded.");
                element = linkDoc.GetElement(elementId);
            }
            else
            {
                element = doc.GetElement(elementId);
            }

            if (element is null)
                return QaqcSupport.Fail($"Element {elementId.Value} not found{(link is null ? "" : " in the linked model")}.");

            return QaqcSupport.Ok(VisibilityAnalyzer.Analyze(doc, view, element, link).ToResult());
        }
        catch (Exception ex)
        {
            return QaqcSupport.Fail(ex.Message);
        }
    }
}

/// <summary>Severity of one visibility finding.</summary>
internal enum VisibilitySeverity
{
    /// <summary>Hides the element outright. Reported as a likely cause.</summary>
    Blocker,
    /// <summary>May hide or obscure the element.</summary>
    Warning,
    /// <summary>Context for the other findings.</summary>
    Info,
    /// <summary>Checked and not a problem.</summary>
    Ok
}

internal sealed class VisibilityFinding(VisibilitySeverity severity, string text)
{
    public VisibilitySeverity Severity { get; } = severity;
    public string Text { get; } = text;
    public List<string> Notes { get; } = new();

    public VisibilityFinding Note(string note)
    {
        Notes.Add(note);
        return this;
    }
}

internal sealed class VisibilitySection(string title)
{
    public string Title { get; } = title;
    public List<VisibilityFinding> Findings { get; } = new();

    public VisibilityFinding Blocker(string text) => Add(VisibilitySeverity.Blocker, text);
    public VisibilityFinding Warn(string text) => Add(VisibilitySeverity.Warning, text);
    public VisibilityFinding Info(string text) => Add(VisibilitySeverity.Info, text);
    public VisibilityFinding Ok(string text) => Add(VisibilitySeverity.Ok, text);

    private VisibilityFinding Add(VisibilitySeverity severity, string text)
    {
        var f = new VisibilityFinding(severity, text);
        Findings.Add(f);
        return f;
    }
}

internal sealed class VisibilityReport
{
    public Dictionary<string, object?> Header { get; } = new();
    public List<VisibilitySection> Sections { get; } = new();

    public VisibilitySection AddSection(string title)
    {
        var s = new VisibilitySection(title);
        Sections.Add(s);
        return s;
    }

    private List<string> TextsOf(VisibilitySeverity severity) =>
        Sections.SelectMany(s => s.Findings).Where(f => f.Severity == severity).Select(f => f.Text).ToList();

    public object ToResult()
    {
        var blockers = TextsOf(VisibilitySeverity.Blocker);
        var warnings = TextsOf(VisibilitySeverity.Warning);

        return new
        {
            Element = Header,
            Verdict = blockers.Count > 0
                ? $"{blockers.Count} likely cause(s) found"
                : warnings.Count > 0 ? "No hard blockers found; review the warnings" : "No visibility blockers found",
            LikelyCauses = blockers,
            Warnings = warnings,
            Sections = Sections.Select(s => new
            {
                s.Title,
                Findings = s.Findings.Select(f => new
                {
                    Severity = f.Severity.ToString(),
                    f.Text,
                    Notes = f.Notes.Count > 0 ? f.Notes : null
                })
            })
        };
    }
}

/// <summary>
/// The checks themselves. Same-document checks are exact; checks of a linked
/// element against host view settings are approximations and say so.
/// </summary>
internal static class VisibilityAnalyzer
{
    public static VisibilityReport Analyze(Document doc, View view, Element element, RevitLinkInstance? linkInstance)
    {
        bool isLinked = linkInstance is not null;
        Document elemDoc = element.Document;
        Transform linkTransform = linkInstance?.GetTotalTransform() ?? Transform.Identity;
        View checkView = view;                     // view whose V/G settings govern the element
        ElementId hostSideId = linkInstance?.Id ?? element.Id;

        var report = new VisibilityReport();
        report.Header["ElementId"] = element.Id.Value;
        report.Header["Name"] = QaqcSupport.SafeName(element);
        report.Header["Category"] = element.Category?.Name;
        report.Header["ViewId"] = view.Id.Value;
        report.Header["View"] = $"{view.ViewType} | {view.Name}";
        if (linkInstance is not null)
        {
            report.Header["LinkInstanceId"] = linkInstance.Id.Value;
            report.Header["Link"] = linkInstance.Name;
        }

        // ---------- View Template ----------
        var setup = report.AddSection("View Setup");
        if (view.ViewTemplateId != ElementId.InvalidElementId)
            setup.Info($"View template '{QaqcSupport.SafeName(doc.GetElement(view.ViewTemplateId))}' is applied. Fix V/G, filters, and view range in the TEMPLATE, not the view.");
        else
            setup.Ok("No view template. V/G settings are set on the view itself.");

        // ---------- Link Instance ----------
        if (linkInstance is not null)
        {
            var link = report.AddSection("Link Instance");

            try
            {
                if (linkInstance.IsHidden(view)) link.Blocker("Link instance is hidden in this view (Hide in View > Elements).");
                else link.Ok("Link instance is not hidden.");
            }
            catch { link.Warn("Could not check link instance hide state."); }

            try
            {
                if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_RvtLinks))) link.Blocker("'RVT Links' category is hidden in this view.");
                else link.Ok("'RVT Links' category is visible.");
            }
            catch { link.Warn("Could not check RVT Links category visibility."); }

            try
            {
                if (doc.IsWorkshared)
                {
                    if (view.GetWorksetVisibility(linkInstance.WorksetId) == WorksetVisibility.Hidden) link.Blocker("Link instance's host workset is hidden.");
                    else link.Ok("Link instance's host workset is visible.");
                }
            }
            catch { link.Warn("Could not check link instance workset."); }

            try
            {
                var linkOvr = view.GetElementOverrides(linkInstance.Id);
                if (linkOvr.Halftone) link.Warn("Link instance is halftoned.");
                if (linkOvr.Transparency > 0) link.Warn($"Link instance transparency: {linkOvr.Transparency}%");
            }
            catch { }

            try
            {
                var linkSettings = view.GetLinkOverrides(linkInstance.Id);
                var linkMode = linkSettings?.LinkVisibilityType ?? LinkVisibility.ByHostView;

                if (linkMode == LinkVisibility.ByLinkView)
                {
                    if (linkSettings is not null && elemDoc.GetElement(linkSettings.LinkedViewId) is View linkedView)
                    {
                        checkView = linkedView;
                        link.Info($"Display: By Linked View → '{linkedView.Name}'.")
                            .Note("V/G checks below use the LINKED view's settings; host filters/overrides do not apply.");
                    }
                    else
                    {
                        link.Warn("Display: By Linked View, but the linked view could not be found.");
                    }
                }
                else if (linkMode == LinkVisibility.Custom)
                {
                    link.Info("Display: Custom. Category, filter, workset, and phase settings may be customized per link in V/G > Revit Links (not readable via API).")
                        .Note("Results below use host view settings.");
                }
                else
                {
                    link.Info("Display: By Host View. Host categories, filters, and overrides apply.");
                }
            }
            catch { link.Warn("Could not read link display settings. Assuming By Host View."); }
        }

        bool sameDoc = checkView.Document.Equals(elemDoc);

        CheckViewCollector(report, doc, view, element, linkInstance);
        CheckHiding(report, view, checkView, element, elemDoc, hostSideId, sameDoc);
        CheckPhase(report, checkView, element, elemDoc, sameDoc);
        CheckFilters(report, checkView, element, sameDoc);
        CheckGraphics(report, view, checkView, element, elemDoc, sameDoc);
        CheckSpatial(report, doc, view, element, linkTransform);

        return report;
    }

    private static void CheckViewCollector(VisibilityReport report, Document doc, View view, Element element, RevitLinkInstance? linkInstance)
    {
        var section = report.AddSection("Revit View Collector");
        try
        {
            ElementId catId = element.Category?.Id ?? ElementId.InvalidElementId;
            var collector = linkInstance is not null
                ? new FilteredElementCollector(doc, view.Id, linkInstance.Id)
                : new FilteredElementCollector(doc, view.Id);

            if (catId != ElementId.InvalidElementId)
                collector = collector.OfCategoryId(catId);

            if (collector.ToElementIds().Contains(element.Id))
                section.Ok("Revit includes this element in the view's visible set. If you still can't see it, look at graphics (overrides, halftone, detail level, white lines) or the view range/crop sections.");
            else
                section.Blocker("Revit EXCLUDES this element from the view's visible set. The other checks narrow down why.");
        }
        catch (Exception ex)
        {
            section.Warn($"Could not run view collector: {ex.Message}");
        }
    }

    private static void CheckHiding(VisibilityReport report, View view, View checkView, Element element,
        Document elemDoc, ElementId hostSideId, bool sameDoc)
    {
        var section = report.AddSection("Hiding");

        try
        {
            if (view.IsTemporaryHideIsolateActive())
            {
                if (view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, hostSideId))
                    section.Info("Temporary Hide/Isolate is active, but this element is not affected.");
                else
                    section.Blocker("Temporary Hide/Isolate is active and hides this element (reset via the sunglasses icon).");
            }
            else
            {
                section.Ok("Temporary Hide/Isolate is not active.");
            }
        }
        catch { section.Warn("Could not check Temporary Hide/Isolate."); }

        if (sameDoc && element.ViewSpecific && element.OwnerViewId != checkView.Id)
        {
            var ownerName = QaqcSupport.SafeName(elemDoc.GetElement(element.OwnerViewId));
            section.Blocker($"Element is view-specific and belongs to view '{ownerName}' (id {element.OwnerViewId.Value}). It can only ever appear there.");
        }

        if (sameDoc)
        {
            try
            {
                if (element.IsHidden(checkView))
                    section.Blocker("Element is hidden in view (Hide in View > Elements). Use Reveal Hidden Elements > Unhide Element.");
                else
                    section.Ok("Element is not hidden individually.");
            }
            catch { }
        }

        if (element.Category is not null)
        {
            try
            {
                if (checkView.GetCategoryHidden(element.Category.Id))
                    section.Blocker($"Category '{element.Category.Name}' is hidden in this view.");
                else
                    section.Ok("Category is visible.");
            }
            catch { section.Warn("Could not check category visibility."); }
        }

        try
        {
            if (elemDoc.IsWorkshared)
            {
                WorksetId wsId = element.WorksetId;
                Workset ws = elemDoc.GetWorksetTable().GetWorkset(wsId);

                if (sameDoc)
                {
                    if (checkView.GetWorksetVisibility(wsId) == WorksetVisibility.Hidden)
                        section.Blocker($"Element's workset '{ws.Name}' is hidden in this view.");
                    else
                        section.Ok($"Workset '{ws.Name}' is visible.");
                }
                else
                {
                    var f = ws.IsVisibleByDefault
                        ? section.Ok($"Linked workset '{ws.Name}' is visible by default.")
                        : section.Warn($"Linked workset '{ws.Name}' is NOT visible by default in its model.");
                    f.Note("Per-link workset visibility (V/G > Revit Links > Custom) is not readable via API.");
                }
            }
        }
        catch { section.Warn("Could not determine workset visibility."); }

        try
        {
            DesignOption? dopt = element.DesignOption;
            if (dopt is not null)
            {
                if (sameDoc)
                {
                    ElementId viewOptId = checkView.get_Parameter(BuiltInParameter.VIEWER_OPTION_VISIBILITY)?.AsElementId()
                        ?? ElementId.InvalidElementId;

                    if (viewOptId != ElementId.InvalidElementId && viewOptId != dopt.Id)
                        section.Blocker($"Element is in design option '{dopt.Name}', which this view doesn't show.");
                    else if (viewOptId == ElementId.InvalidElementId && !dopt.IsPrimary)
                        section.Blocker($"Element is in secondary option '{dopt.Name}'; this view shows primary options only.");
                    else
                        section.Ok("Design option visible in this view.");
                }
                else if (dopt.IsPrimary)
                {
                    section.Ok($"Element is in primary option '{dopt.Name}' (shown by default in links).");
                }
                else
                {
                    section.Blocker($"Element is in secondary option '{dopt.Name}'. Links show primary options unless set in V/G > Revit Links > Custom.");
                }
            }
        }
        catch { section.Warn("Could not check design option visibility."); }
    }

    private static void CheckPhase(VisibilityReport report, View checkView, Element element, Document elemDoc, bool sameDoc)
    {
        var section = report.AddSection("Phase");
        try
        {
            Document viewDoc = checkView.Document;
            ElementId viewPhaseId = checkView.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId;
            ElementId phaseFilterId = checkView.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId;
            var viewPhase = viewDoc.GetElement(viewPhaseId) as Phase;
            var phaseFilter = viewDoc.GetElement(phaseFilterId) as PhaseFilter;

            // For linked elements, find the link phase with the same name as the host view phase
            ElementId phaseForElement = viewPhaseId;
            if (!sameDoc && viewPhase is not null)
            {
                phaseForElement = ElementId.InvalidElementId;
                foreach (Phase p in elemDoc.Phases)
                {
                    if (p.Name.Equals(viewPhase.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        phaseForElement = p.Id;
                        break;
                    }
                }
                if (phaseForElement == ElementId.InvalidElementId)
                    section.Warn($"Linked model has no phase named '{viewPhase.Name}'. Check the link's Phase Mapping.");
            }

            string createdName = QaqcSupport.SafeName(elemDoc.GetElement(element.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() ?? ElementId.InvalidElementId));
            string demoName = QaqcSupport.SafeName(elemDoc.GetElement(element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsElementId() ?? ElementId.InvalidElementId));
            section.Info($"View phase: '{viewPhase?.Name ?? "—"}' | Phase filter: '{phaseFilter?.Name ?? "None"}'");
            section.Info($"Element created: '{(createdName == "" ? "—" : createdName)}' | demolished: '{(demoName == "" ? "None" : demoName)}'");

            if (phaseForElement != ElementId.InvalidElementId && element.HasPhases())
            {
                ElementOnPhaseStatus status = element.GetPhaseStatus(phaseForElement);
                VisibilityFinding f;

                if (status == ElementOnPhaseStatus.Future)
                    f = section.Blocker("Element is created in a LATER phase than this view. It doesn't exist yet in this view's phase.");
                else if (status == ElementOnPhaseStatus.Past)
                    f = section.Blocker("Element was demolished in an EARLIER phase. It no longer exists in this view's phase.");
                else if (phaseFilter is not null && status != ElementOnPhaseStatus.None)
                {
                    var pres = phaseFilter.GetPhaseStatusPresentation(status);
                    f = pres == PhaseStatusPresentation.DontShow
                        ? section.Blocker($"Element status is '{status}' and phase filter '{phaseFilter.Name}' is set to NOT SHOW '{status}'.")
                        : section.Ok($"Element status is '{status}'; phase filter shows it ({pres}).");
                }
                else
                    f = section.Ok($"Element phase status: {status}.");

                if (!sameDoc)
                    f.Note("Linked phase matched by name. Verify Manage Links > Phase Mapping if results look wrong.");
            }
        }
        catch (Exception ex)
        {
            section.Warn($"Could not check phasing: {ex.Message}");
        }
    }

    private static void CheckFilters(VisibilityReport report, View checkView, Element element, bool sameDoc)
    {
        var section = report.AddSection("View Filters");
        bool filterMatch = false;
        bool paramRuleCaveat = false;
        try
        {
            Document viewDoc = checkView.Document;
            ElementId elemCatId = element.Category?.Id ?? ElementId.InvalidElementId;

            foreach (ElementId filterId in checkView.GetFilters())
            {
                Element? rawFilter = viewDoc.GetElement(filterId);
                if (rawFilter is null) continue;

                string filterName = rawFilter.Name;
                bool matches = false;

                if (rawFilter is ParameterFilterElement paramFilter)
                {
                    bool categoryTargeted = elemCatId != ElementId.InvalidElementId
                        && paramFilter.GetCategories().Contains(elemCatId);

                    if (categoryTargeted)
                    {
                        ElementFilter? internalFilter = null;
                        try { internalFilter = paramFilter.GetElementFilter(); } catch { }

                        if (internalFilter is null)
                        {
                            // Category-only filter (no rules) matches every element of its categories
                            matches = true;
                        }
                        else
                        {
                            try { matches = internalFilter.PassesFilter(element); } catch { }
                            if (!sameDoc) paramRuleCaveat = true;
                        }
                    }
                }
                else if (rawFilter is SelectionFilterElement selFilter && sameDoc)
                {
                    matches = selFilter.GetElementIds().Contains(element.Id);
                }

                if (!matches) continue;
                filterMatch = true;

                bool isEnabled = true;
                try { isEnabled = checkView.GetIsFilterEnabled(filterId); } catch { }
                bool isVisible = checkView.GetFilterVisibility(filterId);
                OverrideGraphicSettings o = checkView.GetFilterOverrides(filterId);

                bool hasOverrides = o.Halftone
                    || o.Transparency > 0
                    || o.ProjectionLineColor.IsValid
                    || o.ProjectionLinePatternId != ElementId.InvalidElementId
                    || o.CutLineColor.IsValid
                    || o.SurfaceBackgroundPatternColor.IsValid
                    || o.SurfaceForegroundPatternColor.IsValid;

                if (!isEnabled)
                    section.Info($"Filter '{filterName}' matches but 'Enable Filter' is unchecked. No effect.");
                else if (!isVisible)
                    section.Blocker($"Filter '{filterName}' matches this element and its 'Visibility' box is UNCHECKED. This hides the element.");
                else if (!hasOverrides)
                    section.Info($"Filter '{filterName}' matches this element but has NO graphic overrides set.");
                else
                {
                    var f = section.Warn($"Filter '{filterName}' is ACTIVE and affects this element.");
                    if (o.Halftone) f.Note("Applies halftone.");
                    if (o.Transparency > 0) f.Note($"Applies transparency {o.Transparency}%.");
                    if (o.ProjectionLineColor.IsValid) f.Note($"Overrides projection line color: {Rgb(o.ProjectionLineColor)}.");
                    if (o.ProjectionLinePatternId != ElementId.InvalidElementId) f.Note("Overrides projection line pattern.");
                    if (o.CutLineColor.IsValid) f.Note($"Overrides cut line color: {Rgb(o.CutLineColor)}.");
                    if (o.SurfaceBackgroundPatternColor.IsValid) f.Note("Overrides surface background pattern color.");
                    if (o.SurfaceForegroundPatternColor.IsValid) f.Note("Overrides surface foreground pattern color.");
                }
            }
        }
        catch (Exception ex)
        {
            section.Warn($"Could not analyze view filters: {ex.Message}");
        }

        if (!filterMatch)
            section.Ok("No view filters match this element.");

        if (paramRuleCaveat)
            section.Info("Rule-based filters were evaluated against a linked element. Rules on shared/project parameters may not evaluate reliably across models.");
    }

    private static void CheckGraphics(VisibilityReport report, View view, View checkView, Element element,
        Document elemDoc, bool sameDoc)
    {
        var section = report.AddSection("Graphics");

        try
        {
            if (sameDoc)
            {
                var o = checkView.GetElementOverrides(element.Id);
                if (o.Halftone) section.Warn("Element is halftoned.");
                if (o.Transparency > 0) section.Warn($"Transparency applied: {o.Transparency}%");
                if (o.ProjectionLineColor.IsValid) section.Warn($"Line color override: {Rgb(o.ProjectionLineColor)}");
            }

            if (element.Category is not null && checkView.GetCategoryOverrides(element.Category.Id).Halftone)
                section.Warn("Category-level halftone applied.");

            // Hidden or halftoned subcategories can remove all of a family's visible geometry
            Category? parentCat = element.Category;
            if (parentCat?.SubCategories is not null)
            {
                Category? viewParentCat = sameDoc ? parentCat : Category.GetCategory(checkView.Document, parentCat.Id);

                foreach (Category subCat in parentCat.SubCategories)
                {
                    try
                    {
                        ElementId subCatId = sameDoc ? subCat.Id : FindSubCategoryId(viewParentCat, subCat.Name);
                        if (subCatId == ElementId.InvalidElementId) continue;

                        if (checkView.GetCategoryHidden(subCatId))
                            section.Warn($"Subcategory '{subCat.Name}' is HIDDEN. Geometry on it won't display.");
                        if (checkView.GetCategoryOverrides(subCatId).Halftone)
                            section.Warn($"Subcategory '{subCat.Name}' has halftone override.");
                    }
                    catch { }
                }
            }
        }
        catch { section.Warn("Could not retrieve graphic overrides."); }

        try
        {
            int fine = CountGeometry(element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
            int medium = CountGeometry(element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Medium }));
            int coarse = CountGeometry(element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Coarse }));
            int total = fine + medium + coarse;
            section.Info($"Geometry objects by detail level: Coarse {coarse} | Medium {medium} | Fine {fine}. View is {view.DetailLevel}.");

            if (sameDoc)
            {
                int inView = CountGeometry(element.get_Geometry(new Options { View = checkView }));
                if (inView == 0 && total > 0)
                    section.Blocker("Element has geometry, but NONE is generated for this view. Check the family's Visibility Settings (plan/RCP/front-back/left-right, detail levels) and any visibility parameters.");
                else if (inView > 0)
                    section.Ok($"{inView} geometry object(s) generated for this view.");
            }
            else
            {
                int atViewLevel = view.DetailLevel == ViewDetailLevel.Coarse ? coarse
                    : view.DetailLevel == ViewDetailLevel.Medium ? medium : fine;
                if (atViewLevel == 0 && total > 0)
                    section.Blocker($"Element has no geometry at this view's detail level ({view.DetailLevel}).");
            }

            if (total == 0)
                section.Warn("Element has no model geometry at any detail level (empty family, or all geometry controlled off).");
        }
        catch (Exception ex)
        {
            section.Warn($"Could not evaluate geometry: {ex.Message}");
        }

        try
        {
            var toCheck = new List<Parameter>();
            foreach (Parameter p in element.Parameters) toCheck.Add(p);
            if (elemDoc.GetElement(element.GetTypeId()) is Element typeElem)
                foreach (Parameter p in typeElem.Parameters) toCheck.Add(p);

            foreach (var p in toCheck)
            {
                if (p?.Definition is null || !p.HasValue) continue;
                if (p.Definition.GetDataType() != SpecTypeId.Boolean.YesNo) continue;

                string name = p.Definition.Name;
                string lower = name.ToLowerInvariant();
                if ((lower.Contains("visib") || lower.Contains("show") || lower.Contains("display")) && p.AsInteger() == 0)
                    section.Warn($"Yes/No parameter '{name}' is OFF. It may control geometry visibility in the family.");
            }
        }
        catch { }
    }

    private static void CheckSpatial(VisibilityReport report, Document doc, View view, Element element, Transform linkTransform)
    {
        var section = report.AddSection("Spatial");

        var corners = GetHostCorners(element, linkTransform);
        if (corners is null)
        {
            section.Warn("Element has no bounding box. Spatial checks skipped.");
            return;
        }

        double elemMinZ = corners.Min(c => c.Z);
        double elemMaxZ = corners.Max(c => c.Z);

        try
        {
            if (view is ViewPlan vp && (vp.ViewType == ViewType.FloorPlan || vp.ViewType == ViewType.EngineeringPlan || vp.ViewType == ViewType.AreaPlan))
            {
                PlanViewRange range = vp.GetViewRange();
                double top = GetPlaneElevation(doc, vp, range, PlanViewPlane.TopClipPlane);
                double cut = GetPlaneElevation(doc, vp, range, PlanViewPlane.CutPlane);
                double bottom = GetPlaneElevation(doc, vp, range, PlanViewPlane.BottomClipPlane);
                double depth = GetPlaneElevation(doc, vp, range, PlanViewPlane.ViewDepthPlane);

                section.Info($"View range (ft): Top {Ft(top)} | Cut {Ft(cut)} | Bottom {Ft(bottom)} | Depth {Ft(depth)}");
                section.Info($"Element Z extents (ft): {Ft(elemMinZ)} to {Ft(elemMaxZ)}");

                VisibilityFinding f;
                if (elemMaxZ < depth)
                    f = section.Blocker("Element is entirely BELOW the View Depth.");
                else if (elemMinZ > top)
                    f = section.Blocker("Element is entirely ABOVE the Top of the view range.");
                else if (elemMinZ > cut)
                    f = section.Warn("Element is entirely between the Cut Plane and Top. Most model categories only display at or below the cut plane (exceptions include Windows, Casework, Generic Models, and Specialty Equipment). Lower-mounted geometry or a Plan Region may be needed, or use an RCP.");
                else if (elemMaxZ < bottom)
                    f = section.Info("Element is between Bottom and View Depth. It displays with the <Beyond> line style.");
                else
                    f = section.Ok("Element falls within the primary view range.");

                f.Note("Uses the element's bounding box. Plan Regions in this view are not evaluated.");
            }
            else if (view is ViewPlan rcp && rcp.ViewType == ViewType.CeilingPlan)
            {
                section.Info("Ceiling plan: view range looks UP from the cut plane. Elements below the cut plane don't display.");
            }
        }
        catch (Exception ex)
        {
            section.Warn($"Could not evaluate view range: {ex.Message}");
        }

        try
        {
            if (view.CropBoxActive && view is not View3D)
            {
                BoundingBoxXYZ crop = view.CropBox;
                Transform inv = crop.Transform.Inverse;
                var local = corners.Select(c => inv.OfPoint(c)).ToList();

                bool inX = local.Max(p => p.X) >= crop.Min.X && local.Min(p => p.X) <= crop.Max.X;
                bool inY = local.Max(p => p.Y) >= crop.Min.Y && local.Min(p => p.Y) <= crop.Max.Y;

                var cropFinding = inX && inY
                    ? section.Ok("Element is inside the crop region.")
                    : section.Blocker("Element is OUTSIDE the crop region (or scope box).");
                cropFinding.Note("Rectangular test. Non-rectangular crop shapes are approximated by their bounding rectangle.");

                if (view.ViewType is ViewType.Section or ViewType.Elevation or ViewType.Detail)
                {
                    int farClip = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.AsInteger() ?? 0;
                    bool inZ = local.Max(p => p.Z) >= crop.Min.Z && local.Min(p => p.Z) <= crop.Max.Z;

                    if (farClip != 0 && !inZ)
                        section.Blocker("Element is outside the view depth (behind the far clip plane or in front of the section line).");
                    else if (!inZ)
                        section.Warn("Element is outside the section's depth box. Check whether it is in front of the section/elevation line.");
                    else
                        section.Ok("Element is within the view's depth.");
                }
            }
            else if (view is not View3D)
            {
                section.Ok("Crop region is off.");
            }
        }
        catch (Exception ex)
        {
            section.Warn($"Could not evaluate crop region: {ex.Message}");
        }

        try
        {
            if (view is View3D v3)
            {
                if (v3.IsSectionBoxActive)
                {
                    BoundingBoxXYZ sb = v3.GetSectionBox();
                    Transform inv = sb.Transform.Inverse;
                    var local = corners.Select(c => inv.OfPoint(c)).ToList();

                    bool inside = local.Max(p => p.X) >= sb.Min.X && local.Min(p => p.X) <= sb.Max.X
                        && local.Max(p => p.Y) >= sb.Min.Y && local.Min(p => p.Y) <= sb.Max.Y
                        && local.Max(p => p.Z) >= sb.Min.Z && local.Min(p => p.Z) <= sb.Max.Z;

                    if (inside) section.Ok("Element intersects the section box.");
                    else section.Blocker("Element is OUTSIDE the 3D section box.");
                }
                else
                {
                    section.Ok("Section box is off.");
                }
            }
        }
        catch (Exception ex)
        {
            section.Warn($"Could not evaluate section box: {ex.Message}");
        }
    }

    // -- Helpers -------------------------------------------------------------

    private static string Rgb(Color c) => $"RGB({c.Red},{c.Green},{c.Blue})";

    private static string Ft(double value) =>
        double.IsInfinity(value) ? "Unlimited" : value.ToString("0.###");

    private static ElementId FindSubCategoryId(Category? parent, string name)
    {
        if (parent?.SubCategories is null) return ElementId.InvalidElementId;
        foreach (Category sub in parent.SubCategories)
            if (sub.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return sub.Id;
        return ElementId.InvalidElementId;
    }

    private static List<XYZ>? GetHostCorners(Element elem, Transform linkTransform)
    {
        BoundingBoxXYZ? bb = elem.get_BoundingBox(null);
        if (bb is null) return null;

        var result = new List<XYZ>();
        foreach (double x in new[] { bb.Min.X, bb.Max.X })
            foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                    result.Add(linkTransform.OfPoint(bb.Transform.OfPoint(new XYZ(x, y, z))));
        return result;
    }

    private static double GetPlaneElevation(Document doc, ViewPlan vp, PlanViewRange range, PlanViewPlane plane)
    {
        ElementId levelId = range.GetLevelId(plane);
        double unlimited = plane == PlanViewPlane.TopClipPlane ? double.PositiveInfinity : double.NegativeInfinity;

        if (levelId.Equals(PlanViewRange.Unlimited)) return unlimited;

        Level? level;
        if (levelId.Equals(PlanViewRange.Current)) level = vp.GenLevel;
        else if (levelId.Equals(PlanViewRange.LevelAbove)) level = GetAdjacentLevel(doc, vp.GenLevel, true);
        else if (levelId.Equals(PlanViewRange.LevelBelow)) level = GetAdjacentLevel(doc, vp.GenLevel, false);
        else level = doc.GetElement(levelId) as Level;

        return level is null ? unlimited : level.ProjectElevation + range.GetOffset(plane);
    }

    private static Level? GetAdjacentLevel(Document doc, Level? reference, bool above)
    {
        if (reference is null) return null;

        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.ProjectElevation)
            .ToList();

        return above
            ? levels.FirstOrDefault(l => l.ProjectElevation > reference.ProjectElevation + 0.001)
            : levels.LastOrDefault(l => l.ProjectElevation < reference.ProjectElevation - 0.001);
    }

    private static int CountGeometry(GeometryElement? geom)
    {
        if (geom is null) return 0;

        int count = 0;
        foreach (GeometryObject obj in geom)
        {
            if (obj is GeometryInstance gi) count += CountGeometry(gi.GetInstanceGeometry());
            else if (obj is Solid s) { if (s.Faces.Size > 0) count++; }
            else if (obj is Curve or Mesh or PolyLine or Point) count++;
        }
        return count;
    }
}
