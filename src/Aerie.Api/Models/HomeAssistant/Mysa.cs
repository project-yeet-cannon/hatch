using System.Runtime.Serialization;

namespace Aerie.Api.Models.HomeAssistant;

public class MysaAttributes
{
    public int? CurrentTemperature { get; set; }
    public int? Temperature { get; set; }
    public int? CurrentHumidity { get; set; }
    public MysaActions HvacAction { get; set; }
    public string? FriendlyName { get; set; }

    public static MysaAttributes FromDictionary(Dictionary<string, object> dict)
    {
        var result = new MysaAttributes();

        if (dict.TryGetValue("current_temperature", out var obj))
        {
            result.CurrentTemperature = (int)(long)obj;
        }
        if (dict.TryGetValue("temperature", out var obj2))
        {
            result.Temperature = (int)(long)obj2;
        }
        if (dict.TryGetValue("current_humidity", out var obj3))
        {
            result.CurrentHumidity = (int)(long)obj3;
        }
        if (dict.TryGetValue("hvac_action", out var obj4))
        {
            if (Enum.TryParse<MysaActions>((string)obj4, true, out var e))
            {
                result.HvacAction = e;
            }
            else
            {
                throw new Exception($"Unrecognized MysaActions: [{obj4}]");
            }
        }
        if (dict.TryGetValue("friendly_name", out var obj5))
        {
            result.FriendlyName = (string)obj5;
        }

        return result;
    }
}

public enum MysaActions
{
    Idle,
    Heating
}
