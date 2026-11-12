namespace Chandlery.Models;

/// <summary>
/// A node in a company's vessel group tree (up to three levels, for example Fleet › Tankers › Far East).
/// Level 1 is mandatory: every vessel sits under a level 1 group, and may be placed on a deeper node.
/// </summary>
public class VesselGroup
{
    public const int MaxLevel = 3;

    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>1 for a top group, 2 and 3 below it. Kept in step with the parent whenever a group moves.</summary>
    public int Level { get; set; } = 1;
    public int SortOrder { get; set; }

    /// <summary>Flat group list into (group, path, depth) rows, depth first, siblings by sort order then name.</summary>
    public static List<(VesselGroup Group, string Path, int Depth)> BuildTree(IEnumerable<VesselGroup> all)
    {
        var byParent = all.ToLookup(g => g.ParentId);
        var result = new List<(VesselGroup, string, int)>();

        void Walk(Guid? parentId, string parentPath, int depth)
        {
            foreach (var group in byParent[parentId].OrderBy(g => g.SortOrder).ThenBy(g => g.Name))
            {
                var path = string.IsNullOrEmpty(parentPath) ? group.Name : $"{parentPath} › {group.Name}";
                result.Add((group, path, depth));
                Walk(group.Id, path, depth + 1);
            }
        }

        Walk(null, "", 0);
        return result;
    }

    /// <summary>The group itself plus every group beneath it.</summary>
    public static HashSet<Guid> Descendants(IEnumerable<VesselGroup> all, Guid rootId)
    {
        var byParent = all.ToLookup(g => g.ParentId);
        var result = new HashSet<Guid> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
            foreach (var child in byParent[queue.Dequeue()])
                if (result.Add(child.Id))
                    queue.Enqueue(child.Id);
        return result;
    }
}

/// <summary>
/// One policy row: what a target (a group, applying to every vessel below it, or a single vessel) may do for a subject
/// (a department, a category, both, or neither meaning everything). A capability left blank has no opinion and is decided
/// by a less specific row; an explicit false denies and overrides anything inherited.
/// </summary>
public class VesselPolicy
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>Exactly one of GroupId and VesselId is set.</summary>
    public Guid? GroupId { get; set; }
    public Guid? VesselId { get; set; }

    public Guid? DepartmentId { get; set; }
    public Guid? CategoryId { get; set; }

    public bool? CanRequisition { get; set; }
    public bool? CanPurchaseDirect { get; set; }

    /// <summary>Up to this amount (compared in USD) the Master may approve a direct PO for this subject; above it the office approves.</summary>
    public decimal? MasterApprovalLimitUsd { get; set; }

    /// <summary>Optional monthly spend cap per vessel for this subject, in USD.</summary>
    public decimal? MonthlyBudgetUsd { get; set; }

    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Switches a module on or off for a group or vessel. Resolved like policy rows; no row means off.</summary>
public class VesselModuleSwitch
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? VesselId { get; set; }
    public string Module { get; set; } = "";
    public bool Enabled { get; set; }
}

/// <summary>
/// Limits an office user to some vessel groups (and everything beneath them). A user with no rows here sees every vessel.
/// </summary>
public class UserGroupScope
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
}

public static class PolicyModules
{
    public const string Procurement = "Procurement";
    public const string Worklist = "Worklist";
    public const string Pms = "PMS";
    public const string RiskAssessment = "Risk Assessment";
    public const string TankInspection = "Tank Inspection";
    public const string Projects = "Projects";
    public const string RestHours = "Rest Hours";
    public const string Incidents = "Incidents";
    public const string Drills = "Drills and Meetings";

    public static readonly string[] All = [Procurement, Worklist, Pms, RiskAssessment, TankInspection, Projects, RestHours, Incidents, Drills];
}
