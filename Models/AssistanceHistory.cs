namespace GlpiNg.Modules.Assistance.Models;

/// <summary>
/// Une ligne d'historique telle que la fiche l'affiche, projetée depuis
/// <see cref="AssistanceHistoryEntry"/> — voir <c>Components/Shared/ItemHistoryTab.razor</c>.
/// </summary>
public sealed record HistoryRow(DateTime OccurredAt, string? User, string Field, string? Description);

/// <summary>
/// Compare un avant/après et n'ajoute une ligne d'historique que si la valeur a bougé. Même
/// mécanique que dans le module Gestion : la dupliquer ici évite de faire dépendre l'assistance de
/// la gestion, deux domaines qui n'ont rien à se dire.
/// </summary>
public sealed class AssistanceHistoryRecorder(string itemType, int itemId, string? user)
{
    private readonly List<AssistanceHistoryEntry> _entries = [];

    public IReadOnlyList<AssistanceHistoryEntry> Entries => _entries;

    public bool HasChanges => _entries.Count > 0;

    public void Track(string field, string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        _entries.Add(new AssistanceHistoryEntry
        {
            ItemType = itemType,
            ItemId = itemId,
            User = user,
            Field = field,
            Description = $"« {Displayable(before)} » → « {Displayable(after)} »",
        });
    }

    public void Track(string field, DateTime? before, DateTime? after) =>
        Track(field, before?.ToString("dd/MM/yyyy HH:mm"), after?.ToString("dd/MM/yyyy HH:mm"));

    private static string Displayable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(vide)" : value;
}
