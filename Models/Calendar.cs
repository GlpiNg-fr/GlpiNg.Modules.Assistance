using GlpiNg.Modules.Abstractions.Entities;

namespace GlpiNg.Modules.Assistance.Models;

/// <summary>
/// Calendrier d'ouverture (<c>glpi_calendars</c>) : les plages pendant lesquelles le service
/// travaille, et les jours où il ne travaille pas. C'est ce qui donne son sens à une durée de
/// niveau de service — « 4 heures » sur un incident ouvert vendredi à 17 h veut dire lundi matin,
/// pas samedi soir.
///
/// Rangé dans le module Assistance parce que c'est son seul usage aujourd'hui. Le jour où le
/// déploiement voudra s'en servir (il a ses propres <c>TimeSlot</c>, de forme voisine mais
/// d'intention différente : quand a-t-on le droit de déployer, et non quand travaille-t-on), ce
/// sera le moment de le promouvoir chez l'hôte plutôt que de faire dépendre un module d'un autre.
/// </summary>
public class Calendar : IEntityScoped
{
    /// <inheritdoc />
    public int? EntityId { get; set; }

    /// <inheritdoc />
    public bool IsRecursive { get; set; }

    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Comment { get; set; }

    public List<CalendarSegment> Segments { get; set; } = [];

    public List<CalendarHoliday> Holidays { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Un calendrier sans aucune plage n'ouvre jamais : une échéance calculée dessus n'arriverait
    /// pas. Les écrans le signalent plutôt que de laisser poser un niveau de service inerte.
    /// </summary>
    public bool IsEmpty => Segments.Count == 0;
}

/// <summary>
/// Plage travaillée d'un jour de la semaine (<c>glpi_calendarsegments</c>). Plusieurs plages par
/// jour sont permises : c'est ainsi qu'on ferme entre midi et deux.
/// </summary>
public class CalendarSegment
{
    public int Id { get; set; }

    public int CalendarId { get; set; }
    public Calendar? Calendar { get; set; }

    public DayOfWeek DayOfWeek { get; set; }

    public TimeOnly StartTime { get; set; }

    public TimeOnly EndTime { get; set; }

    /// <summary>Durée de la plage. Une plage dont la fin précède le début ne compte pour rien.</summary>
    public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
}

/// <summary>
/// Période de fermeture (<c>glpi_holidays</c>) : un jour férié, un pont, une fermeture annuelle.
/// Pendant ces jours, le compteur d'un niveau de service ne tourne pas.
/// </summary>
public class CalendarHoliday
{
    public int Id { get; set; }

    public int CalendarId { get; set; }
    public Calendar? Calendar { get; set; }

    public required string Name { get; set; }

    public DateOnly StartDate { get; set; }

    /// <summary>Dernier jour fermé, inclus. Égal au premier pour une fermeture d'un seul jour.</summary>
    public DateOnly EndDate { get; set; }

    /// <summary>
    /// Fermeture qui revient chaque année à la même date (1er mai, 14 juillet). Seuls le jour et le
    /// mois comptent alors ; l'année saisie ne sert qu'à mémoriser la première occurrence.
    /// </summary>
    public bool IsPerpetual { get; set; }
}
