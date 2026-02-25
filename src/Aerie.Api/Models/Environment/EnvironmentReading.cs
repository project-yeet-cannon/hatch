namespace Aerie.Api.Models.Environment;

public class EnvironmentReading
{
    public Guid Id { get; set; }

    public required string EntityId { get; set; }
    public required DateTimeOffset Timestamp { get; set; }

    public decimal? Temperature { get; set; }
    public decimal? Humidity { get; set; }

    public decimal? DesiredTemperature { get; set; }
    public bool IsHeating { get; set; }
}
