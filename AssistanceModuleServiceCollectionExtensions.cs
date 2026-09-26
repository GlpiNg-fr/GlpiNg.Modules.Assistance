using GlpiNg.Modules.Abstractions.Cron;
using GlpiNg.Modules.Abstractions.Menu;
using GlpiNg.Modules.Assistance.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GlpiNg.Modules.Assistance;

/// <summary>
/// Point d'enregistrement du module Assistance dans le conteneur DI de l'hôte, même principe que
/// <c>ManagementModuleServiceCollectionExtensions.AddManagementModule</c>.
///
/// Ses pages lisent et écrivent par le <c>DbContext</c> de base, et empruntent à l'hôte l'annuaire
/// des acteurs, les documents, les notes et la publication des notifications — tous par leurs
/// contrats. Ses pages Razor vivant dans une autre assembly, elles doivent être déclarées au
/// routeur (Routes.razor) et au point de terminaison (AddAdditionalAssemblies dans Program.cs).
/// </summary>
public static class AssistanceModuleServiceCollectionExtensions
{
    public static IServiceCollection AddAssistanceModule(this IServiceCollection services)
    {
        services.AddSingleton<IMenuProvider, AssistanceMenuProvider>();

        // Pose les échéances des niveaux de service sur les tickets. Scoped parce qu'il consomme
        // la fabrique de DbContext, elle-même Scoped.
        services.AddScoped<ServiceLevelService>();

        // Applique les niveaux d'escalade au fil du temps. Comme tout ICronTask, il est exécuté
        // par le service cron de l'hôte et réglable depuis /config/automatic-actions.
        services.AddScoped<ICronTask, ServiceLevelEscalationCronTask>();

        return services;
    }
}
