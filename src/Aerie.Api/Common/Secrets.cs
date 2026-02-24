namespace Aerie.Api.Common;

public interface ISecrets
{
    public string? GetSecret(string key);
}

public class EnvSecrets(IDictionary<string, string> envJson) : ISecrets
{
    public string? GetSecret(string key)
        => envJson.ContainsKey(key) ? envJson[key] : null;
}
