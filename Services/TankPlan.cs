using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>How a tank stands according to its latest submitted inspection. Worked out, never stored.</summary>
public enum TankHealth { NotInspected, Clean, Exceptions, Bad }

/// <summary>Names and colours for showing tanks, kept in one place so the plan, the list and the reports agree.</summary>
public static class TankLook
{
    public static string Label(TankType type) => type switch
    {
        TankType.FreshWater => "Fresh water",
        TankType.VoidSpace => "Void space",
        _ => type.ToString(),
    };

    public static string Label(TankHealth health) => health switch
    {
        TankHealth.NotInspected => "Not inspected",
        TankHealth.Clean => "No exceptions",
        TankHealth.Exceptions => "Exceptions",
        _ => "Bad rating",
    };

    /// <summary>Fill colour for a tank drawn by type (readable in light and dark themes).</summary>
    public static string Colour(TankType type) => type switch
    {
        TankType.Cargo => "#6366f1",
        TankType.Ballast => "#0ea5e9",
        TankType.Slops => "#f59e0b",
        TankType.FreshWater => "#14b8a6",
        TankType.VoidSpace => "#94a3b8",
        _ => "#a855f7",
    };

    public static string Colour(TankHealth health) => health switch
    {
        TankHealth.NotInspected => "#94a3b8",
        TankHealth.Clean => "#10b981",
        TankHealth.Exceptions => "#f59e0b",
        _ => "#f43f5e",
    };

    public static TankHealth HealthOf(bool inspected, int exceptionCount, bool hasBadRating)
        => !inspected ? TankHealth.NotInspected : hasBadRating ? TankHealth.Bad : exceptionCount > 0 ? TankHealth.Exceptions : TankHealth.Clean;
}

/// <summary>A tank with what its latest submitted inspection said, for the plan and the list.</summary>
public record TankPlanRow(Tank Tank, int InspectionCount, DateTime? LastInspectedOn, int ExceptionCount, bool HasBadRating)
{
    public TankHealth Health => TankLook.HealthOf(LastInspectedOn is not null, ExceptionCount, HasBadRating);
}

public record TankCopyResult(string Vessel, int Added, int Skipped, string? Problem = null);
