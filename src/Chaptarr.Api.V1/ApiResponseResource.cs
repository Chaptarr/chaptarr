using Newtonsoft.Json;
using SystemTextJsonIgnoreAttribute = System.Text.Json.Serialization.JsonIgnoreAttribute;
using SystemTextJsonIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Chaptarr.Api.V1
{
    public class ApiMessageResource
    {
        public string Message { get; set; }
    }

    public class ApiErrorResource
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public string Error { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public string Message { get; set; }
    }

    public class ApiSuccessResource
    {
        public bool Success { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public string Message { get; set; }
    }
}
