namespace Hatch.Api.Modules.Hatch;

/// <summary>Lets `dotnet ef --context HatchContext` build the model without running Program.cs.</summary>
public class HatchDesignTimeFactory : ModuleDesignTimeFactory<HatchContext>
{
    protected override string Schema => HatchContext.Schema;
}
