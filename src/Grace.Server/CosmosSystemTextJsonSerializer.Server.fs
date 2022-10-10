namespace Grace.Server

open Azure.Core.Serialization
open Microsoft.Azure.Cosmos
open System
open System.IO
open System.Text.Json
open System.Threading

module CosmosSystemTextJsonSerializer =
    let x = 0


    //type Serializer(jsonSerializerOptions) =
    //    inherit CosmosSerializer()

    //    let systemTextJsonSerializer = JsonObjectSerializer(jsonSerializerOptions)

        //override this.FromStream<'T> (stream: Stream) : 'T =
        //    try
        //        if stream.CanSeek && stream.Length = 0 then
        //            Object :?> 'T
                    
        //        if typeof<Stream>.IsAssignableFrom(typeof<'T>) then
        //            stream :> Object :?> 'T

        //        let cancellationToken = CancellationToken()
        //        systemTextJsonSerializer.Deserialize(stream, typeof<'T>, cancellationToken) :?> 'T

        //    finally
        //        if not <| isNull(stream) then
        //            stream.Dispose()
