namespace Aerie.Api.Modules.Quill;

/// <summary>Lets `dotnet ef --context QuillContext` build the model without running Program.cs.</summary>
public class QuillDesignTimeFactory : ModuleDesignTimeFactory<QuillContext>
{
    protected override string Schema => QuillContext.Schema;
}
