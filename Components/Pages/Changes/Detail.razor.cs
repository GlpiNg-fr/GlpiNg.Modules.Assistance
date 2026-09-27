using BlazorBootstrap;
using GlpiNg.Modules.Abstractions.Directory;
using GlpiNg.Modules.Abstractions.Items;
using GlpiNg.Modules.Abstractions.Notifications;
using GlpiNg.Modules.Assistance.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using GlpiNg.Modules.Abstractions.Localization;

namespace GlpiNg.Modules.Assistance.Components.Pages.Changes;

public partial class Detail : ComponentBase
{
    [Parameter]
    public int ChangeId { get; set; }

    [Inject]
    private IDbContextFactory<DbContext> DbFactory { get; set; } = null!;

    [Inject]
    private IPrincipalDirectory Directory { get; set; } = null!;

    [Inject]
    private INotificationPublisher Notifications { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Inject]
    private ToastService ToastService { get; set; } = null!;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthStateTask { get; set; }

    private Change? _change;
    private List<TicketCategory> _categories = [];
    private List<ItilFollowup> _followups = [];
    private List<ItilTask> _tasks = [];
    private List<ChangeValidation> _validations = [];
    private List<Ticket> _tickets = [];
    private List<Ticket> _ticketCandidates = [];
    private List<Problem> _problems = [];
    private List<Problem> _problemCandidates = [];
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
    private int _problemToAttach;
    private int _validatorToRequest;

    private string _validationRequestComment = string.Empty;

    /// <summary>Réponse en cours de saisie, par approbation.</summary>
    private readonly Dictionary<int, string> _answers = [];

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
    private int? _currentUserId;

    /// <summary>Redemander l'avis de quelqu'un qui ne l'a pas encore donné ferait doublon.</summary>
    private bool _validatorAlreadyWaiting => _validatorToRequest != 0
        && _validations.Exists(validation => validation.ValidatorUserId == _validatorToRequest
            && validation.Status == ChangeValidationStatus.Waiting);

    private IEnumerable<(string Key, string Label, string Icon, int? Count)> Tabs
    {
        get
        {
            yield return ("fiche", "Fiche", "ti-replace", null);
            yield return ("analyse", "Analyse", "ti-zoom-question", null);
            yield return ("plans", "Plans", "ti-list-check", null);
            yield return ("approbations", "Approbations", "ti-gavel", _validations.Count);
            yield return ("tickets", "Tickets", "ti-ticket", _tickets.Count);
            yield return ("problemes", "Problèmes", "ti-bulb", _problems.Count);
            yield return ("suivis", "Suivis", "ti-message", _followups.Count);
            yield return ("taches", "Tâches", "ti-checklist", _tasks.Count);
            yield return ("solution", "Solution", "ti-circle-check", null);
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

            string? claim = authState.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            _currentUserId = int.TryParse(claim, out int userId) ? userId : null;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        _change = await db.Set<Change>()
            .AsNoTracking()
            .FirstOrDefaultAsync(change => change.Id == ChangeId);

        if (_change is null)
        {
            return;
        }

        _categoryId = _change.CategoryId ?? 0;
        _authorId = _change.AuthorUserId ?? 0;
        _assignedUserId = _change.AssignedUserId ?? 0;
        _assignedGroupId = _change.AssignedGroupId ?? 0;

        _followups = await db.Set<ItilFollowup>()
            .AsNoTracking()
            .Where(entry => entry.ItemType == ItemTypes.Change && entry.ItemId == ChangeId)
            .OrderByDescending(entry => entry.CreatedAt)
            .ToListAsync();

        _tasks = await db.Set<ItilTask>()
            .AsNoTracking()
            .Where(entry => entry.ItemType == ItemTypes.Change && entry.ItemId == ChangeId)
            .OrderBy(entry => entry.State)
            .ThenBy(entry => entry.PlannedStart)
            .ToListAsync();

        _validations = await db.Set<ChangeValidation>()
            .AsNoTracking()
            .Where(validation => validation.ChangeId == ChangeId)
            .OrderByDescending(validation => validation.SubmittedAt)
            .ToListAsync();

        _tickets = await db.Set<ChangeTicket>()
            .AsNoTracking()
            .Where(link => link.ChangeId == ChangeId)
            .Select(link => link.Ticket!)
            .OrderByDescending(ticket => ticket.OpenedAt)
            .ToListAsync();

        // Seuls les objets pas encore rattachés sont proposés : l'index unique refuserait le doublon.
        List<int> attachedTickets = [.. _tickets.Select(ticket => ticket.Id)];

        _ticketCandidates = await db.Set<Ticket>()
            .AsNoTracking()
            .Where(ticket => !attachedTickets.Contains(ticket.Id))
            .OrderByDescending(ticket => ticket.OpenedAt)
            .ToListAsync();

        _problems = await db.Set<ChangeProblem>()
            .AsNoTracking()
            .Where(link => link.ChangeId == ChangeId)
            .Select(link => link.Problem!)
            .OrderByDescending(problem => problem.OpenedAt)
            .ToListAsync();

        List<int> attachedProblems = [.. _problems.Select(problem => problem.Id)];

        _problemCandidates = await db.Set<Problem>()
            .AsNoTracking()
            .Where(problem => !attachedProblems.Contains(problem.Id))
            .OrderByDescending(problem => problem.OpenedAt)
            .ToListAsync();

        _categories = await db.Set<TicketCategory>()
            .AsNoTracking()
            .Include(category => category.Parent)
            .Where(category => category.IsActive)
            .OrderBy(category => category.Name)
            .ToListAsync();

        _history = await db.Set<AssistanceHistoryEntry>()
            .AsNoTracking()
            .Where(entry => entry.ItemType == ItemTypes.Change && entry.ItemId == ChangeId)
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
        if (_change is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _change.Status = (ChangeStatus)value;
    }

    private void OnUrgencyChanged(ChangeEventArgs args)
    {
        if (_change is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _change.Urgency = (ItilLevel)value;
        RecomputePriority();
    }

    private void OnImpactChanged(ChangeEventArgs args)
    {
        if (_change is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _change.Impact = (ItilLevel)value;
        RecomputePriority();
    }

    /// <summary>
    /// Toucher la priorité à la main la détache de la matrice : sans ce marqueur, le premier
    /// changement d'urgence l'écraserait sans prévenir.
    /// </summary>
    private void OnPriorityChanged(ChangeEventArgs args)
    {
        if (_change is null || !int.TryParse(args.Value?.ToString(), out int value))
        {
            return;
        }

        _change.Priority = (ItilLevel)value;
        _change.IsPriorityManual = true;
    }

    private void ResetPriority()
    {
        if (_change is null)
        {
            return;
        }

        _change.IsPriorityManual = false;
        RecomputePriority();
    }

    private void RecomputePriority()
    {
        if (_change is null || _change.IsPriorityManual)
        {
            return;
        }

        _change.Priority = ItilPriorityMatrix.Compute(_change.Urgency, _change.Impact);
    }

    private async Task SaveAsync()
    {
        if (_change is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_change.Name))
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

            Change? stored = await db.Set<Change>().FirstOrDefaultAsync(change => change.Id == ChangeId);
            if (stored is null)
            {
                return;
            }

            _change.CategoryId = _categoryId == 0 ? null : _categoryId;
            _change.AuthorUserId = _authorId == 0 ? null : _authorId;
            _change.AssignedUserId = _assignedUserId == 0 ? null : _assignedUserId;
            _change.AssignedGroupId = _assignedGroupId == 0 ? null : _assignedGroupId;

            AssistanceHistoryRecorder history = new(ItemTypes.Change, ChangeId, _currentUserName);

            history.Track("Titre", stored.Name, _change.Name);
            history.Track("Description", stored.Content, _change.Content);
            history.Track("Statut", ChangeLabels.For(stored.Status), ChangeLabels.For(_change.Status));
            history.Track("Urgence", ItilLabels.For(stored.Urgency), ItilLabels.For(_change.Urgency));
            history.Track("Impact", ItilLabels.For(stored.Impact), ItilLabels.For(_change.Impact));
            history.Track("Priorité", ItilLabels.For(stored.Priority), ItilLabels.For(_change.Priority));
            history.Track("Catégorie", CategoryNameOf(stored.CategoryId), CategoryNameOf(_change.CategoryId));
            history.Track("Rédacteur", ActorNameOf(stored.AuthorUserId), ActorNameOf(_change.AuthorUserId));
            history.Track("Technicien", ActorNameOf(stored.AssignedUserId), ActorNameOf(_change.AssignedUserId));
            history.Track("Groupe", GroupNameOf(stored.AssignedGroupId), GroupNameOf(_change.AssignedGroupId));
            history.Track("Échéance", stored.DueDate, _change.DueDate);
            history.Track("Impacts", stored.ImpactContent, _change.ImpactContent);
            history.Track("Liste de contrôle", stored.ControlListContent, _change.ControlListContent);
            history.Track("Plan de déploiement", stored.RolloutPlanContent, _change.RolloutPlanContent);
            history.Track("Plan de retour arrière", stored.BackoutPlanContent, _change.BackoutPlanContent);
            history.Track("Checklist", stored.ChecklistContent, _change.ChecklistContent);
            history.Track("Solution", stored.Solution, _change.Solution);
            history.Track("Type de solution", stored.SolutionType, _change.SolutionType);

            ApplyStatusDates(stored.Status, _change);

            stored.Name = _change.Name.Trim();
            stored.Content = _change.Content;
            stored.Status = _change.Status;
            stored.Urgency = _change.Urgency;
            stored.Impact = _change.Impact;
            stored.Priority = _change.Priority;
            stored.IsPriorityManual = _change.IsPriorityManual;
            stored.CategoryId = _change.CategoryId;
            stored.AuthorUserId = _change.AuthorUserId;
            stored.AssignedUserId = _change.AssignedUserId;
            stored.AssignedGroupId = _change.AssignedGroupId;
            stored.DueDate = _change.DueDate;
            stored.ImpactContent = _change.ImpactContent;
            stored.ControlListContent = _change.ControlListContent;
            stored.RolloutPlanContent = _change.RolloutPlanContent;
            stored.BackoutPlanContent = _change.BackoutPlanContent;
            stored.ChecklistContent = _change.ChecklistContent;
            stored.Solution = _change.Solution;
            stored.SolutionType = _change.SolutionType;
            stored.SolvedAt = _change.SolvedAt;
            stored.ClosedAt = _change.ClosedAt;

            // L'état global des approbations ne se saisit pas : il est tenu par les approbations
            // elles-mêmes (voir RefreshGlobalValidationAsync), et l'écraser ici avec la copie
            // chargée à l'ouverture de la fiche perdrait une réponse arrivée entre-temps.

            if (history.HasChanges)
            {
                stored.UpdatedAt = DateTime.UtcNow;
                db.Set<AssistanceHistoryEntry>().AddRange(history.Entries);
            }

            await db.SaveChangesAsync();

            ToastService.Notify(new ToastMessage(ToastType.Success, Tr.T("Changement enregistré.")));
            await LoadAsync();
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>
    /// Dates d'application et de clôture, déduites du seul statut. « Revue » compte comme appliqué :
    /// le changement est en place, c'est son effet qu'on vérifie. « Annulé » clôt sans appliquer.
    /// </summary>
    private static void ApplyStatusDates(ChangeStatus before, Change change)
    {
        if (change.Status == before)
        {
            return;
        }

        change.SolvedAt = change.Status switch
        {
            ChangeStatus.Solved or ChangeStatus.Observed or ChangeStatus.Closed
                => change.SolvedAt ?? DateTime.UtcNow,

            // Retour à un statut de préparation, ou annulation : rien n'est appliqué.
            _ => null,
        };

        change.ClosedAt = change.Status is ChangeStatus.Closed or ChangeStatus.Canceled
            ? change.ClosedAt ?? DateTime.UtcNow
            : null;
    }

    private string? CategoryNameOf(int? categoryId) =>
        categoryId is { } id ? _categories.FirstOrDefault(category => category.Id == id)?.Name : null;

    private string? ActorNameOf(int? userId) => userId is { } id ? NameOfUser(id) : null;

    private string? GroupNameOf(int? groupId) =>
        groupId is { } id ? _groups.FirstOrDefault(group => group.Id == id)?.Name ?? $"#{id} (supprimé)" : null;

    // ---- Approbations ------------------------------------------------------------------------

    private async Task RequestValidationAsync()
    {
        if (_change is null || _validatorToRequest == 0 || _validatorAlreadyWaiting)
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        string? comment = string.IsNullOrWhiteSpace(_validationRequestComment) ? null : _validationRequestComment.Trim();
        string validatorName = NameOfUser(_validatorToRequest);

        db.Set<ChangeValidation>().Add(new ChangeValidation
        {
            ChangeId = ChangeId,
            ValidatorUserId = _validatorToRequest,
            RequestComment = comment,
            RequesterName = _currentUserName,
        });

        AddHistory(db, "Approbation", $"Demande d'approbation à {validatorName}");

        Change? stored = await db.Set<Change>().FirstOrDefaultAsync(change => change.Id == ChangeId);
        if (stored is not null)
        {
            // Demander une approbation, c'est soumettre le changement : il passe en « Approbation »
            // s'il en était encore à sa préparation. Plus loin dans le cycle, on ne le fait pas
            // reculer — une approbation complémentaire ne remet pas en cause ce qui est engagé.
            if (stored.Status is ChangeStatus.New or ChangeStatus.Evaluation)
            {
                AddHistory(db, "Statut", $"« {ChangeLabels.For(stored.Status)} » → « {ChangeLabels.For(ChangeStatus.Approval)} »");
                stored.Status = ChangeStatus.Approval;
            }

            stored.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        await RefreshGlobalValidationAsync(db);

        await Notifications.PublishAsync(ItemTypes.Change, "validation", ChangeId, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["change.id"] = ChangeId.ToString(),
            ["change.title"] = _change.Name,
            ["validation.validator"] = validatorName,
            ["validation.requester"] = _currentUserName,
            ["validation.comment"] = comment,
            ["change.url"] = $"/assistance/changes/{ChangeId}",
        });

        _validatorToRequest = 0;
        _validationRequestComment = string.Empty;

        await LoadAsync();
    }

    private async Task AnswerValidationAsync(int validationId, ChangeValidationStatus answer)
    {
        if (_change is null || answer is not (ChangeValidationStatus.Accepted or ChangeValidationStatus.Refused))
        {
            return;
        }

        string? comment = _answers.GetValueOrDefault(validationId)?.Trim();

        // Un refus sans raison laisse le rédacteur sans rien pour corriger son changement.
        if (answer == ChangeValidationStatus.Refused && string.IsNullOrWhiteSpace(comment))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ChangeValidation? validation = await db.Set<ChangeValidation>()
            .FirstOrDefaultAsync(entry => entry.Id == validationId && entry.ChangeId == ChangeId);

        // Revérifié côté serveur, et pas seulement par l'affichage des boutons : seul l'approbateur
        // désigné répond, et une seule fois.
        if (validation is null
            || validation.Status != ChangeValidationStatus.Waiting
            || validation.ValidatorUserId != _currentUserId)
        {
            return;
        }

        validation.Status = answer;
        validation.ValidationComment = string.IsNullOrWhiteSpace(comment) ? null : comment;
        validation.ValidatedAt = DateTime.UtcNow;

        AddHistory(db, "Approbation", $"{ChangeLabels.For(answer)} par {_currentUserName}");

        await db.SaveChangesAsync();
        ChangeValidationStatus global = await RefreshGlobalValidationAsync(db);

        // Tous d'accord : le changement soumis devient « Accepté », prêt à être préparé. Un refus,
        // lui, ne décide rien du statut à la place du rédacteur, qui choisira de revoir ou d'annuler.
        if (global == ChangeValidationStatus.Accepted)
        {
            Change? stored = await db.Set<Change>().FirstOrDefaultAsync(change => change.Id == ChangeId);
            if (stored is { Status: ChangeStatus.Approval })
            {
                AddHistory(db, "Statut", $"« {ChangeLabels.For(ChangeStatus.Approval)} » → « {ChangeLabels.For(ChangeStatus.Accepted)} »");
                stored.Status = ChangeStatus.Accepted;
                await db.SaveChangesAsync();
            }
        }

        await Notifications.PublishAsync(ItemTypes.Change, "validation_answer", ChangeId, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["change.id"] = ChangeId.ToString(),
            ["change.title"] = _change.Name,
            ["validation.validator"] = _currentUserName,
            ["validation.status"] = ChangeLabels.For(answer),
            ["validation.answer"] = validation.ValidationComment,
            ["change.url"] = $"/assistance/changes/{ChangeId}",
        });

        _answers.Remove(validationId);
        await LoadAsync();
    }

    /// <summary>Retire une demande restée sans réponse : un approbateur parti ou sollicité par erreur.</summary>
    private async Task DeleteValidationAsync(int validationId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ChangeValidation? validation = await db.Set<ChangeValidation>()
            .FirstOrDefaultAsync(entry => entry.Id == validationId
                && entry.ChangeId == ChangeId
                && entry.Status == ChangeValidationStatus.Waiting);

        if (validation is null)
        {
            return;
        }

        db.Set<ChangeValidation>().Remove(validation);
        AddHistory(db, "Approbation", $"Retrait de la demande d'approbation à {NameOfUser(validation.ValidatorUserId)}");

        await db.SaveChangesAsync();
        await RefreshGlobalValidationAsync(db);

        await LoadAsync();
    }

    /// <summary>
    /// Recalcule l'état global à partir des approbations en base et le stocke sur le changement,
    /// d'où la liste le lit pour filtrer. Seul endroit qui l'écrit.
    /// </summary>
    private async Task<ChangeValidationStatus> RefreshGlobalValidationAsync(DbContext db)
    {
        List<ChangeValidationStatus> statuses = await db.Set<ChangeValidation>()
            .Where(validation => validation.ChangeId == ChangeId)
            .Select(validation => validation.Status)
            .ToListAsync();

        ChangeValidationStatus global = ChangeValidationRules.Global(statuses);

        Change? stored = await db.Set<Change>().FirstOrDefaultAsync(change => change.Id == ChangeId);
        if (stored is not null && stored.GlobalValidation != global)
        {
            AddHistory(db, "Approbation globale", $"« {ChangeLabels.For(stored.GlobalValidation)} » → « {ChangeLabels.For(global)} »");
            stored.GlobalValidation = global;
            await db.SaveChangesAsync();
        }

        return global;
    }

    // ---- Rattachements ------------------------------------------------------------------------

    private async Task AttachTicketAsync()
    {
        if (_ticketToAttach == 0)
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        Ticket? ticket = await db.Set<Ticket>().AsNoTracking().FirstOrDefaultAsync(entry => entry.Id == _ticketToAttach);

        db.Set<ChangeTicket>().Add(new ChangeTicket { ChangeId = ChangeId, TicketId = _ticketToAttach });

        AddHistory(db, "Ticket", $"Rattachement du ticket #{_ticketToAttach}"
            + (ticket is null ? string.Empty : $" « {ticket.Name} »"));

        await TouchAsync(db);
        await db.SaveChangesAsync();

        _ticketToAttach = 0;
        await LoadAsync();
    }

    private async Task DetachTicketAsync(int ticketId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ChangeTicket? link = await db.Set<ChangeTicket>()
            .FirstOrDefaultAsync(entry => entry.ChangeId == ChangeId && entry.TicketId == ticketId);

        if (link is null)
        {
            return;
        }

        db.Set<ChangeTicket>().Remove(link);
        AddHistory(db, "Ticket", $"Détachement du ticket #{ticketId}");

        await TouchAsync(db);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    private async Task AttachProblemAsync()
    {
        if (_problemToAttach == 0)
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        Problem? problem = await db.Set<Problem>().AsNoTracking().FirstOrDefaultAsync(entry => entry.Id == _problemToAttach);

        db.Set<ChangeProblem>().Add(new ChangeProblem { ChangeId = ChangeId, ProblemId = _problemToAttach });

        AddHistory(db, "Problème", $"Rattachement du problème #{_problemToAttach}"
            + (problem is null ? string.Empty : $" « {problem.Name} »"));

        await TouchAsync(db);
        await db.SaveChangesAsync();

        _problemToAttach = 0;
        await LoadAsync();
    }

    private async Task DetachProblemAsync(int problemId)
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        ChangeProblem? link = await db.Set<ChangeProblem>()
            .FirstOrDefaultAsync(entry => entry.ChangeId == ChangeId && entry.ProblemId == problemId);

        if (link is null)
        {
            return;
        }

        db.Set<ChangeProblem>().Remove(link);
        AddHistory(db, "Problème", $"Détachement du problème #{problemId}");

        await TouchAsync(db);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    // ---- Suivis et tâches ---------------------------------------------------------------------

    private async Task AddFollowupAsync()
    {
        if (_change is null || string.IsNullOrWhiteSpace(_newFollowup))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        db.Set<ItilFollowup>().Add(new ItilFollowup
        {
            ItemType = ItemTypes.Change,
            ItemId = ChangeId,
            Content = _newFollowup.Trim(),
            AuthorName = _currentUserName,
            IsPrivate = _newFollowupPrivate,
        });

        AddHistory(db, "Suivi", "Ajout d'un suivi");

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
                entry.Id == followupId && entry.ItemType == ItemTypes.Change && entry.ItemId == ChangeId);

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
        if (_change is null || string.IsNullOrWhiteSpace(_newTask.Content))
        {
            return;
        }

        await using DbContext db = await DbFactory.CreateDbContextAsync();

        db.Set<ItilTask>().Add(new ItilTask
        {
            ItemType = ItemTypes.Change,
            ItemId = ChangeId,
            Content = _newTask.Content.Trim(),
            AssignedUserId = _newTaskUserId == 0 ? null : _newTaskUserId,
            PlannedStart = _newTask.PlannedStart,
            AuthorName = _currentUserName,
        });

        AddHistory(db, "Tâche", $"Ajout de la tâche « {_newTask.Content.Trim()} »");

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
                entry.Id == taskId && entry.ItemType == ItemTypes.Change && entry.ItemId == ChangeId);

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
                entry.Id == taskId && entry.ItemType == ItemTypes.Change && entry.ItemId == ChangeId);

        if (task is null)
        {
            return;
        }

        db.Set<ItilTask>().Remove(task);
        await db.SaveChangesAsync();

        await LoadAsync();
    }

    // ---- Cycle de vie -------------------------------------------------------------------------

    private async Task ApplyAsync()
    {
        if (_change is null || string.IsNullOrWhiteSpace(_change.Solution))
        {
            return;
        }

        _change.Status = ChangeStatus.Solved;
        await SaveAsync();
    }

    /// <summary>
    /// Enregistre l'application sans clore le changement : l'étape où l'on vérifie qu'il a produit
    /// l'effet voulu. C'est ce que « Revue » veut dire dans GLPI.
    /// </summary>
    private async Task ReviewAsync()
    {
        if (_change is null || string.IsNullOrWhiteSpace(_change.Solution))
        {
            return;
        }

        _change.Status = ChangeStatus.Observed;
        await SaveAsync();
    }

    private async Task CloseAsync()
    {
        if (_change is null)
        {
            return;
        }

        _change.Status = ChangeStatus.Closed;
        await SaveAsync();
    }

    private async Task CancelAsync()
    {
        if (_change is null)
        {
            return;
        }

        _change.Status = ChangeStatus.Canceled;
        await SaveAsync();
    }

    /// <summary>
    /// Rouvre un changement appliqué, clos ou annulé. Il revient à « Accepté » s'il avait été
    /// approuvé — l'approbation reste acquise — et sinon à « Nouveau », à refaire approuver.
    /// </summary>
    private async Task ReopenAsync()
    {
        if (_change is null)
        {
            return;
        }

        _change.Status = _change.GlobalValidation == ChangeValidationStatus.Accepted
            ? ChangeStatus.Accepted
            : ChangeStatus.New;

        _activeTab = "fiche";
        await SaveAsync();
    }

    private void AddHistory(DbContext db, string field, string description) =>
        db.Set<AssistanceHistoryEntry>().Add(new AssistanceHistoryEntry
        {
            ItemType = ItemTypes.Change,
            ItemId = ChangeId,
            User = _currentUserName,
            Field = field,
            Description = description,
        });

    /// <summary>
    /// Marque le changement comme modifié depuis un geste qui ne passe pas par l'enregistrement de
    /// la fiche (suivi, tâche, rattachement), pour que la date de dernière activité reste vraie.
    /// </summary>
    private async Task TouchAsync(DbContext db)
    {
        Change? stored = await db.Set<Change>().FirstOrDefaultAsync(change => change.Id == ChangeId);

        if (stored is not null)
        {
            stored.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task DeleteAsync()
    {
        await using DbContext db = await DbFactory.CreateDbContextAsync();

        Change? stored = await db.Set<Change>().FirstOrDefaultAsync(change => change.Id == ChangeId);
        if (stored is null)
        {
            return;
        }

        // Suivis, tâches et historique sont polymorphes, donc sans clé étrangère : ils partent ici.
        // Approbations et rattachements partent en cascade ; tickets et problèmes restent.
        db.Set<ItilFollowup>().RemoveRange(await db.Set<ItilFollowup>()
            .Where(entry => entry.ItemType == ItemTypes.Change && entry.ItemId == ChangeId)
            .ToListAsync());

        db.Set<ItilTask>().RemoveRange(await db.Set<ItilTask>()
            .Where(entry => entry.ItemType == ItemTypes.Change && entry.ItemId == ChangeId)
            .ToListAsync());

        db.Set<AssistanceHistoryEntry>().RemoveRange(await db.Set<AssistanceHistoryEntry>()
            .Where(entry => entry.ItemType == ItemTypes.Change && entry.ItemId == ChangeId)
            .ToListAsync());

        db.Set<Change>().Remove(stored);
        await db.SaveChangesAsync();

        Navigation.NavigateTo("/assistance/changes");
    }

    private static ItilTask NewBlankTask() => new() { ItemType = ItemTypes.Change, Content = string.Empty };
}
