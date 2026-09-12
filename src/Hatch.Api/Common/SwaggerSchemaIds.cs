namespace Hatch.Api.Common;

/// <summary>
/// Names the schemas in the OpenAPI document. Swashbuckle keys them on the bare
/// type name by default, which two modules cannot both honour: Storage has an
/// <c>ItemDto</c> and so does Gather, and a collision doesn't degrade the
/// document - it throws, and <c>/swagger</c> serves a stack trace instead of an
/// API.
/// </summary>
/// <remarks>
/// So a module's types carry their module: <c>StorageItemDto</c>,
/// <c>GatherItemDto</c>. That is a property of the platform rather than a
/// naming rule each module has to remember, which matters because the failure
/// lands on whoever adds the *second* module with the name - and it takes out
/// Swagger for the whole app, not just theirs. Everything outside
/// <c>Modules/</c> keeps the plain name it already has.
/// </remarks>
public static class SwaggerSchemaIds
{
    private const string ModulesNamespace = "Hatch.Api.Modules.";

    public static string For(Type type)
    {
        var prefix = type.Namespace?.StartsWith(ModulesNamespace, StringComparison.Ordinal) == true
            ? type.Namespace[ModulesNamespace.Length..].Split('.')[0]
            : "";

        return prefix + Name(type);
    }

    /// <summary>
    /// The type's own name, spelling out generic arguments the way Swashbuckle's
    /// default does - taking over schema naming means taking over all of it, and
    /// a bare <c>Result`1</c> would collide with every other instantiation.
    /// </summary>
    private static string Name(Type type)
    {
        if (!type.IsGenericType) return type.Name;

        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}Of{string.Join("And", type.GetGenericArguments().Select(For))}";
    }
}
