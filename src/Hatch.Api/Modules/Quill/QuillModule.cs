namespace Hatch.Api.Modules.Quill;

/// <summary>
/// Everything Quill puts into DI, so the module registry in ModuleRegistration
/// stays one line per app.
///
/// No service of its own: the module is CRUD over one table, and the only thing
/// it needs that Storage and Gather did not - "who is calling" - is platform
/// (ICallerIdentity), not Quill's to own.
/// </summary>
public static class QuillModule
{
    public static IServiceCollection AddQuillModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleContext<QuillContext>(configuration, QuillContext.Schema);
        return services;
    }
}
