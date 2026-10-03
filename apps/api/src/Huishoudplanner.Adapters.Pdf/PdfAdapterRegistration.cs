using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Huishoudplanner.Adapters.Pdf;

public static class PdfAdapterRegistration
{
    /// <summary>Registers the QuestPDF implementation of <see cref="ForRenderingSheets"/>. It needs a <see cref="TimeProvider"/> in the container.</summary>
    public static IServiceCollection AddPdfAdapter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ForRenderingSheets>(sp => new QuestPdfSheetRenderer(sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
