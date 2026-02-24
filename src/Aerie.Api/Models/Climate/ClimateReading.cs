namespace Aerie.Api.Models.Climate;

public class ClimateReading
{
    public Guid Id { get; set; }

    public required string EntityId { get; set; }
    public required DateTimeOffset Timestamp { get; set; }

    public decimal? Temperature { get; set; }
    public decimal? Humidity { get; set; }

    public decimal? DesiredTemperature { get; set; }
    public bool IsHeating { get; set; }
}
