using GlpiNg.Modules.Abstractions.Menu;
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

        return services;
    }
}
