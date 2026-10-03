using Huishoudplanner.Adapters.Http.Export;
using Huishoudplanner.Adapters.Pdf;
using Huishoudplanner.Application.Export;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Export;

/// <summary>The wiring of the PDF sheets slice: the use case, the QuestPDF renderer and the endpoints. The data comes through the ports of the other slices.</summary>
public static class ExportComposition
{
    public static IServiceCollection AddExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPdfAdapter();
        services.AddScoped<IExportService, ExportService>();
        return services;
    }

    public static IEndpointRouteBuilder MapExport(this IEndpointRouteBuilder routes) => routes.MapExportEndpoints();
}
