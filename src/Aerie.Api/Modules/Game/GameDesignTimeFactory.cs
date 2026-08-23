namespace Aerie.Api.Modules.Game;

/// <summary>Lets `dotnet ef --context GameContext` build the model without running Program.cs.</summary>
public class GameDesignTimeFactory : ModuleDesignTimeFactory<GameContext>
{
    protected override string Schema => GameContext.Schema;
}
