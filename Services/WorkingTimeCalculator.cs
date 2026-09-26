using GlpiNg.Modules.Assistance.Models;

namespace GlpiNg.Modules.Assistance.Services;

/// <summary>
/// Arithmétique des heures ouvrées : « quelle date tombe 4 heures de travail après celle-ci ? ».
/// C'est ce qui sépare un niveau de service crédible d'un simple délai en heures.
///
/// Sans calendrier, le temps est compté en heures réelles — c'est le comportement de GLPI quand
/// aucun calendrier n'est rattaché, et il vaut mieux qu'un refus : une astreinte continue n'a pas
/// besoin d'un calendrier pour que ses SLA aient un sens.
///
/// Les dates manipulées ici sont **locales**, pas UTC : un calendrier dit « du lundi au vendredi,
/// 9 h - 18 h », ce qui n'a de sens que dans l'heure du service. Les appelants convertissent avant
/// et après — voir <see cref="ServiceLevelService"/>.
/// </summary>
public static class WorkingTimeCalculator
{
    /// <summary>
    /// Garde-fou : nombre maximal de jours parcourus avant d'abandonner. Un calendrier dont toutes
    /// les plages sont vides ou entièrement couvertes par des fermetures n'ouvrirait jamais, et la
    /// boucle ne se terminerait pas. Deux ans couvrent tout délai raisonnable, jours fériés
    /// compris, et laissent l'appelant décider quoi faire d'une échéance impossible.
    /// </summary>
    private const int MaxDaysScanned = 730;

    /// <summary>
    /// Ajoute <paramref name="duration"/> de temps ouvré à <paramref name="start"/>.
    /// Renvoie <c>null</c> si le calendrier n'ouvre jamais assez pour absorber la durée.
    /// </summary>
    public static DateTime? Add(DateTime start, TimeSpan duration, Calendar? calendar)
    {
        if (duration <= TimeSpan.Zero)
        {
            return start;
        }

        // Pas de calendrier, ou un calendrier sans plage : temps calendaire. Ne pas traiter le
        // second cas comme le premier laisserait poser une échéance qui n'arrive jamais.
        if (calendar is null || calendar.Segments.Count == 0)
        {
            return start + duration;
        }

        TimeSpan remaining = duration;
        DateTime cursor = start;

        for (int day = 0; day <= MaxDaysScanned; day++)
        {
            DateOnly date = DateOnly.FromDateTime(cursor);

            if (!IsClosed(date, calendar))
            {
                // Les plages d'un même jour sont parcourues dans l'ordre : on ne consomme que ce
                // qui reste après l'heure du curseur, pour qu'un ticket ouvert à 16 h ne se voie
                // pas créditer la matinée.
                foreach (CalendarSegment segment in SegmentsOf(date, calendar))
                {
                    // Le curseur porte l'heure réelle au premier tour, puis minuit les jours
                    // suivants : la comparaison suffit donc à distinguer les deux cas.
                    TimeOnly cursorTime = TimeOnly.FromDateTime(cursor);
                    TimeOnly from = cursorTime > segment.StartTime ? cursorTime : segment.StartTime;

                    if (from >= segment.EndTime)
                    {
                        continue;
                    }

                    TimeSpan available = segment.EndTime - from;

                    if (available >= remaining)
                    {
                        return date.ToDateTime(from.Add(remaining));
                    }

                    remaining -= available;
                }
            }

            // Jour suivant, à son tout début : le curseur ne porte plus d'heure de départ.
            cursor = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        }

        return null;
    }

    /// <summary>
    /// Retire <paramref name="duration"/> de temps ouvré à <paramref name="start"/> : « une heure
    /// ouvrée avant cette échéance ». C'est ce que consomment les niveaux d'escalade, pour qu'un
    /// « 2 h avant » sur une échéance du lundi matin tombe le vendredi après-midi, quand il y a
    /// encore quelqu'un pour agir, et non le lundi à 7 h.
    /// </summary>
    public static DateTime? Subtract(DateTime start, TimeSpan duration, Calendar? calendar)
    {
        if (duration <= TimeSpan.Zero)
        {
            return start;
        }

        if (calendar is null || calendar.Segments.Count == 0)
        {
            return start - duration;
        }

        TimeSpan remaining = duration;
        DateTime cursor = start;

        for (int day = 0; day <= MaxDaysScanned; day++)
        {
            DateOnly date = DateOnly.FromDateTime(cursor);

            if (!IsClosed(date, calendar))
            {
                // Les plages sont parcourues à rebours, de la fin de journée vers le matin, et on
                // ne consomme que ce qui précède l'heure du curseur.
                foreach (CalendarSegment segment in SegmentsOf(date, calendar).Reverse())
                {
                    TimeOnly cursorTime = TimeOnly.FromDateTime(cursor);
                    TimeOnly until = cursorTime < segment.EndTime ? cursorTime : segment.EndTime;

                    if (until <= segment.StartTime)
                    {
                        continue;
                    }

                    TimeSpan available = until - segment.StartTime;

                    if (available >= remaining)
                    {
                        return date.ToDateTime(until.Add(-remaining));
                    }

                    remaining -= available;
                }
            }

            // Jour précédent, pris à sa toute fin : le curseur ne porte plus d'heure d'arrivée.
            cursor = date.AddDays(-1).ToDateTime(TimeOnly.MaxValue);
        }

        return null;
    }

    /// <summary>
    /// Temps ouvré écoulé entre deux dates. Sert à dire de combien une échéance a été manquée, et
    /// alimentera les statistiques le jour où elles existeront.
    /// </summary>
    public static TimeSpan Elapsed(DateTime from, DateTime to, Calendar? calendar)
    {
        if (to <= from)
        {
            return TimeSpan.Zero;
        }

        if (calendar is null || calendar.Segments.Count == 0)
        {
            return to - from;
        }

        TimeSpan total = TimeSpan.Zero;
        DateOnly date = DateOnly.FromDateTime(from);
        DateOnly last = DateOnly.FromDateTime(to);

        for (int day = 0; date <= last && day <= MaxDaysScanned; day++, date = date.AddDays(1))
        {
            if (IsClosed(date, calendar))
            {
                continue;
            }

            foreach (CalendarSegment segment in SegmentsOf(date, calendar))
            {
                DateTime segmentStart = date.ToDateTime(segment.StartTime);
                DateTime segmentEnd = date.ToDateTime(segment.EndTime);

                DateTime overlapStart = segmentStart > from ? segmentStart : from;
                DateTime overlapEnd = segmentEnd < to ? segmentEnd : to;

                if (overlapEnd > overlapStart)
                {
                    total += overlapEnd - overlapStart;
                }
            }
        }

        return total;
    }

    /// <summary>Le service travaille-t-il à cet instant précis ?</summary>
    public static bool IsOpenAt(DateTime moment, Calendar? calendar)
    {
        if (calendar is null || calendar.Segments.Count == 0)
        {
            return true;
        }

        DateOnly date = DateOnly.FromDateTime(moment);

        if (IsClosed(date, calendar))
        {
            return false;
        }

        TimeOnly time = TimeOnly.FromDateTime(moment);

        return SegmentsOf(date, calendar).Any(segment => time >= segment.StartTime && time < segment.EndTime);
    }

    private static IEnumerable<CalendarSegment> SegmentsOf(DateOnly date, Calendar calendar) =>
        calendar.Segments
            .Where(segment => segment.DayOfWeek == date.DayOfWeek && segment.Duration > TimeSpan.Zero)
            .OrderBy(segment => segment.StartTime);

    /// <summary>
    /// Jour couvert par une fermeture. Une fermeture perpétuelle ne compare que le jour et le
    /// mois : le 1er mai vaut pour toutes les années, sans qu'on ait à le ressaisir chaque janvier.
    /// </summary>
    private static bool IsClosed(DateOnly date, Calendar calendar)
    {
        foreach (CalendarHoliday holiday in calendar.Holidays)
        {
            if (!holiday.IsPerpetual)
            {
                if (date >= holiday.StartDate && date <= holiday.EndDate)
                {
                    return true;
                }

                continue;
            }

            // Perpétuelle : on rejoue la période sur l'année de la date examinée. Une période qui
            // franchit le 31 décembre (Noël au Jour de l'an) est donc aussi testée sur l'année
            // précédente, sans quoi le 1er janvier passerait au travers.
            if (CoversPerpetual(date, holiday, date.Year) || CoversPerpetual(date, holiday, date.Year - 1))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CoversPerpetual(DateOnly date, CalendarHoliday holiday, int year)
    {
        DateOnly start = SafeDate(year, holiday.StartDate);
        int span = holiday.EndDate.DayNumber - holiday.StartDate.DayNumber;

        DateOnly end = start.AddDays(span < 0 ? 0 : span);

        return date >= start && date <= end;
    }

    /// <summary>
    /// Reporte un jour/mois sur une autre année. Le 29 février d'une année bissextile n'existe pas
    /// partout : il est ramené au 28, plutôt que de lever une exception au moment d'un calcul
    /// d'échéance.
    /// </summary>
    private static DateOnly SafeDate(int year, DateOnly source)
    {
        int day = Math.Min(source.Day, DateTime.DaysInMonth(year, source.Month));

        return new DateOnly(year, source.Month, day);
    }
}
