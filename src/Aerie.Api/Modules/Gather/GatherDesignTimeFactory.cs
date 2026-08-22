namespace Aerie.Api.Modules.Gather;

/// <summary>Lets `dotnet ef --context GatherContext` build the model without running Program.cs.</summary>
public class GatherDesignTimeFactory : ModuleDesignTimeFactory<GatherContext>
{
    protected override string Schema => GatherContext.Schema;
}
