using Huishoudplanner.Adapters.Http.Transfer;
using Huishoudplanner.Adapters.Mongo.Transfer;
using Huishoudplanner.Application.Transfer;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Transfer;

/// <summary>The wiring of the JSON export and import slice: the use case, the store of the whole dataset and the endpoints. The ledger rebuild after an import goes through the points service.</summary>
public static class TransferComposition
{
    public static IServiceCollection AddTransfer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoTransfer();
        services.AddScoped<ITransferService, TransferService>();
        return services;
    }

    public static IEndpointRouteBuilder MapTransfer(this IEndpointRouteBuilder routes) => routes.MapTransferEndpoints();
}
