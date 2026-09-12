namespace Hatch.Api.Common;

public interface ISecrets
{
    public string? GetSecret(string key);
}

// Backed by IConfiguration so secrets can come from either .env.json (local
// dev, see Program.cs) or environment variables (e.g. HA_HOST/HA_PORT/HA_TOKEN
// injected in production) - configuration keys are case-insensitive, so either
// casing resolves the same "ha_host" style keys DeviceMappingSeeder looks up.
public class EnvSecrets(IConfiguration configuration) : ISecrets
{
    public string? GetSecret(string key) => configuration[key];
}
