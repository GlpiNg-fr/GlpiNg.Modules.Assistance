using BlazorBootstrap;
using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace GlpiNg.Modules.Assistance.Components.Pages.Tickets;

public partial class Detail : ComponentBase
{
    [Parameter]
    public int TicketId { get; set; }

    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private IPrincipalDirectory Directory { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Inject]
    private ToastService ToastService { get; set; } = null!;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthStateTask { get; set; }

    private Ticket? _ticket;
    private List<TicketCategory> _categories = [];
    private List<ItilFollowup> _followups = [];
    private List<ItilTask> _tasks = [];
    private List<Problem> _problems = [];
    private List<HistoryRow> _history = [];
    private IReadOnlyList<PrincipalOption> _users = [];
    private IReadOnlyList<PrincipalOption> _groups = [];

    // Les listes déroulantes travaillent sur des int (0 pour « aucun ») plutôt que sur les int? du
    // modèle : @bind ne sait pas rendre un null en valeur d'option.
    private int _categoryId;
    private int _requesterId;
    private int _assignedUserId;
    private int _assignedGroupId;

    private string _newFollowup = string.Empty;
    private bool _newFollowupPrivate;

    private ItilTask _newTask = NewBlankTask();
    private int _newTaskUserId;

    private string _activeTab = "fiche";
    private int _documentCount;
    private int _noteCount;
    private bool _isSaving;
    private string? _error;
    private string _currentUserName = "?";

    private IEnumerable<(string Key, string Label, string Icon, int? Count)> Tabs
    {
        get
        {
            yield return ("fiche", "Fiche", "ti-ticket", null);
            yield return ("suivis", "Suivis", "ti-message", _followups.Count);
            yield return ("taches", "Tâches", "ti-checklist", _tasks.Count);
            yield return ("solution", "Solution", "ti-circle-check", null);
            yield return ("problemes", "Problèmes", "ti-bulb", _problems.Count);
            yield return ("documents", "Documents", "ti-file", _documentCount);
            yield return ("notes", "Notes", "ti-notes", _noteCount);
            yield return ("historique", "Historique", "ti-history", _history.Count);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (AuthStateTask is not null)
        {
            AuthenticationState authState = await AuthStateTask;
            _currentUserName = authState.User.Identity?.Name ?? "?";
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _ticket = await db.Set<Ticket>()
            .AsNoTracking()
            .FirstOrDefaultAsync(ticket => ticket.Id == TicketId);

        if (_ticket is null)
        {
            return;
        }

        // Suivis et tâches vivent dans des tables polymorphes : ils se lisent par le couple
        // type/identifiant, comme l'historique juste en dessous, et non par une navigation.
        _followups = await db.Set<ItilFollowup>()
            .AsNoTracking()
            .Where(entry => entry.ItemType == ItemTypes.Ticket && entry.ItemId == TicketId)
            .OrderByDescending(entry => entry.CreatedAt)
            .ToListAsync();

        _tasks = await db.Set<ItilTask>()
            .AsNoTracking()
            .Where(entry => entry.ItemType == ItemTypes.Ticket && entry.ItemId == TicketId)
            .OrderBy(entry => entry.State)
            .ThenBy(entry => entry.PlannedStart)
            .ToListAsync();

        _problems = await db.Set<ProblemTicket>()
            .AsNoTracking()
            .Where(link => link.TicketId == TicketId)
            .Select(link => link.Problem!)
            .OrderByDescending(problem => problem.OpenedAt)
            .ToListAsync();

        _categoryId = _ticket.CategoryId ?? 0;
        _requesterId = _ticket.RequesterUserId ?? 0;
        _assignedUserId = _ticket.AssignedUserId ?? 0;
        _assignedGroupId = _ticket.AssignedGroupId ?? 0;

        _categories = await db.Set<TicketCategory>()
            .AsNoTracking()
            .Include(category => category.Parent)
            .Where(category => category.IsActive)
            .OrderBy(category => category.Name)
            .ToListAsync();

        _history = await db.Set<AssistanceHistoryEntry>()
            .AsNoTracking()
            .Where(entry => entry.ItemType == ItemTypes.Ticket && entry.ItemId == TicketId)
            .OrderByDescending(entry => entry.OccurredAt)
            .Select(entry => new HistoryRow(entry.OccurredAt, entry.User, entry.Field, entry.Description))
            .ToListAsync();

        _users = await Directory.GetAsync(PrincipalKind.User);
        _groups = await Directory.GetAsync(PrincipalKind.Group);
    }

    private static string CategoryPath(TicketCategory category) =>
        category.Parent is { } parent ? $"{parent.Name} > {category.Name}" : category.Name;

    /// <summary>
    /// Un acteur supprimé depuis l'ouverture du ticket laisse un identifiant sans nom : le dire
    /// vaut mieux qu'afficher un numéro ou une case vide, qui laisserait croire à un oubli.
    /// </summary>
    private string NameOfUser(int id) =>
        _users.FirstOrDefault(user => user.Id == id)?.Name ?? $"#{id} (supprimé)";

    private void OnStatusChanged(ChangeEventArgs args)
    {
        if (_ticket is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _ticket.Status = (TicketStatus)value;
    }

    private void OnUrgencyChanged(ChangeEventArgs args)
    {
        if (_ticket is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _ticket.Urgency = (ItilLevel)value;
        RecomputePriority();
    }

    private void OnImpactChanged(ChangeEventArgs args)
    {
        if (_ticket is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _ticket.Impact = (ItilLevel)value;
        RecomputePriority();
    }

    /// <summary>
    /// Toucher la priorité à la main la détache de la matrice : sans ce marqueur, le premier
    /// changement d'urgence l'écraserait sans prévenir.
    /// </summary>
    private void OnPriorityChanged(ChangeEventArgs args)
    {
        if (_ticket is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _ticket.Priority = (ItilLevel)value;
        _ticket.IsPriorityManual = true;
    }

    private void ResetPriority()
    {
        if (_ticket is null)
        {
            return;
        }

        _ticket.IsPriorityManual = false;
        RecomputePriority();
    }

    private void RecomputePriority()
    {
        if (_ticket is null || _ticket.IsPriorityManual)
        {
            return;
        }

        _ticket.Priority = ItilPriorityMatrix.Compute(_ticket.Urgency, _ticket.Impact);
    }

    private async Task SaveAsync()
    {
        if (_ticket is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_ticket.Name))
        {
            _error = "Le titre est obligatoire.";
            _activeTab = "fiche";
            return;
        }

        _error = null;
        _isSaving = true;

        try
        {
            await using DbContext db = await DbFactory.CreateDbContextAsync();

            Ticket? stored = await db.Set<Ticket>().FirstOrDefaultAsync(ticket => ticket.Id == TicketId);
            if (stored is null)
            {
                return;
            }

            _ticket.CategoryId = _categoryId == 0 ? null : _categoryId;
            _ticket.RequesterUserId = _requesterId == 0 ? null : _requesterId;
            _ticket.AssignedUserId = _assignedUserId == 0 ? null : _assignedUserId;
            _ticket.AssignedGroupId = _assignedGroupId == 0 ? null : _assignedGroupId;

            AssistanceHistoryRecorder history = new(ItemTypes.Ticket, TicketId, _currentUserName);

            history.Track("Titre", stored.Name, _ticket.Name);
            history.Track("Description", stored.Content, _ticket.Content);
            history.Track("Type", TicketLabels.For(stored.Type), TicketLabels.For(_ticket.Type));
            history.Track("Statut", TicketLabels.For(stored.Status), TicketLabels.For(_ticket.Status));
            history.Track("Urgence", ItilLabels.For(stored.Urgency), ItilLabels.For(_ticket.Urgency));
            history.Track("Impact", ItilLabels.For(stored.Impact), ItilLabels.For(_ticket.Impact));
            history.Track("Priorité", ItilLabels.For(stored.Priority), ItilLabels.For(_ticket.Priority));
            history.Track("Catégorie", CategoryNameOf(stored.CategoryId), CategoryNameOf(_ticket.CategoryId));
            history.Track("Demandeur", ActorNameOf(stored.RequesterUserId), ActorNameOf(_ticket.RequesterUserId));
            history.Track("Demandeur externe", stored.RequesterName, _ticket.RequesterName);
            history.Track("Technicien", ActorNameOf(stored.AssignedUserId), ActorNameOf(_ticket.AssignedUserId));
            history.Track("Groupe", GroupNameOf(stored.AssignedGroupId), GroupNameOf(_ticket.AssignedGroupId));
            history.Track("Échéance", stored.DueDate, _ticket.DueDate);
            history.Track("Solution", stored.Solution, _ticket.Solution);
            history.Track("Type de solution", stored.SolutionType, _ticket.SolutionType);

            // Le passage en « Résolu » ou « Clos » depuis la liste des statuts doit dater la
            // résolution comme le ferait le bouton « Résoudre » : sinon un ticket résolu
            // n'aurait de date que selon le chemin emprunté pour le clore.
            ApplyStatusDates(stored.Status, _ticket);

            stored.Name = _ticket.Name.Trim();
            stored.Content = _ticket.Content;
            stored.Type = _ticket.Type;
            stored.Status = _ticket.Status;
            stored.Urgency = _ticket.Urgency;
            stored.Impact = _ticket.Impact;
            stored.Priority = _ticket.Priority;
            stored.IsPriorityManual = _ticket.IsPriorityManual;
            stored.CategoryId = _ticket.CategoryId;
            stored.RequesterUserId = _ticket.RequesterUserId;
            stored.RequesterName = _ticket.RequesterName;
            stored.AssignedUserId = _ticket.AssignedUserId;
            stored.AssignedGroupId = _ticket.AssignedGroupId;
            stored.DueDate = _ticket.DueDate;
            stored.Solution = _ticket.Solution;
            stored.SolutionType = _ticket.SolutionType;
            stored.SolvedAt = _ticket.SolvedAt;
            stored.ClosedAt = _ticket.ClosedAt;

            if (history.HasChanges)
            {
                stored.UpdatedAt = DateTime.UtcNow;
                db.Set<AssistanceHistoryEntry>().AddRange(history.Entries);
            }

            await db.SaveChangesAsync();

            ToastService.Notify(new ToastMessage(ToastType.Success, "Ticket enregistré."));
            await LoadAsync();
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>
    /// Dates de résolution et de clôture, déduites du seul statut : elles sont la trace du passage
    /// d'une étape, et les laisser saisir à la main les ferait mentir.
    /// </summary>
    private static void ApplyStatusDates(TicketStatus before, Ticket ticket)
    {
        if (ticket.Status == before)
        {
            return;
        }

        ticket.SolvedAt = ticket.Status switch
        {
            TicketStatus.Solved or TicketStatus.Closed => ticket.SolvedAt ?? DateTime.UtcNow,

            // Retour à un statut ouvert : le ticket n'est plus résolu, sa date de résolution non plus.
            _ => null,
        };

        ticket.ClosedAt = ticket.Status == TicketStatus.Closed ? ticket.ClosedAt ?? DateTime.UtcNow : null;
    }

    private string? CategoryNameOf(int? categoryId) =>
        categoryId is { } id ? _categories.FirstOrDefault(category => category.Id == id)?.Name : null;

    private string? ActorNameOf(int? userId) => userId is { } id ? NameOfUser(id) : null;

    private string? GroupNameOf(int? groupId) =>
        groupId is { } id ? _groups.FirstOrDefault(group => group.Id == id)?.Name ?? $"#{id} (supprimé)" : null;

    private async Task AddFollowupAsync()
    {
        if (_ticket is null || string.IsNullOrWhiteSpace(_newFollowup))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        db.Set<ItilFollowup>().Add(new ItilFollowup
        {
            ItemType = ItemTypes.Ticket,
            ItemId = TicketId,
            Content = _newFollowup.Trim(),
            AuthorName = _currentUserName,
            IsPrivate = _newFollowupPrivate,
        });

        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Ticket,
            ItemId = TicketId,
            User = _currentUserName,
            Field = "Suivi",
            Description = "Ajout d'un suivi",
        });

        await TouchAsync(db);
        await db.SaveChangesAsync();

        _newFollowup = string.Empty;
        _newFollowupPrivate = false;

        await LoadAsync();
    }

    private async Task DeleteFollowupAsync(int followupId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ItilFollowup? followup = await db.Set<ItilFollowup>()
            .FirstOrDefaultAsync(entry =>
                entry.Id == followupId && entry.ItemType == ItemTypes.Ticket && entry.ItemId == TicketId);

        if (followup is null)
        {
            return;
        }

        db.Set<ItilFollowup>().Remove(followup);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task AddTaskAsync()
    {
        if (_ticket is null || string.IsNullOrWhiteSpace(_newTask.Content))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        db.Set<ItilTask>().Add(new ItilTask
        {
            ItemType = ItemTypes.Ticket,
            ItemId = TicketId,
            Content = _newTask.Content.Trim(),
            AssignedUserId = _newTaskUserId == 0 ? null : _newTaskUserId,
            PlannedStart = _newTask.PlannedStart,
            AuthorName = _currentUserName,
        });

        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Ticket,
            ItemId = TicketId,
            User = _currentUserName,
            Field = "Tâche",
            Description = $"Ajout de la tâche « {_newTask.Content.Trim()} »",
        });

        await TouchAsync(db);
        await db.SaveChangesAsync();

        _newTask = NewBlankTask();
        _newTaskUserId = 0;

        await LoadAsync();
    }

    private async Task ToggleTaskAsync(int taskId, bool done)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ItilTask? task = await db.Set<ItilTask>()
            .FirstOrDefaultAsync(entry =>
                entry.Id == taskId && entry.ItemType == ItemTypes.Ticket && entry.ItemId == TicketId);

        if (task is null)
        {
            return;
        }

        task.State = done ? ItilTaskState.Done : ItilTaskState.ToDo;
        task.CompletedAt = done ? DateTime.UtcNow : null;

        await TouchAsync(db);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task DeleteTaskAsync(int taskId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ItilTask? task = await db.Set<ItilTask>()
            .FirstOrDefaultAsync(entry =>
                entry.Id == taskId && entry.ItemType == ItemTypes.Ticket && entry.ItemId == TicketId);

        if (task is null)
        {
            return;
        }

        db.Set<ItilTask>().Remove(task);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task SolveAsync()
    {
        if (_ticket is null || string.IsNullOrWhiteSpace(_ticket.Solution))
        {
            return;
        }

        _ticket.Status = TicketStatus.Solved;
        await SaveAsync();
    }

    private async Task CloseAsync()
    {
        if (_ticket is null)
        {
            return;
        }

        _ticket.Status = TicketStatus.Closed;
        await SaveAsync();
    }

    /// <summary>
    /// Rouvre un ticket résolu ou clos. Le statut revient à « Attribué » s'il y a un technicien,
    /// sinon à « Nouveau » : un ticket rouvert sans personne dessus n'est pas en cours.
    /// </summary>
    private async Task ReopenAsync()
    {
        if (_ticket is null)
        {
            return;
        }

        _ticket.Status = _ticket.AssignedUserId is not null || _assignedUserId != 0
            ? TicketStatus.Assigned
            : TicketStatus.New;

        _activeTab = "fiche";
        await SaveAsync();
    }

    /// <summary>
    /// Marque le ticket comme modifié depuis un geste qui ne passe pas par l'enregistrement de la
    /// fiche (suivi, tâche), pour que la date de dernière activité reste vraie.
    /// </summary>
    private async Task TouchAsync(DbContext db)
    {
        Ticket? stored = await db.Set<Ticket>().FirstOrDefaultAsync(ticket => ticket.Id == TicketId);

        if (stored is not null)
        {
            stored.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task DeleteAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        Ticket? stored = await db.Set<Ticket>().FirstOrDefaultAsync(ticket => ticket.Id == TicketId);
        if (stored is null)
        {
            return;
        }

        // Suivis, tâches et historique sont dans des tables polymorphes, sans clé étrangère vers le
        // ticket : rien ne part en cascade, tout part ici — voir la même règle dans la liste.
        db.Set<ItilFollowup>().RemoveRange(await db.Set<ItilFollowup>()
            .Where(entry => entry.ItemType == ItemTypes.Ticket && entry.ItemId == TicketId)
            .ToListAsync());

        db.Set<ItilTask>().RemoveRange(await db.Set<ItilTask>()
            .Where(entry => entry.ItemType == ItemTypes.Ticket && entry.ItemId == TicketId)
            .ToListAsync());

        db.Set<AssistanceHistoryEntry>().RemoveRange(await db.Set<AssistanceHistoryEntry>()
            .Where(entry => entry.ItemType == ItemTypes.Ticket && entry.ItemId == TicketId)
            .ToListAsync());

        db.Set<Ticket>().Remove(stored);
        await db.SaveChangesAsync();

        Navigation.NavigateTo("/assistance/tickets");
    }

    private static ItilTask NewBlankTask() => new() { ItemType = ItemTypes.Ticket, Content = string.Empty };
}
