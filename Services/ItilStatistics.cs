using System.Globalization;
using GlpiNg.Modules.Assistance.Models;
using GlpiNg.Modules.Abstractions.Localization;

namespace GlpiNg.Modules.Assistance.Services;

/// <summary>
/// Un objet d'assistance ramené à ce que les statistiques en lisent, quel que soit son type : les
/// tickets, problèmes et changements se comptent de la même façon, seuls leurs statuts diffèrent.
/// Les dates sont dans le fuseau de l'utilisateur : c'est dans ce référentiel qu'on dit « ouvert en
/// septembre » ou « résolu lundi ».
/// </summary>
public sealed record ItilStatItem(
    DateTime OpenedAt,
    DateTime? SolvedAt,
    DateTime? ClosedAt,
    bool IsOpen,
    bool IsOverdue,
    string StatusLabel,
    string? Category,
    int? AssignedUserId,
    ItilLevel Priority,
    string? TypeLabel,
    bool? ResolvedWithinSla)
{
    public TimeSpan? ResolutionTime => SolvedAt is { } solved && solved >= OpenedAt ? solved - OpenedAt : null;
}

/// <summary>Granularité de l'axe du temps, choisie d'après la longueur de la période.</summary>
public enum StatBucketSize
{
    Day,
    Week,
    Month,
}

/// <summary>Un pas de l'axe du temps et ce qui s'y est passé.</summary>
public sealed record StatBucket(DateTime Start, string Label, string LongLabel, int Opened, int Solved, int Closed);

/// <summary>Une ligne de répartition (par catégorie, technicien, priorité...).</summary>
public sealed record StatBreakdownRow(string Label, int Opened, int Solved, TimeSpan? AverageResolution);

/// <summary>Chiffres d'une période : ce que la page affiche en tuiles.</summary>
public sealed record StatSummary(
    int Opened,
    int Solved,
    int Closed,
    int Backlog,
    int Overdue,
    TimeSpan? AverageResolution,
    TimeSpan? MedianResolution,
    int SlaMeasured,
    int SlaMet);

/// <summary>
/// Calculs des statistiques de l'assistance (<c>Statistiques</c> de GLPI, vue globale) : ce qui a
/// été ouvert, résolu et clos sur une période, le délai de résolution, et leur répartition.
///
/// Trois compteurs indépendants, comme dans GLPI : un ticket ouvert en août et résolu en septembre
/// compte dans les ouverts d'août et les résolus de septembre. C'est ce qui permet de lire la
/// charge entrante contre la charge traitée, mois par mois.
/// </summary>
public static class ItilStatistics
{
    // Noms de jours et de mois dans la langue de l'interface (voir Tr), pas dans celle du serveur.
    private static CultureInfo UiCulture => CultureInfo.CurrentUICulture;

    /// <summary>Au-delà, une répartition regroupe le reste sous « Autres » : une liste de cinquante
    /// catégories ne se lit plus.</summary>
    public const int MaxBreakdownRows = 10;

    public static bool InPeriod(DateTime? date, DateTime from, DateTime toExclusive) =>
        date is { } value && value >= from && value < toExclusive;

    public static StatSummary Summarize(IReadOnlyList<ItilStatItem> items, DateTime from, DateTime toExclusive)
    {
        List<ItilStatItem> solved = [.. items.Where(item => InPeriod(item.SolvedAt, from, toExclusive))];
        List<TimeSpan> durations = [.. solved.Select(item => item.ResolutionTime).OfType<TimeSpan>().Order()];
        List<ItilStatItem> slaMeasured = [.. solved.Where(item => item.ResolvedWithinSla is not null)];

        return new StatSummary(
            Opened: items.Count(item => InPeriod(item.OpenedAt, from, toExclusive)),
            Solved: solved.Count,
            Closed: items.Count(item => InPeriod(item.ClosedAt, from, toExclusive)),
            Backlog: items.Count(item => item.IsOpen),
            Overdue: items.Count(item => item.IsOpen && item.IsOverdue),
            AverageResolution: Average(durations),
            MedianResolution: durations.Count == 0 ? null : durations[durations.Count / 2],
            SlaMeasured: slaMeasured.Count,
            SlaMet: slaMeasured.Count(item => item.ResolvedWithinSla == true));
    }

    /// <summary>Jour jusqu'à un mois, semaine jusqu'à six mois, mois au-delà : assez de points pour
    /// une tendance, pas au point de faire du bruit.</summary>
    public static StatBucketSize BucketSizeFor(DateTime from, DateTime toExclusive) =>
        (toExclusive - from).TotalDays switch
        {
            <= 31 => StatBucketSize.Day,
            <= 183 => StatBucketSize.Week,
            _ => StatBucketSize.Month,
        };

    public static List<StatBucket> Buckets(IReadOnlyList<ItilStatItem> items, DateTime from, DateTime toExclusive)
    {
        StatBucketSize size = BucketSizeFor(from, toExclusive);
        List<StatBucket> buckets = [];

        for (DateTime start = BucketStart(from, size); start < toExclusive; start = Next(start, size))
        {
            // Les bornes du premier et du dernier pas sont ramenées à la période : une période qui
            // commence un mercredi ne compte pas le lundi et le mardi de sa première semaine.
            DateTime bucketFrom = start < from ? from : start;
            DateTime next = Next(start, size);
            DateTime bucketTo = next > toExclusive ? toExclusive : next;

            buckets.Add(new StatBucket(
                start,
                Label(start, size),
                LongLabel(start, next, size),
                items.Count(item => InPeriod(item.OpenedAt, bucketFrom, bucketTo)),
                items.Count(item => InPeriod(item.SolvedAt, bucketFrom, bucketTo)),
                items.Count(item => InPeriod(item.ClosedAt, bucketFrom, bucketTo))));
        }

        return buckets;
    }

    /// <summary>
    /// Répartition selon une clé : ouverts sur la période, résolus sur la période, et délai moyen de
    /// ces derniers. Trié par volume, le reste regroupé sous « Autres » passé <see cref="MaxBreakdownRows"/>.
    /// Un <paramref name="order"/> impose l'ordre (les priorités se lisent de la plus haute à la
    /// plus basse, pas par volume) et affiche aussi les valeurs à zéro.
    /// </summary>
    public static List<StatBreakdownRow> Breakdown(
        IReadOnlyList<ItilStatItem> items,
        DateTime from,
        DateTime toExclusive,
        Func<ItilStatItem, string> key,
        IReadOnlyList<string>? order = null)
    {
        List<ItilStatItem> relevant = [.. items.Where(item =>
            InPeriod(item.OpenedAt, from, toExclusive) || InPeriod(item.SolvedAt, from, toExclusive))];

        List<StatBreakdownRow> rows = [.. relevant
            .GroupBy(key)
            .Select(group => Row(group.Key, group, from, toExclusive))];

        if (order is not null)
        {
            return [.. order.Select(label => rows.FirstOrDefault(row => row.Label == label)
                ?? new StatBreakdownRow(label, 0, 0, null))];
        }

        rows = [.. rows.OrderByDescending(row => row.Opened).ThenByDescending(row => row.Solved).ThenBy(row => row.Label)];

        if (rows.Count <= MaxBreakdownRows)
        {
            return rows;
        }

        HashSet<string> kept = [.. rows.Take(MaxBreakdownRows - 1).Select(row => row.Label)];
        StatBreakdownRow others = Row("Autres", relevant.Where(item => !kept.Contains(key(item))), from, toExclusive);

        return [.. rows.Take(MaxBreakdownRows - 1), others];
    }

    private static StatBreakdownRow Row(string label, IEnumerable<ItilStatItem> group, DateTime from, DateTime toExclusive)
    {
        List<ItilStatItem> all = [.. group];
        List<ItilStatItem> solved = [.. all.Where(item => InPeriod(item.SolvedAt, from, toExclusive))];

        return new StatBreakdownRow(
            label,
            all.Count(item => InPeriod(item.OpenedAt, from, toExclusive)),
            solved.Count,
            Average([.. solved.Select(item => item.ResolutionTime).OfType<TimeSpan>()]));
    }

    private static TimeSpan? Average(List<TimeSpan> durations) =>
        durations.Count == 0 ? null : TimeSpan.FromTicks((long)durations.Average(duration => duration.Ticks));

    /// <summary>« 3 j 4 h », « 5 h 20 min », « 12 min » : deux unités suffisent à un délai moyen.</summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } value)
        {
            return "—";
        }

        if (value.TotalDays >= 1)
        {
            return value.Hours == 0 ? $"{(int)value.TotalDays} j" : $"{(int)value.TotalDays} j {value.Hours} h";
        }

        if (value.TotalHours >= 1)
        {
            return value.Minutes == 0 ? $"{value.Hours} h" : Tr.T("{0} h {1} min", value.Hours, value.Minutes);
        }

        return Tr.T("{0} min", Math.Max(1, (int)Math.Round(value.TotalMinutes)));
    }

    private static DateTime BucketStart(DateTime date, StatBucketSize size) => size switch
    {
        StatBucketSize.Day => date.Date,
        StatBucketSize.Week => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        _ => new DateTime(date.Year, date.Month, 1),
    };

    private static DateTime Next(DateTime start, StatBucketSize size) => size switch
    {
        StatBucketSize.Day => start.AddDays(1),
        StatBucketSize.Week => start.AddDays(7),
        _ => start.AddMonths(1),
    };

    private static string Label(DateTime start, StatBucketSize size) => size switch
    {
        StatBucketSize.Day => start.ToString("dd/MM", UiCulture),
        StatBucketSize.Week => start.ToString("dd/MM", UiCulture),
        _ => start.ToString("MMM yy", UiCulture),
    };

    private static string LongLabel(DateTime start, DateTime next, StatBucketSize size) => size switch
    {
        StatBucketSize.Day => start.ToString("dddd d MMMM yyyy", UiCulture),
        StatBucketSize.Week => Tr.T("Semaine du {0} au {1}", start.ToString("d MMMM", UiCulture), next.AddDays(-1).ToString("d MMMM yyyy", UiCulture)),
        _ => start.ToString("MMMM yyyy", UiCulture),
    };
}
