using BlazorBootstrap;
using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using GlpiNg.Modules.Abstractions.Localization;

namespace GlpiNg.Modules.Assistance.Components.Pages.Problems;

public partial class Detail : ComponentBase
{
    [Parameter]
    public int ProblemId { get; set; }

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

    private Problem? _problem;
    private List<TicketCategory> _categories = [];
    private List<ItilFollowup> _followups = [];
    private List<ItilTask> _tasks = [];
    private List<Ticket> _tickets = [];
    private List<Ticket> _ticketCandidates = [];
    private List<Change> _changes = [];
    private List<HistoryRow> _history = [];
    private IReadOnlyList<PrincipalOption> _users = [];
    private IReadOnlyList<PrincipalOption> _groups = [];

    // Listes déroulantes sur des int (0 pour « aucun ») : @bind ne sait pas rendre un null en
    // valeur d'option.
    private int _categoryId;
    private int _authorId;
    private int _assignedUserId;
    private int _assignedGroupId;
    private int _ticketToAttach;

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

    /// <summary>Incidents rattachés encore ouverts : ce qui doit faire hésiter avant de clore.</summary>
    private int _openTicketCount => _tickets.Count(ticket => ticket.IsOpen);

    private IEnumerable<(string Key, string Label, string Icon, int? Count)> Tabs
    {
        get
        {
            yield return ("fiche", "Fiche", "ti-bulb", null);
            yield return ("analyse", "Analyse", "ti-zoom-question", null);
            yield return ("incidents", "Incidents", "ti-ticket", _tickets.Count);
            yield return ("suivis", "Suivis", "ti-message", _followups.Count);
            yield return ("taches", "Tâches", "ti-checklist", _tasks.Count);
            yield return ("solution", "Solution", "ti-circle-check", null);
            yield return ("changements", "Changements", "ti-replace", _changes.Count);
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

        _problem = await db.Set<Problem>()
            .AsNoTracking()
            .FirstOrDefaultAsync(problem => problem.Id == ProblemId);

        if (_problem is null)
        {
            return;
        }

        _categoryId = _problem.CategoryId ?? 0;
        _authorId = _problem.AuthorUserId ?? 0;
        _assignedUserId = _problem.AssignedUserId ?? 0;
        _assignedGroupId = _problem.AssignedGroupId ?? 0;

        _followups = await db.Set<ItilFollowup>()
            .AsNoTracking()
            .Where(entry => entry.ItemType == ItemTypes.Problem && entry.ItemId == ProblemId)
            .OrderByDescending(entry => entry.CreatedAt)
            .ToListAsync();

        _tasks = await db.Set<ItilTask>()
            .AsNoTracking()
            .Where(entry => entry.ItemType == ItemTypes.Problem && entry.ItemId == ProblemId)
            .OrderBy(entry => entry.State)
            .ThenBy(entry => entry.PlannedStart)
            .ToListAsync();

        _tickets = await db.Set<ProblemTicket>()
            .AsNoTracking()
            .Where(link => link.ProblemId == ProblemId)
            .Select(link => link.Ticket!)
            .OrderByDescending(ticket => ticket.OpenedAt)
            .ToListAsync();

        // Seuls les incidents pas encore rattachés sont proposés : reproposer les autres inviterait
        // à créer un doublon que l'index unique refuserait de toute façon.
        List<int> attached = [.. _tickets.Select(ticket => ticket.Id)];

        _ticketCandidates = await db.Set<Ticket>()
            .AsNoTracking()
            .Where(ticket => !attached.Contains(ticket.Id))
            .OrderByDescending(ticket => ticket.OpenedAt)
            .ToListAsync();

        _changes = await db.Set<ChangeProblem>()
            .AsNoTracking()
            .Where(link => link.ProblemId == ProblemId)
            .Select(link => link.Change!)
            .OrderByDescending(change => change.OpenedAt)
            .ToListAsync();

        _categories = await db.Set<TicketCategory>()
            .AsNoTracking()
            .Include(category => category.Parent)
            .Where(category => category.IsActive)
            .OrderBy(category => category.Name)
            .ToListAsync();

        _history = await db.Set<AssistanceHistoryEntry>()
            .AsNoTracking()
            .Where(entry => entry.ItemType == ItemTypes.Problem && entry.ItemId == ProblemId)
            .OrderByDescending(entry => entry.OccurredAt)
            .Select(entry => new HistoryRow(entry.OccurredAt, entry.User, entry.Field, entry.Description))
            .ToListAsync();

        _users = await Directory.GetAsync(PrincipalKind.User);
        _groups = await Directory.GetAsync(PrincipalKind.Group);
    }

    private static string CategoryPath(TicketCategory category) =>
        category.Parent is { } parent ? $"{parent.Name} > {category.Name}" : category.Name;

    /// <summary>
    /// Un acteur supprimé depuis l'ouverture laisse un identifiant sans nom : le dire vaut mieux
    /// qu'afficher un numéro ou une case vide, qui laisserait croire à un oubli.
    /// </summary>
    private string NameOfUser(int id) =>
        _users.FirstOrDefault(user => user.Id == id)?.Name ?? $"#{id} (supprimé)";

    private void OnStatusChanged(ChangeEventArgs args)
    {
        if (_problem is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _problem.Status = (ProblemStatus)value;
    }

    private void OnUrgencyChanged(ChangeEventArgs args)
    {
        if (_problem is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _problem.Urgency = (ItilLevel)value;
        RecomputePriority();
    }

    private void OnImpactChanged(ChangeEventArgs args)
    {
        if (_problem is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _problem.Impact = (ItilLevel)value;
        RecomputePriority();
    }

    /// <summary>
    /// Toucher la priorité à la main la détache de la matrice : sans ce marqueur, le premier
    /// changement d'urgence l'écraserait sans prévenir.
    /// </summary>
    private void OnPriorityChanged(ChangeEventArgs args)
    {
        if (_problem is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _problem.Priority = (ItilLevel)value;
        _problem.IsPriorityManual = true;
    }

    private void ResetPriority()
    {
        if (_problem is null)
        {
            return;
        }

        _problem.IsPriorityManual = false;
        RecomputePriority();
    }

    private void RecomputePriority()
    {
        if (_problem is null || _problem.IsPriorityManual)
        {
            return;
        }

        _problem.Priority = ItilPriorityMatrix.Compute(_problem.Urgency, _problem.Impact);
    }

    private async Task SaveAsync()
    {
        if (_problem is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_problem.Name))
        {
            _error = Tr.T("Le titre est obligatoire.");
            _activeTab = "fiche";
            return;
        }

        _error = null;
        _isSaving = true;

        try
        {
            await using DbContext db = await DbFactory.CreateDbContextAsync();

            Problem? stored = await db.Set<Problem>().FirstOrDefaultAsync(problem => problem.Id == ProblemId);
            if (stored is null)
            {
                return;
            }

            _problem.CategoryId = _categoryId == 0 ? null : _categoryId;
            _problem.AuthorUserId = _authorId == 0 ? null : _authorId;
            _problem.AssignedUserId = _assignedUserId == 0 ? null : _assignedUserId;
            _problem.AssignedGroupId = _assignedGroupId == 0 ? null : _assignedGroupId;

            AssistanceHistoryRecorder history = new(ItemTypes.Problem, ProblemId, _currentUserName);

            history.Track("Titre", stored.Name, _problem.Name);
            history.Track("Énoncé", stored.Content, _problem.Content);
            history.Track("Statut", ProblemLabels.For(stored.Status), ProblemLabels.For(_problem.Status));
            history.Track("Urgence", ItilLabels.For(stored.Urgency), ItilLabels.For(_problem.Urgency));
            history.Track("Impact", ItilLabels.For(stored.Impact), ItilLabels.For(_problem.Impact));
            history.Track("Priorité", ItilLabels.For(stored.Priority), ItilLabels.For(_problem.Priority));
            history.Track("Catégorie", CategoryNameOf(stored.CategoryId), CategoryNameOf(_problem.CategoryId));
            history.Track("Rédacteur", ActorNameOf(stored.AuthorUserId), ActorNameOf(_problem.AuthorUserId));
            history.Track("Technicien", ActorNameOf(stored.AssignedUserId), ActorNameOf(_problem.AssignedUserId));
            history.Track("Groupe", GroupNameOf(stored.AssignedGroupId), GroupNameOf(_problem.AssignedGroupId));
            history.Track("Échéance", stored.DueDate, _problem.DueDate);
            history.Track("Symptômes", stored.SymptomContent, _problem.SymptomContent);
            history.Track("Cause", stored.CauseContent, _problem.CauseContent);
            history.Track("Conséquences", stored.ImpactContent, _problem.ImpactContent);
            history.Track("Contournement", stored.Workaround, _problem.Workaround);
            history.Track("Solution", stored.Solution, _problem.Solution);
            history.Track("Type de solution", stored.SolutionType, _problem.SolutionType);

            ApplyStatusDates(stored.Status, _problem);

            stored.Name = _problem.Name.Trim();
            stored.Content = _problem.Content;
            stored.Status = _problem.Status;
            stored.Urgency = _problem.Urgency;
            stored.Impact = _problem.Impact;
            stored.Priority = _problem.Priority;
            stored.IsPriorityManual = _problem.IsPriorityManual;
            stored.CategoryId = _problem.CategoryId;
            stored.AuthorUserId = _problem.AuthorUserId;
            stored.AssignedUserId = _problem.AssignedUserId;
            stored.AssignedGroupId = _problem.AssignedGroupId;
            stored.DueDate = _problem.DueDate;
            stored.SymptomContent = _problem.SymptomContent;
            stored.CauseContent = _problem.CauseContent;
            stored.ImpactContent = _problem.ImpactContent;
            stored.Workaround = _problem.Workaround;
            stored.Solution = _problem.Solution;
            stored.SolutionType = _problem.SolutionType;
            stored.SolvedAt = _problem.SolvedAt;
            stored.ClosedAt = _problem.ClosedAt;

            if (history.HasChanges)
            {
                stored.UpdatedAt = DateTime.UtcNow;
                db.Set<AssistanceHistoryEntry>().AddRange(history.Entries);
            }

            await db.SaveChangesAsync();

            ToastService.Notify(new ToastMessage(ToastType.Success, Tr.T("Problème enregistré.")));
            await LoadAsync();
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>
    /// Dates de résolution et de clôture, déduites du seul statut. « Sous observation » compte
    /// comme résolu de ce point de vue : la solution est posée, c'est son effet qu'on attend.
    /// </summary>
    private static void ApplyStatusDates(ProblemStatus before, Problem problem)
    {
        if (problem.Status == before)
        {
            return;
        }

        problem.SolvedAt = problem.Status switch
        {
            ProblemStatus.Solved or ProblemStatus.Observed or ProblemStatus.Closed
                => problem.SolvedAt ?? DateTime.UtcNow,

            // Retour à un statut d'analyse : le problème n'est plus résolu, sa date non plus.
            _ => null,
        };

        problem.ClosedAt = problem.Status == ProblemStatus.Closed ? problem.ClosedAt ?? DateTime.UtcNow : null;
    }

    private string? CategoryNameOf(int? categoryId) =>
        categoryId is { } id ? _categories.FirstOrDefault(category => category.Id == id)?.Name : null;

    private string? ActorNameOf(int? userId) => userId is { } id ? NameOfUser(id) : null;

    private string? GroupNameOf(int? groupId) =>
        groupId is { } id ? _groups.FirstOrDefault(group => group.Id == id)?.Name ?? $"#{id} (supprimé)" : null;

    private async Task AttachTicketAsync()
    {
        if (_ticketToAttach == 0)
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        Ticket? ticket = await db.Set<Ticket>()
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Id == _ticketToAttach);

        db.Set<ProblemTicket>().Add(new ProblemTicket
        {
            ProblemId = ProblemId,
            TicketId = _ticketToAttach,
        });

        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Problem,
            ItemId = ProblemId,
            User = _currentUserName,
            Field = "Incident",
            Description = $"Rattachement de l'incident #{_ticketToAttach}"
                + (ticket is null ? string.Empty : $" « {ticket.Name} »"),
        });

        await TouchAsync(db);
        await db.SaveChangesAsync();

        _ticketToAttach = 0;
        await LoadAsync();
    }

    private async Task DetachTicketAsync(int ticketId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ProblemTicket? link = await db.Set<ProblemTicket>()
            .FirstOrDefaultAsync(entry => entry.ProblemId == ProblemId && entry.TicketId == ticketId);

        if (link is null)
        {
            return;
        }

        db.Set<ProblemTicket>().Remove(link);

        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Problem,
            ItemId = ProblemId,
            User = _currentUserName,
            Field = "Incident",
            Description = $"Détachement de l'incident #{ticketId}",
        });

        await TouchAsync(db);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task AddFollowupAsync()
    {
        if (_problem is null || string.IsNullOrWhiteSpace(_newFollowup))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        db.Set<ItilFollowup>().Add(new ItilFollowup
        {
            ItemType = ItemTypes.Problem,
            ItemId = ProblemId,
            Content = _newFollowup.Trim(),
            AuthorName = _currentUserName,
            IsPrivate = _newFollowupPrivate,
        });

        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Problem,
            ItemId = ProblemId,
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
                entry.Id == followupId && entry.ItemType == ItemTypes.Problem && entry.ItemId == ProblemId);

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
        if (_problem is null || string.IsNullOrWhiteSpace(_newTask.Content))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        db.Set<ItilTask>().Add(new ItilTask
        {
            ItemType = ItemTypes.Problem,
            ItemId = ProblemId,
            Content = _newTask.Content.Trim(),
            AssignedUserId = _newTaskUserId == 0 ? null : _newTaskUserId,
            PlannedStart = _newTask.PlannedStart,
            AuthorName = _currentUserName,
        });

        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Problem,
            ItemId = ProblemId,
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
                entry.Id == taskId && entry.ItemType == ItemTypes.Problem && entry.ItemId == ProblemId);

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
                entry.Id == taskId && entry.ItemType == ItemTypes.Problem && entry.ItemId == ProblemId);

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
        if (_problem is null || string.IsNullOrWhiteSpace(_problem.Solution))
        {
            return;
        }

        _problem.Status = ProblemStatus.Solved;
        await SaveAsync();
    }

    /// <summary>
    /// Enregistre la solution sans clore l'analyse : l'étape où l'on attend de voir si les
    /// incidents cessent. C'est ce que « Sous observation » veut dire dans GLPI.
    /// </summary>
    private async Task ObserveAsync()
    {
        if (_problem is null || string.IsNullOrWhiteSpace(_problem.Solution))
        {
            return;
        }

        _problem.Status = ProblemStatus.Observed;
        await SaveAsync();
    }

    private async Task CloseAsync()
    {
        if (_problem is null)
        {
            return;
        }

        _problem.Status = ProblemStatus.Closed;
        await SaveAsync();
    }

    /// <summary>
    /// Rouvre un problème résolu ou clos. Le statut revient à « Accepté » s'il y a un technicien,
    /// sinon à « Nouveau » : un problème rouvert sans personne dessus n'est pas en analyse.
    /// </summary>
    private async Task ReopenAsync()
    {
        if (_problem is null)
        {
            return;
        }

        _problem.Status = _problem.AssignedUserId is not null || _assignedUserId != 0
            ? ProblemStatus.Assigned
            : ProblemStatus.New;

        _activeTab = "fiche";
        await SaveAsync();
    }

    /// <summary>
    /// Marque le problème comme modifié depuis un geste qui ne passe pas par l'enregistrement de la
    /// fiche (suivi, tâche, rattachement), pour que la date de dernière activité reste vraie.
    /// </summary>
    private async Task TouchAsync(DbContext db)
    {
        Problem? stored = await db.Set<Problem>().FirstOrDefaultAsync(problem => problem.Id == ProblemId);

        if (stored is not null)
        {
            stored.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task DeleteAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        Problem? stored = await db.Set<Problem>().FirstOrDefaultAsync(problem => problem.Id == ProblemId);
        if (stored is null)
        {
            return;
        }

        // Suivis, tâches et historique sont polymorphes, donc sans clé étrangère : ils partent ici.
        // Le rattachement aux incidents part en cascade, mais les incidents restent : un problème
        // abandonné ne doit pas emporter ceux qu'il expliquait.
        db.Set<ItilFollowup>().RemoveRange(await db.Set<ItilFollowup>()
            .Where(entry => entry.ItemType == ItemTypes.Problem && entry.ItemId == ProblemId)
            .ToListAsync());

        db.Set<ItilTask>().RemoveRange(await db.Set<ItilTask>()
            .Where(entry => entry.ItemType == ItemTypes.Problem && entry.ItemId == ProblemId)
            .ToListAsync());

        db.Set<AssistanceHistoryEntry>().RemoveRange(await db.Set<AssistanceHistoryEntry>()
            .Where(entry => entry.ItemType == ItemTypes.Problem && entry.ItemId == ProblemId)
            .ToListAsync());

        db.Set<Problem>().Remove(stored);
        await db.SaveChangesAsync();

        Navigation.NavigateTo("/assistance/problems");
    }

    private static ItilTask NewBlankTask() => new() { ItemType = ItemTypes.Problem, Content = string.Empty };
}
