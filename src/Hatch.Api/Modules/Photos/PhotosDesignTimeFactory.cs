namespace Hatch.Api.Modules.Photos;

/// <summary>Lets `dotnet ef --context PhotosContext` build the model without running Program.cs.</summary>
public class PhotosDesignTimeFactory : ModuleDesignTimeFactory<PhotosContext>
{
    protected override string Schema => PhotosContext.Schema;
}
