using Azure.Core.Serialization;
using Microsoft.Azure.Cosmos;
using System.IO;
using System.Text.Json;

namespace CosmosJsonSerializer
{
    public class CosmosJsonSerializer : CosmosSerializer
    {
        private readonly JsonObjectSerializer systemTextJsonSerializer;

        public CosmosJsonSerializer(JsonSerializerOptions jsonSerializerOptions)
        {
            systemTextJsonSerializer = new JsonObjectSerializer(jsonSerializerOptions);
        }

        public override T FromStream<T>(Stream stream)
        {
            using (stream)
            {
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8603 // Possible null reference return.
                if (stream.CanSeek && stream.Length == 0)
                {
                    return default;
                }

                if (typeof(Stream).IsAssignableFrom(typeof(T)))
                {
                    return (T)(object)stream;
                }

                return (T)systemTextJsonSerializer.Deserialize(stream, typeof(T), default);
#pragma warning restore CS8603 // Possible null reference return.
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
            }
        }

        public override Stream ToStream<T>(T input)
        {
            MemoryStream streamPayload = new();
            systemTextJsonSerializer.Serialize(streamPayload, input, typeof(T), default);
            streamPayload.Position = 0;
            return streamPayload;
        }
    }
}
