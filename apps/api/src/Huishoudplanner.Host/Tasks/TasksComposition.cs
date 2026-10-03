using Huishoudplanner.Adapters.Http.Tasks;
using Huishoudplanner.Adapters.Mongo.Tasks;
using Huishoudplanner.Application.Tasks;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Tasks;

/// <summary>The wiring of the tasks slice: the use cases, the task store and the endpoints.</summary>
public static class TasksComposition
{
    public static IServiceCollection AddTasks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoTasks();
        services.AddScoped<ITaskService, TaskService>();
        return services;
    }

    public static IEndpointRouteBuilder MapTasks(this IEndpointRouteBuilder routes) => routes.MapTaskEndpoints();
}
